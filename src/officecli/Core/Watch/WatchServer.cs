// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0
//
// CONSISTENCY(watch-isolation): this file does not reference OfficeCli.Handlers, does not open files,
// does not write to disk. See CLAUDE.md "Watch Server Rules". To relax this red line,
// grep "CONSISTENCY(watch-isolation)" and review every file in the watch subsystem project-wide.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OfficeCli.Core;

/// <summary>
/// Pure SSE relay server. Never opens the document file.
/// Receives pre-rendered HTML from command processes via named pipe,
/// forwards to browsers via SSE.
/// </summary>
internal class WatchServer : IDisposable, IWatchBroadcaster
{
    private readonly string _filePath;
    private readonly string _pipeName;
    private readonly int _port;
    private readonly TcpListener _tcpListener;
    private readonly List<NetworkStream> _sseClients = new();
    private readonly object _sseLock = new();
    private CancellationTokenSource _cts = new();
    private bool _disposed;
    private DateTime _lastActivityTime = DateTime.UtcNow;
    private readonly TimeSpan _idleTimeout;

    // Shared shutdown Task so every teardown entrypoint — idle watchdog,
    // unwatch command, SIGTERM/SIGINT, Dispose — converges on a single
    // ordered sequence. Before this, idle/unwatch just called
    // _cts.Cancel() and hoped the async chain would unwind; but
    // TcpListener.AcceptTcpClientAsync on macOS under .NET 10 does NOT
    // reliably honour the cancellation token, so the main loop would
    // hang indefinitely in `await AcceptTcpClientAsync(token)` and the
    // process would ignore SIGINT for 15+ seconds (observed in
    // stress test) until something else kicked the TCP listener.
    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;

    // Host-agnostic watch state lives in WatchEngine (owns no sockets, never
    // opens the document): selection, the cached HTML/version, and marks. The
    // engine is constructed in the ctor because it needs `this` as its
    // IWatchBroadcaster sink for mark-update events.
    private readonly WatchEngine _engine;

    private const string WaitingHtml = """
        <html><head><meta charset="utf-8"><title>Watching...</title>
        <style>body{font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f5f5f5;color:#666;}
        .msg{text-align:center;}</style></head>
        <body><div class="msg"><h2>Waiting for first update...</h2><p>Run an officecli command to see the preview.</p></div></body></html>
        """;

    // SSE script content loaded from embedded resources (watch-sse-core.js + watch-overlay.js).
    // Layer 1 (sse-core) handles SSE connection, DOM updates, word diff/patch, slide ops.
    // Layer 2 (overlay) handles selection, marks, rubber-band, CSS injection.
    // Coupling: Layer 1 calls window._watchReapplyHook() after DOM mutations;
    //           Layer 2 sets that hook to reapplyDecorations().
    private static readonly Lazy<string> _sseScriptBlock = new(() =>
    {
        var core = LoadWatchResource("Resources.watch-sse-core.js");
        var overlay = LoadWatchResource("Resources.watch-overlay.js");
        return $"<script>\n{core}\n</script>\n<script>\n{overlay}\n</script>";
    });

    // Test access: allows tests to verify SSE script content without reflection on a const field.
    internal static string SseScriptContent => _sseScriptBlock.Value;

    private static string LoadWatchResource(string name)
    {
        var assembly = typeof(WatchServer).Assembly;
        var fullName = $"OfficeCli.{name}";
        using var stream = assembly.GetManifestResourceStream(fullName);
        if (stream == null) return $"/* Resource not found: {fullName} */";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Idle timeout is configurable via OFFICECLI_WATCH_IDLE_SECONDS so
    // tests can exercise the auto-shutdown path in seconds instead of
    // minutes. Callers that pass an explicit TimeSpan (tests that need
    // fixed values) bypass the env var. Valid range: 1s .. 24h.
    private static TimeSpan ResolveIdleTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("OFFICECLI_WATCH_IDLE_SECONDS");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out var secs)
            && secs >= 1 && secs <= 86400)
        {
            return TimeSpan.FromSeconds(secs);
        }
        return TimeSpan.FromMinutes(5);
    }

    public WatchServer(string filePath, int port, TimeSpan? idleTimeout = null, string? initialHtml = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _pipeName = GetWatchPipeName(_filePath);
        _port = port;
        _idleTimeout = idleTimeout ?? ResolveIdleTimeout();
        _tcpListener = new TcpListener(IPAddress.Any, _port);
        _engine = new WatchEngine(this);
        if (!string.IsNullOrEmpty(initialHtml))
            _engine.CurrentHtml = initialHtml;
    }

    public static string GetWatchPipeName(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            fullPath = fullPath.ToUpperInvariant();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..16];
        return $"officecli-watch-{hash}";
    }

    /// <summary>
    /// Path of the on-disk marker that records {pid, port} for a running
    /// watch. Used by <see cref="GetExistingWatchPort"/> and
    /// <see cref="IsWatching"/> to answer "is anyone watching this file?"
    /// without a pipe round-trip. Same hash key as the pipe name — one
    /// file ↔ one pipe ↔ one marker.
    /// </summary>
    public static string GetWatchMarkerPath(string filePath)
    {
        return Path.Combine(Path.GetTempPath(), GetWatchPipeName(filePath) + ".port");
    }

    /// <summary>
    /// Check if another watch process is already running for this file.
    /// Returns the port number if running, or null if not.
    ///
    /// Implementation: reads the on-disk marker file ({pid}\n{port}\n) and
    /// validates the pid is still alive. Replaces the pre-1.0.51 pipe ping
    /// probe, which cost ~100ms and falsely reported "not watching" when
    /// the pipe server was momentarily busy with another connection.
    /// </summary>
    public static int? GetExistingWatchPort(string filePath)
    {
        var markerPath = GetWatchMarkerPath(filePath);
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists) return null;
            // The marker path is predictable ($TMPDIR/officecli-watch-<hash>.port),
            // so on a shared temp dir a local attacker can plant a symlink there.
            // Only a regular file we could have written is a trustworthy marker:
            // never read through (or delete) a symlink / reparse point (CWE-59).
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var lines = File.ReadAllLines(markerPath);
            if (lines.Length < 2) return null;
            if (!int.TryParse(lines[0], out var pid)) return null;
            if (!int.TryParse(lines[1], out var port)) return null;
            if (!IsProcessAlive(pid))
            {
                // Stale marker — writer crashed or was killed without cleanup.
                // Best-effort remove so the caller can start a fresh watch.
                try { File.Delete(markerPath); } catch { }
                return null;
            }
            return port;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWatching(string filePath)
    {
        return GetExistingWatchPort(filePath).HasValue;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void WriteMarker()
    {
        var markerPath = GetWatchMarkerPath(_filePath);
        try
        {
            // Refuse to follow a pre-planted symlink at the predictable marker
            // path: a local attacker who creates the marker as a symlink to a
            // victim-writable file would otherwise have us truncate that file
            // (CWE-59 symlink-follow). FileMode.CreateNew maps to O_CREAT|O_EXCL,
            // which by POSIX fails — without following — when the path already
            // exists, including when it is a symlink. A stale regular marker from
            // a dead writer was cleared by GetExistingWatchPort just above; if a
            // squatter still holds the name we simply skip the marker (IsWatching
            // then reports false — fail-safe, no clobber).
            var bytes = Encoding.UTF8.GetBytes(
                $"{System.Diagnostics.Process.GetCurrentProcess().Id}\n{_port}\n");
            var opts = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600
            using var fs = new FileStream(markerPath, opts);
            fs.Write(bytes);
        }
        catch { /* best-effort; IsWatching just reports false if marker absent */ }
    }

    private void DeleteMarker()
    {
        try
        {
            var markerPath = GetWatchMarkerPath(_filePath);
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
        catch { /* best-effort cleanup */ }
    }

    public async Task RunAsync(CancellationToken externalToken = default)
    {
        // Prevent duplicate watch processes for the same file
        var existingPort = GetExistingWatchPort(_filePath);
        if (existingPort.HasValue)
        {
            var url = existingPort.Value > 0 ? $" at http://localhost:{existingPort.Value}" : "";
            throw new InvalidOperationException($"Another watch process is already running{url} for {_filePath}");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalToken);
        var token = linkedCts.Token;

        _tcpListener.Start();
        WriteMarker();
        Console.WriteLine($"Watch: http://localhost:{_port}");
        Console.WriteLine($"Watching: {_filePath}");
        Console.WriteLine("Press Ctrl+C to stop.");

        // Hook graceful shutdown signals. Cooperatively terminating a
        // watch process needs to (a) stop the TCP listener — the only
        // reliable way to kick AcceptTcpClientAsync on macOS, which
        // does NOT honour cancellation tokens on .NET 10 — and (b)
        // delete the $TMPDIR/CoreFxPipe_ socket file (.NET doesn't,
        // BUG-BT-003). Both steps happen inside StopAsync.
        //
        // Two signal paths cover the realistic user scenarios:
        //
        // 1. PosixSignalRegistration for SIGTERM / SIGHUP / SIGQUIT.
        //    These are the usual "kill this daemon" signals; they fire
        //    whether or not the process has a controlling TTY. Works
        //    reliably for `pkill officecli`, launcher kill, and
        //    terminal-close-while-backgrounded.
        //
        // 2. Console.CancelKeyPress for Ctrl+C (SIGINT). This fires
        //    when watch is running in the foreground of an interactive
        //    terminal — the realistic user scenario for "I pressed
        //    Ctrl+C to stop the watch I just started".
        //
        // Known limitation: sending SIGINT or SIGQUIT to a BACKGROUNDED
        // watch process (e.g. `officecli watch file & ; kill -INT %1`)
        // does not trigger either path because .NET's runtime gates
        // SIGINT/SIGQUIT handling on having a controlling TTY. This is
        // not a realistic daemon-termination pattern — callers who
        // need to stop a backgrounded watch should use `officecli
        // unwatch file` or SIGTERM, both of which work.
        var signalRegs = new List<PosixSignalRegistration>();
        void DoShutdownFromSignal()
        {
            try { StopAsync().Wait(TimeSpan.FromSeconds(10)); } catch { }
            Environment.Exit(0);
        }
        void HandleSignal(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            DoShutdownFromSignal();
        }
        void TryRegister(PosixSignal sig)
        {
            try { signalRegs.Add(PosixSignalRegistration.Create(sig, HandleSignal)); }
            catch (PlatformNotSupportedException) { /* host doesn't support this signal */ }
        }
        TryRegister(PosixSignal.SIGTERM);
        // SIGHUP: only treat as shutdown when we have a controlling TTY
        // (user closed the terminal hosting a foreground watch). For
        // non-interactive launchers (CI, agent schedulers using stdin=
        // /dev/null without setsid/nohup), the parent shell delivers a
        // spurious SIGHUP after eval; we must catch and IGNORE it,
        // because the kernel's default disposition for SIGHUP is
        // terminate — simply not registering would still kill us.
        bool sighupKills = !Console.IsInputRedirected;
        try
        {
            signalRegs.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                ctx.Cancel = true;
                if (sighupKills) DoShutdownFromSignal();
                // else: swallow — headless watch survives stray SIGHUP.
            }));
        }
        catch (PlatformNotSupportedException) { /* host doesn't support */ }
        TryRegister(PosixSignal.SIGQUIT);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            DoShutdownFromSignal();
        };
        Console.CancelKeyPress += cancelHandler;

        var pipeTask = RunPipeListenerAsync(token);
        var idleTask = RunIdleWatchdogAsync(token);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Watch HTTP error: {ex.Message}");
            }
        }

        // Main loop exited — drive the shared shutdown path. This cleans
        // up TCP listener, pipe listener, CoreFxPipe_ socket, and SSE
        // clients in order. Idempotent, so signal-driven and
        // cancellation-driven paths both converge here safely.
        try { await StopAsync(); } catch { }

        try { await pipeTask; } catch (OperationCanceledException) { }
        try { await idleTask; } catch (OperationCanceledException) { }

        foreach (var reg in signalRegs)
            try { reg.Dispose(); } catch { }
        Console.CancelKeyPress -= cancelHandler;
    }

    /// <summary>
    /// Idempotent, ordered shutdown. Every teardown path (idle watchdog,
    /// unwatch pipe command, SIGTERM/SIGINT/SIGHUP, Dispose) funnels
    /// through this method and awaits the same cached Task.
    ///
    /// Order:
    ///   1. Cancel _cts — idle watchdog and pipe listener exit their loops.
    ///   2. Call TcpListener.Stop() — only reliable way to unstick
    ///      AcceptTcpClientAsync on macOS under .NET 10.
    ///   3. Close all live SSE client streams so RunSseClientAsync
    ///      coroutines drop their references.
    ///   4. Kick the pipe listener via a local NamedPipeClientStream
    ///      connect so RunPipeListenerAsync unsticks on Windows (where
    ///      WaitForConnectionAsync doesn't honour cancellation).
    ///   5. On Unix, delete the stale $TMPDIR/CoreFxPipe_ socket file
    ///      (.NET doesn't clean it up — BUG-BT-003).
    /// </summary>
    public Task StopAsync()
    {
        lock (_shutdownLock)
        {
            return _shutdownTask ??= Task.Run(DoStopAsync);
        }
    }

    private async Task DoStopAsync()
    {
        // 1. Signal everything to stop.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        // 2. Stop the TCP listener. AcceptTcpClientAsync(token) on macOS
        //    under .NET 10 does not reliably respect cancellation; Stop()
        //    force-closes the underlying socket which makes the pending
        //    accept throw ObjectDisposedException and unwind the loop.
        try { _tcpListener.Stop(); } catch { }

        // 3. Close live SSE streams so the per-client coroutines unwind
        //    promptly. (They would eventually notice token cancellation,
        //    but a blocking write to a dead client can hang for seconds.)
        lock (_sseLock)
        {
            foreach (var s in _sseClients)
            {
                try { s.Close(); } catch { }
            }
            _sseClients.Clear();
        }

        // 4. Kick the pipe listener out of WaitForConnectionAsync.
        try
        {
            using var kick = new System.IO.Pipes.NamedPipeClientStream(
                ".", _pipeName, System.IO.Pipes.PipeDirection.InOut);
            kick.Connect(500);
        }
        catch { }

        // 4b. Delete the on-disk watch marker so external IsWatching() probes
        //     immediately see "no watch running".
        DeleteMarker();

        // 5. Delete the stale CoreFxPipe_ socket on Unix. .NET does not
        //    do this on its own (BUG-BT-003 — fuzzer found 302 stale
        //    files). Run here in StopAsync rather than Dispose so it
        //    also works when the process exits via SIGTERM signal path.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var sockPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + _pipeName);
                if (File.Exists(sockPath)) File.Delete(sockPath);
            }
            catch { /* best-effort cleanup */ }
        }

        // Small yield so any synchronous continuations scheduled on the
        // now-cancelled token get a chance to run before the caller
        // proceeds. Not strictly required for correctness.
        await Task.Yield();
    }

    private async Task RunIdleWatchdogAsync(CancellationToken token)
    {
        var checkInterval = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, _idleTimeout.TotalSeconds / 2)));
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, token);
            int clientCount;
            lock (_sseLock) { clientCount = _sseClients.Count; }
            if (clientCount == 0 && DateTime.UtcNow - _lastActivityTime > _idleTimeout)
            {
                Console.WriteLine("Watch: idle timeout, shutting down.");
                // Go through the shared ordered shutdown path instead of
                // raw-cancelling _cts, so TcpListener.Stop() gets called
                // and the main loop doesn't hang waiting for an accept
                // that never completes.
                _ = StopAsync();
                break;
            }
        }
    }

    private async Task RunPipeListenerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var server = new System.IO.Pipes.NamedPipeServerStream(
                _pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException) { await server.DisposeAsync(); break; }
            catch { await server.DisposeAsync(); continue; }

            // Handle the client on a background task and immediately loop back
            // to accept another connection. This avoids a tiny window where the
            // pipe is not listening between iterations and back-to-back CLI
            // calls (e.g. multiple mark adds in a tight test loop) get refused.
            _ = Task.Run(async () =>
            {
                using (server)
                {
                    try { await HandleSinglePipeClientAsync(server, token); }
                    catch { /* ignore individual client errors */ }
                }
            }, token);
        }
    }

    private async Task HandleSinglePipeClientAsync(System.IO.Pipes.NamedPipeServerStream server, CancellationToken token)
    {
            try
            {
                var noBom = new UTF8Encoding(false);
                using var reader = new StreamReader(server, noBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(server, noBom, leaveOpen: true) { AutoFlush = true };

                var message = await reader.ReadLineAsync(token);
                _lastActivityTime = DateTime.UtcNow;

                if (message == "close")
                {
                    await writer.WriteLineAsync("ok".AsMemory(), token);
                    Console.WriteLine("Watch closed by remote command.");
                    // Go through shared shutdown — idempotent, ordered,
                    // also cleans up CoreFxPipe_ socket on Unix.
                    _ = StopAsync();
                    return;
                }
                else if (message == "get-selection")
                {
                    // Return current selection as a JSON array of paths.
                    // Empty selection → "[]". Never null.
                    string[] snapshot = _engine.GetSelectionSnapshot();
                    var json = JsonSerializer.Serialize(snapshot, WatchSelectionJsonOptions.StringArrayInfo);
                    await writer.WriteLineAsync(json.AsMemory(), token);
                }
                else if (message == "get-marks")
                {
                    // Return {"version":N,"marks":[...]} so callers can do CAS-style
                    // detection. Empty marks → []. Never null.
                    // Uses Relaxed options so CJK content emits literal chars.
                    var (snapshot, version) = _engine.GetMarksAndVersion();
                    var resp = new MarksResponse { Version = version, Marks = snapshot };
                    var payload = JsonSerializer.Serialize(resp, WatchMarkJsonOptions.MarksResponseInfo);
                    await writer.WriteLineAsync(payload.AsMemory(), token);
                }
                else if (message != null && message.StartsWith("mark ", StringComparison.Ordinal))
                {
                    // "mark <json>" — add a mark, return assigned id
                    var payload = message.Substring(5);
                    var resp = _engine.HandleMarkAdd(payload);
                    await writer.WriteLineAsync(resp.AsMemory(), token);
                }
                else if (message != null && message.StartsWith("unmark ", StringComparison.Ordinal))
                {
                    // "unmark <json>" — remove marks by path or all
                    var payload = message.Substring(7);
                    var resp = _engine.HandleMarkRemove(payload);
                    await writer.WriteLineAsync(resp.AsMemory(), token);
                }
                else if (message != null && message.StartsWith("scroll ", StringComparison.Ordinal))
                {
                    // "scroll <selector>" — validate the CSS selector against
                    // the cached HTML snapshot, broadcast on success, return
                    // "ok" or "err:<msg>". BUG-BT-R33-3: pure-positional
                    // existence check on the cached HTML so goto can fail
                    // exit=1 instead of silently exit=0 on missing anchors.
                    // CONSISTENCY(watch-isolation): no file open — only the
                    // already-cached HTML string is inspected.
                    var selector = message.Substring(7);
                    if (_engine.TryScroll(selector))
                        await writer.WriteLineAsync("ok".AsMemory(), token);
                    else
                        await writer.WriteLineAsync(("err:selector not found in current HTML: " + selector).AsMemory(), token);
                }
                else if (message != null)
                {
                    await writer.WriteLineAsync("ok".AsMemory(), token);
                    // Try to parse as WatchMessage JSON
                    _engine.HandleWatchMessage(message);
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* ignore pipe errors */ }
    }

    // ==================== Marks (state + ops live in WatchEngine) ====================

    /// <summary>Test-only accessor for current marks snapshot.</summary>
    internal WatchMark[] GetMarksSnapshot() => _engine.GetMarksSnapshot();

    /// <summary>Test-only accessor for the current marks version.</summary>
    internal int GetMarksVersion() => _engine.GetMarksVersion();

    /// <summary>
    /// Test-only hook: install a full HTML snapshot and trigger reconciliation.
    /// Delegates to the engine.
    /// </summary>
    internal void ApplyFullHtmlForTests(string html) => _engine.ApplyFullHtmlForTests(html);

    private void BroadcastSse(string sseJson)
        => Broadcast(new WatchSseEvent(sseJson));

    /// <summary>
    /// <see cref="IWatchBroadcaster"/> delivery point: frame the event and write it
    /// to every connected SSE client, dropping any that fault.
    /// </summary>
    public void Broadcast(WatchSseEvent watchEvent)
    {
        lock (_sseLock)
        {
            var dead = new List<NetworkStream>();
            foreach (var client in _sseClients)
            {
                try
                {
                    var data = Encoding.UTF8.GetBytes($"event: {watchEvent.EventName}\ndata: {watchEvent.Data}\n\n");
                    client.Write(data);
                    client.Flush();
                }
                catch
                {
                    dead.Add(client);
                }
            }
            foreach (var d in dead) _sseClients.Remove(d);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            var stream = client.GetStream();
            var (requestLine, headers, bodyPrefix) = await ReadHttpRequestHeaderAsync(stream, token);

            // Anti-DNS-rebinding gate. A rebinding attack reaches this loopback
            // port but the request carries the attacker's domain in the Host
            // header (a header page JS cannot forge), so any request whose Host
            // is not a loopback name is rejected — including GET / and the SSE
            // stream, which would otherwise leak the whole document. Embedder-
            // agnostic: direct browser tabs and Electron <webview>s send a
            // localhost Host automatically; reverse proxies that forward a
            // non-loopback Host can allowlist it via OFFICECLI_WATCH_ALLOWED_HOSTS.
            if (!IsHostAllowed(headers))
            {
                await WriteForbiddenAsync(stream, ForbiddenHostMessage(headers), token);
                client.Close();
                return;
            }

            if (requestLine.Contains("GET /events"))
            {
                try
                {
                    await HandleSseAsync(stream, token);
                }
                finally
                {
                    client.Close();
                }
                return;
            }

            if (requestLine.StartsWith("POST /api/selection", StringComparison.Ordinal))
            {
                if (!IsOriginAllowed(headers))
                {
                    await WriteForbiddenAsync(stream, ForbiddenOriginMessage(headers), token);
                    client.Close();
                    return;
                }
                await HandlePostSelectionAsync(stream, headers, bodyPrefix, token);
                client.Close();
                return;
            }

            if (requestLine.StartsWith("POST /api/send", StringComparison.Ordinal))
            {
                if (!IsOriginAllowed(headers))
                {
                    await WriteForbiddenAsync(stream, ForbiddenOriginMessage(headers), token);
                    client.Close();
                    return;
                }
                await HandlePostSendAsync(stream, headers, bodyPrefix, WantsJson(requestLine), token);
                client.Close();
                return;
            }

            if (requestLine.StartsWith("POST /api/batch", StringComparison.Ordinal))
            {
                if (!IsOriginAllowed(headers))
                {
                    await WriteForbiddenAsync(stream, ForbiddenOriginMessage(headers), token);
                    client.Close();
                    return;
                }
                await HandlePostBatchAsync(stream, headers, bodyPrefix, WantsJson(requestLine), token);
                client.Close();
                return;
            }

            // BUG-TESTER-R503: GET/PUT/etc on /api/selection must return 405,
            // not fall through to the HTML preview. Without this, an API
            // client that uses the wrong verb gets back a 200 HTML page and
            // never realizes the request was malformed.
            if (requestLine.Contains(" /api/selection"))
            {
                var msg = Encoding.UTF8.GetBytes("Method Not Allowed: /api/selection only accepts POST");
                var hdr = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 405 Method Not Allowed\r\nAllow: POST\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {msg.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(hdr, token);
                await stream.WriteAsync(msg, token);
                client.Close();
                return;
            }

            // BUG-TESTER-R504: any other /api/... path is unknown and must
            // return 404. Without this, an agent that mistypes /api/marks
            // (we don't have a marks HTTP endpoint, only the pipe verb) gets
            // the HTML preview page back and silently misroutes.
            if (requestLine.Contains(" /api/"))
            {
                var msg = Encoding.UTF8.GetBytes("Not Found");
                var hdr = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {msg.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(hdr, token);
                await stream.WriteAsync(msg, token);
                client.Close();
                return;
            }

            // Default: serve current HTML (GET / and everything else)
            var html = string.IsNullOrEmpty(_engine.CurrentHtml)
                ? InjectSseScript(WaitingHtml)
                : InjectSseScript(_engine.CurrentHtml);
            var bodyBytes = Encoding.UTF8.GetBytes(html);
            var header = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(bodyBytes, token);
            client.Close();
        }
        catch
        {
            try { client.Close(); } catch { }
        }
    }

    // Loopback host names accepted in the Host/Origin headers. Seeded with the
    // standard loopback identities and extended (once, at first use) from
    // OFFICECLI_WATCH_ALLOWED_HOSTS for reverse-proxy setups that forward a
    // non-loopback Host upstream.
    private static readonly HashSet<string> _allowedHosts = BuildAllowedHosts();

    private static HashSet<string> BuildAllowedHosts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "localhost", "127.0.0.1", "[::1]", "::1" };
        var extra = Environment.GetEnvironmentVariable("OFFICECLI_WATCH_ALLOWED_HOSTS");
        if (!string.IsNullOrWhiteSpace(extra))
            foreach (var h in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(h);
        return set;
    }

    /// <summary>Strip the optional <c>:port</c> from a Host header value, preserving bracketed IPv6.</summary>
    internal static string ExtractHostname(string hostHeader)
    {
        var v = hostHeader.Trim();
        if (v.StartsWith("[", StringComparison.Ordinal)) // [::1] or [::1]:port
        {
            var rb = v.IndexOf(']');
            return rb >= 0 ? v[..(rb + 1)] : v;
        }
        var colon = v.IndexOf(':');
        return colon >= 0 ? v[..colon] : v;
    }

    /// <summary>True if the request's Host header names an accepted loopback host (anti-rebinding).</summary>
    internal static bool IsHostAllowed(Dictionary<string, string> headers)
    {
        // HTTP/1.1 mandates Host and every browser sends it; a missing/blank
        // Host is treated as untrusted and rejected.
        if (!headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host))
            return false;
        return _allowedHosts.Contains(ExtractHostname(host));
    }

    /// <summary>
    /// True if a state-changing request's Origin is absent or names a loopback host.
    /// Absent Origin (server-side proxy hop, or a same-origin navigation that omits
    /// it) is allowed; a present cross-origin Origin is rejected (CSRF defense).
    /// </summary>
    internal static bool IsOriginAllowed(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin))
            return true;
        if (Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var u))
            return _allowedHosts.Contains(u.Host) || _allowedHosts.Contains($"[{u.Host}]");
        return false;
    }

    private static string ForbiddenHostMessage(Dictionary<string, string> headers)
    {
        headers.TryGetValue("Host", out var host);
        return $"403 Forbidden: request Host '{host ?? "(none)"}' is not a recognized loopback host.\n" +
               "The officecli watch preview only accepts Host: localhost / 127.0.0.1 (anti-DNS-rebinding).\n" +
               "If you reach it through a reverse proxy that forwards a different Host, set\n" +
               "OFFICECLI_WATCH_ALLOWED_HOSTS=<hostname>[,<hostname>...] before starting `officecli watch`.\n";
    }

    private static string ForbiddenOriginMessage(Dictionary<string, string> headers)
    {
        headers.TryGetValue("Origin", out var origin);
        return $"403 Forbidden: cross-origin request from Origin '{origin ?? "(none)"}' is not allowed for this endpoint.\n";
    }

    private static async Task WriteForbiddenAsync(NetworkStream stream, string message, CancellationToken token)
    {
        var msg = Encoding.UTF8.GetBytes(message);
        var hdr = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {msg.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(hdr, token);
        await stream.WriteAsync(msg, token);
    }

    /// <summary>
    /// Read the HTTP request line and headers, plus any body bytes that arrived in the
    /// same TCP read. Returns (requestLine, headers, bodyPrefix). Caller is responsible
    /// for reading the rest of the body using Content-Length if needed.
    /// </summary>
    private static async Task<(string requestLine, Dictionary<string, string> headers, string bodyPrefix)>
        ReadHttpRequestHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), token);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (sb.Length > 32 * 1024) break; // safety cap
        }

        var raw = sb.ToString();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headerEnd < 0)
        {
            // No header terminator — treat the whole thing as a single line
            var firstLine = raw;
            var crlf = raw.IndexOf("\r\n", StringComparison.Ordinal);
            if (crlf >= 0) firstLine = raw[..crlf];
            return (firstLine, headers, "");
        }

        var headerSection = raw[..headerEnd];
        var bodyPrefix = raw[(headerEnd + 4)..];
        var lines = headerSection.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0] : "";
        for (int i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        return (requestLine, headers, bodyPrefix);
    }

    // Maximum size of a POST /api/selection request body. 64 KB is plenty for tens
    // of thousands of selected paths and bounds memory + read time per request.
    private const int MaxSelectionBodyBytes = 64 * 1024;
    // Hard limit on how long we'll wait for the rest of a POST body to arrive.
    // Prevents slow-loris style stalls (Content-Length advertised, body never sent).
    private static readonly TimeSpan PostBodyReadTimeout = TimeSpan.FromSeconds(3);

    private async Task HandlePostSelectionAsync(NetworkStream stream, Dictionary<string, string> headers, string bodyPrefix, CancellationToken token)
    {
        int statusCode = 204;
        string statusText = "No Content";
        string body = bodyPrefix;

        try
        {
            // Reject runaway Content-Length up front (covers FUZZER-001 slow-loris).
            int contentLength = -1;
            if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var parsedCl))
            {
                if (parsedCl < 0 || parsedCl > MaxSelectionBodyBytes)
                    throw new InvalidDataException("body too large");
                contentLength = parsedCl;
            }

            // If the bodyPrefix already exceeds Content-Length, trim it. Without this,
            // an attacker could smuggle extra bytes by sending a long body in the same
            // TCP segment as the headers (FUZZER-002).
            var prefixBytes = Encoding.UTF8.GetByteCount(body);
            if (contentLength >= 0 && prefixBytes > contentLength)
            {
                var prefBytes = Encoding.UTF8.GetBytes(body);
                body = Encoding.UTF8.GetString(prefBytes, 0, contentLength);
                prefixBytes = contentLength;
            }

            // Read any missing tail bytes, bounded by both size and time.
            if (contentLength > prefixBytes)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(PostBodyReadTimeout);
                var sb = new StringBuilder(body, contentLength);
                int have = prefixBytes;
                var buf = new byte[8192];
                try
                {
                    while (have < contentLength)
                    {
                        var toRead = Math.Min(buf.Length, contentLength - have);
                        var n = await stream.ReadAsync(buf.AsMemory(0, toRead), readCts.Token);
                        if (n == 0) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                        have += n;
                        if (have > MaxSelectionBodyBytes)
                            throw new InvalidDataException("body too large");
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new InvalidDataException("body read timed out");
                }
                body = sb.ToString();
            }

            // Expected JSON: {"paths": ["/slide[1]/shape[2]", ...]}
            var req = JsonSerializer.Deserialize(body, WatchSelectionJsonContext.Default.SelectionRequest);
            var rawSelection = req?.Paths ?? new List<string>();
            // BUG-TESTER-R501/R502 + BUG-FUZZER-R5-04: bring selection path
            // hardening up to parity with mark (Round 2/3 fixes). Each path is
            // Trim()-normalized; whitespace-only and paths not starting with
            // '/' are dropped; paths containing control characters (CR/LF/NUL
            // /etc) are dropped because they would corrupt the in-memory
            // representation and the SSE/pipe readback even though
            // AppendJsonString escapes them on the wire.
            // CONSISTENCY(path-stability): mirror of HandleMarkAdd's input
            // validation. If you change the path acceptance rules, change
            // both at once. grep CONSISTENCY(path-stability).
            var newSelection = new List<string>(rawSelection.Count);
            foreach (var raw in rawSelection)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var trimmed = raw.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (!trimmed.StartsWith("/")) continue;
                var hasControl = false;
                for (int i = 0; i < trimmed.Length; i++)
                {
                    if (char.IsControl(trimmed[i])) { hasControl = true; break; }
                }
                if (hasControl) continue;
                newSelection.Add(trimmed);
            }

            _engine.SetSelection(newSelection);
            _lastActivityTime = DateTime.UtcNow;

            // Broadcast to all SSE clients so other browsers can highlight in sync
            BroadcastSelectionUpdate(newSelection);
        }
        catch
        {
            statusCode = 400;
            statusText = "Bad Request";
        }

        var resp = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(resp, token);
    }

    /// <summary>
    /// Handle POST /api/send — spawn officecli set/add/remove as a child process
    /// to modify the file, mirroring the SDKs' send(item) (one batch-item, this
    /// call's own status is the result). That command auto-notifies the watch
    /// server via named pipe, triggering an SSE refresh. WatchServer never opens
    /// the file directly — widening this beyond `set` must stay a child-process
    /// spawn, not an in-process handler call, so this can never become a second
    /// writer racing a live resident.
    /// </summary>
    private async Task HandlePostSendAsync(NetworkStream stream, Dictionary<string, string> headers, string bodyPrefix, bool json, CancellationToken token)
    {
        try
        {
            // Read body (same pattern as selection handler)
            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl))
                contentLength = cl;
            if (contentLength > MaxSelectionBodyBytes) throw new InvalidDataException("body too large");

            var body = bodyPrefix;
            if (contentLength > body.Length)
            {
                var sb = new StringBuilder(body);
                var buf = new byte[4096];
                int have = Encoding.UTF8.GetByteCount(body);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(PostBodyReadTimeout);
                while (have < contentLength)
                {
                    var n = await stream.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    have += n;
                }
                body = sb.ToString();
            }

            // Same batch-item vocabulary as CLI batch / the SDKs' send(item)
            // {"command": "set"|"add"|"remove", ...}.
            // Bare {"path", "props"} or legacy {"path", "prop", "value"} with
            // no "command" field default to "set" for pre-existing callers.
            // The item → CLI-argument mapping + child-process spawn live in
            // WatchCommandRunner so an embedder reuses the exact same behavior.
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var args = WatchCommandRunner.BuildSendArguments(_filePath, doc.RootElement);
            var output = await WatchCommandRunner.RunAsync(
                WatchCommandRunner.ResolveOfficeCliPath(), args, json, token);
            // command auto-notifies watch via named pipe → SSE refresh
            await WriteCommEnvelopeAsync(stream, true, output, token);
        }
        catch (System.Exception ex)
        {
            await WriteCommEnvelopeAsync(stream, false, ex.Message, token);
        }
    }

    /// <summary>
    /// Handle POST /api/batch — spawn officecli batch as a child process,
    /// mirroring the SDKs' batch(items). The posted
    /// body is a JSON array of the same batch-item shape /api/send accepts;
    /// `officecli batch --commands` already takes that array verbatim, so
    /// unlike /api/send there is no per-command arg-building here — the
    /// whole body passes straight through. Same child-process-spawn
    /// constraint as /api/send — never touch the document in-process.
    /// </summary>
    private async Task HandlePostBatchAsync(NetworkStream stream, Dictionary<string, string> headers, string bodyPrefix, bool json, CancellationToken token)
    {
        try
        {
            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl))
                contentLength = cl;
            if (contentLength > MaxSelectionBodyBytes) throw new InvalidDataException("body too large");

            var body = bodyPrefix;
            if (contentLength > body.Length)
            {
                var sb = new StringBuilder(body);
                var buf = new byte[4096];
                int have = Encoding.UTF8.GetByteCount(body);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(PostBodyReadTimeout);
                while (have < contentLength)
                {
                    var n = await stream.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    have += n;
                }
                body = sb.ToString();
            }

            // The whole body is a JSON array of batch-items that `officecli batch
            // --commands` takes verbatim; WatchCommandRunner builds the args and
            // spawns officecli (same child-process trust boundary as /api/send).
            var args = WatchCommandRunner.BuildBatchArguments(_filePath, body);
            var output = await WatchCommandRunner.RunAsync(
                WatchCommandRunner.ResolveOfficeCliPath(), args, json, token);
            // batch auto-notifies watch via named pipe → SSE refresh
            await WriteCommEnvelopeAsync(stream, true, output, token);
        }
        catch (System.Exception ex)
        {
            await WriteCommEnvelopeAsync(stream, false, ex.Message, token);
        }
    }

    /// <summary>
    /// Parse the <c>?json</c> query flag off the request line. Absent, or any
    /// value other than <c>0</c>/<c>false</c> =&gt; true (structured, the
    /// default); <c>?json=0</c> / <c>?json=false</c> =&gt; plain text. Mirrors
    /// the CLI's <c>--json</c> opt-in.
    /// </summary>
    private static bool WantsJson(string requestLine)
    {
        int q = requestLine.IndexOf('?');
        if (q < 0) return true;
        int sp = requestLine.IndexOf(' ', q);
        string query = sp < 0 ? requestLine.Substring(q + 1) : requestLine.Substring(q + 1, sp - q - 1);
        foreach (var pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair.Substring(0, eq);
            if (k == "json")
            {
                string v = eq < 0 ? "1" : pair.Substring(eq + 1);
                return !(v == "0" || v.Equals("false", System.StringComparison.OrdinalIgnoreCase));
            }
        }
        return true;
    }

    /// <summary>
    /// Write the /api/send + /api/batch response as a communication envelope:
    /// <c>{ "success": bool, "message"|"error": string }</c>. <c>success</c>
    /// reflects the transport/process layer only (did the request reach
    /// officecli and run without crashing) — NOT officecli's business verdict,
    /// which rides inside <c>message</c> (its own <c>--json</c> envelope, or
    /// plain text). Callers unwrap this: on success take <c>message</c>
    /// (officecli's raw stdout); on failure, <c>error</c>. Always HTTP 200 —
    /// the envelope's <c>success</c> is the status signal.
    /// </summary>
    private static async Task WriteCommEnvelopeAsync(NetworkStream stream, bool success, string content, CancellationToken token)
    {
        // Trim/AOT-safe JSON build via Utf8JsonWriter (no reflection) — mirrors
        // CommandBuilder.PrintBatchResults. `content` is escaped by WriteString.
        byte[] bodyBytes;
        using (var ms = new System.IO.MemoryStream())
        {
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteBoolean("success", success);
                w.WriteString(success ? "message" : "error", content);
                w.WriteEndObject();
            }
            bodyBytes = ms.ToArray();
        }
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bodyBytes, token);
    }

    private void BroadcastSelectionUpdate(List<string> paths)
    {
        var sb = new StringBuilder();
        sb.Append("{\"action\":\"selection-update\",\"paths\":[");
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WatchEngine.AppendJsonString(sb, paths[i]);
        }
        sb.Append("]}");
        BroadcastSse(sb.ToString());
    }

    private async Task HandleSseAsync(NetworkStream stream, CancellationToken token)
    {
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
        await stream.WriteAsync(header, token);

        _lastActivityTime = DateTime.UtcNow;

        // Send the current selection immediately so the new client can highlight
        // any elements that are already selected by other browsers viewing the same
        // file. CRITICAL: this write must happen BEFORE adding the stream to
        // _sseClients. Otherwise BroadcastSse (running on another thread under
        // _sseLock) could write to the same stream at the same time we are writing
        // the initial event here, and NetworkStream is not safe for concurrent writes
        // — interleaved bytes would corrupt SSE framing.
        try
        {
            string[] snapshot = _engine.GetSelectionSnapshot();
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"selection-update\",\"paths\":[");
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (i > 0) sb.Append(',');
                WatchEngine.AppendJsonString(sb, snapshot[i]);
            }
            sb.Append("]}");
            var initEvt = Encoding.UTF8.GetBytes($"event: update\ndata: {sb}\n\n");
            await stream.WriteAsync(initEvt, token);

            // Also dump the current marks snapshot so a freshly connected browser
            // immediately sees any marks the CLI has already added. Mirrors the
            // selection init dump pattern above.
            var (markSnapshot, markVersion) = _engine.GetMarksAndVersion();
            var markJson = WatchEngine.BuildMarkUpdateJson(markSnapshot, markVersion);
            var markInitEvt = Encoding.UTF8.GetBytes($"event: update\ndata: {markJson}\n\n");
            await stream.WriteAsync(markInitEvt, token);
        }
        catch { }

        // Now safe to register: any subsequent BroadcastSse will serialize against
        // future writes via _sseLock.
        lock (_sseLock) { _sseClients.Add(stream); }

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(30000, token);
                var heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                await stream.WriteAsync(heartbeat, token);
            }
        }
        catch { }
        finally
        {
            lock (_sseLock) { _sseClients.Remove(stream); }
        }
    }

    private static string InjectSseScript(string html)
    {
        var script = _sseScriptBlock.Value;
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return html[..idx] + script + html[idx..];
        return html + script;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Delegate to shared shutdown. If RunAsync or a signal handler
        // already drove shutdown, this just awaits the cached Task.
        // Steps include TcpListener.Stop(), pipe kick, SSE cleanup, and
        // CoreFxPipe_ socket delete (BUG-BT-003).
        try { StopAsync().Wait(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: watch shutdown error: {ex.Message}"); }

        try { _cts.Dispose(); } catch { }
    }
}
