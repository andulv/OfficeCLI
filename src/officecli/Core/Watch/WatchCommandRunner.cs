// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0
//
// CONSISTENCY(watch-isolation): this file does not reference OfficeCli.Handlers, does not open files,
// does not write to disk. It only builds officecli CLI argument lists and spawns officecli as a
// child process (the same trust boundary as WatchServer's /api/send + /api/batch). To relax this
// red line, grep "CONSISTENCY(watch-isolation)" and review every file in the watch subsystem.

using System.Text.Json;

namespace OfficeCli.Core;

/// <summary>
/// Transport-agnostic mapping from a browser edit request (a JSON batch-item, or
/// a raw batch array) to an officecli child-process invocation. This is the exact
/// logic behind the watch preview's <c>/api/send</c> and <c>/api/batch</c>
/// endpoints, factored out so any host (the built-in <see cref="WatchServer"/> or
/// an external embedder) drives officecli identically. It owns no sockets and
/// never opens the document — it only builds argument lists and starts officecli,
/// which auto-notifies the running watch via named pipe (→ SSE refresh).
/// </summary>
public static class WatchCommandRunner
{
    /// <summary>
    /// Resolve the officecli executable to spawn. Defaults to the current process
    /// (so an embedded/self-hosted watch re-invokes the same binary); falls back
    /// to <c>officecli[.exe]</c> on PATH when the module path is unavailable.
    /// </summary>
    public static string ResolveOfficeCliPath()
        => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
           ?? (OperatingSystem.IsWindows() ? "officecli.exe" : "officecli");

    /// <summary>
    /// Build the officecli argument list for a single send batch-item. Mirrors the
    /// SDKs' <c>send(item)</c> vocabulary: <c>{"command": "set"|"add"|"remove"|
    /// "get"|"move"|"swap", ...}</c>. Bare <c>{"path","props"}</c> or legacy
    /// <c>{"path","prop","value"}</c> with no <c>"command"</c> default to
    /// <c>set</c>. Does not append <c>--json</c> — that is a <see cref="RunAsync"/>
    /// concern.
    /// </summary>
    public static List<string> BuildSendArguments(string filePath, JsonElement root)
    {
        var args = new List<string>();
        var command = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() ?? "set" : "set";

        switch (command.ToLowerInvariant())
        {
            case "add":
            {
                // Accept the canonical batch-item key "path" (used by /api/batch,
                // the SDKs) as well as the legacy "parent". /api/send must accept
                // the same item shape as /api/batch — otherwise `add` diverges per
                // backend and a `path`-shaped item throws a raw
                // KeyNotFoundException instead of running.
                var parent = (root.TryGetProperty("path", out var addPathEl) ? addPathEl.GetString() : null)
                    ?? (root.TryGetProperty("parent", out var addParentEl) ? addParentEl.GetString() : null)
                    ?? "";
                args.Add("add");
                args.Add(filePath);
                args.Add(parent);
                // --from clones an existing element (shape/slide); it is
                // mutually exclusive with --type/--prop (see `add`), so when
                // present it is the whole command.
                if (root.TryGetProperty("from", out var fromEl) && fromEl.GetString() is { } from)
                {
                    args.Add("--from");
                    args.Add(from);
                }
                else
                {
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() is { } type)
                    {
                        args.Add("--type");
                        args.Add(type);
                    }
                    AppendProps(args, root);
                }
                // Position hints apply to both clone and typed add.
                AppendPositionArgs(args, root);
                break;
            }
            case "remove":
            {
                var path = root.GetProperty("path").GetString() ?? "";
                args.Add("remove");
                args.Add(filePath);
                args.Add(path);
                break;
            }
            case "get":
            {
                // Read-only: `officecli get <path>` (served from the resident's
                // current in-memory state). Used by the editor to read a
                // property's prior value / capture an element before deletion for
                // undo. Still a child process — no in-process document access, so
                // the watch red line holds.
                var path = root.GetProperty("path").GetString() ?? "";
                args.Add("get");
                args.Add(filePath);
                args.Add(path);
                break;
            }
            case "move":
            {
                var path = root.GetProperty("path").GetString() ?? "";
                args.Add("move");
                args.Add(filePath);
                args.Add(path);
                if (root.TryGetProperty("to", out var toEl) && toEl.GetString() is { } to)
                { args.Add("--to"); args.Add(to); }
                AppendPositionArgs(args, root);
                break;
            }
            case "swap":
            {
                var path1 = root.GetProperty("path").GetString() ?? "";
                // Canonical second path is "path2"; accept legacy "to".
                var path2 = root.TryGetProperty("path2", out var p2El) ? p2El.GetString() ?? ""
                    : root.TryGetProperty("to", out var toEl2) ? toEl2.GetString() ?? "" : "";
                args.Add("swap");
                args.Add(filePath);
                args.Add(path1);
                args.Add(path2);
                break;
            }
            case "set":
            default:
            {
                var path = root.GetProperty("path").GetString() ?? "";
                args.Add("set");
                args.Add(filePath);
                args.Add(path);
                if (root.TryGetProperty("props", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
                {
                    AppendProps(args, root);
                }
                else
                {
                    // Legacy shape: {"path", "prop", "value"} (single property, no "props" object).
                    var prop = root.GetProperty("prop").GetString() ?? "text";
                    var value = root.GetProperty("value").GetString() ?? "";
                    args.Add("--prop");
                    args.Add($"{prop}={value}");
                }
                break;
            }
        }

        return args;
    }

    /// <summary>
    /// Build the officecli argument list for a batch request. <paramref name="commandsJson"/>
    /// is the JSON array of batch-items passed verbatim to <c>officecli batch --commands</c>.
    /// Does not append <c>--json</c> — that is a <see cref="RunAsync"/> concern.
    /// </summary>
    public static List<string> BuildBatchArguments(string filePath, string commandsJson)
        => new() { "batch", filePath, "--commands", commandsJson };

    /// <summary>
    /// Spawn officecli with the given argument list and return its trimmed stdout.
    /// stderr is drained concurrently (redirected but never surfaced) so a child
    /// that fills the pipe buffer with warnings cannot deadlock the caller.
    /// Appends <c>--json</c> when <paramref name="json"/> is set (the CLI's opt-in
    /// for the structured envelope). officecli auto-notifies the running watch via
    /// named pipe, triggering an SSE refresh.
    /// </summary>
    public static async Task<string> RunAsync(string exePath, IReadOnlyList<string> args, bool json, CancellationToken token)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (json) psi.ArgumentList.Add("--json");

        string output = "";
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            var drainErr = proc.StandardError.ReadToEndAsync(token);
            output = await proc.StandardOutput.ReadToEndAsync(token);
            await proc.WaitForExitAsync(token);
            _ = await drainErr;
        }
        return output.TrimEnd('\n', '\r');
    }

    private static void AppendProps(List<string> args, JsonElement root)
    {
        if (!root.TryGetProperty("props", out var propsEl) || propsEl.ValueKind != JsonValueKind.Object)
            return;
        foreach (var kv in propsEl.EnumerateObject())
        {
            args.Add("--prop");
            args.Add($"{kv.Name}={kv.Value.GetString() ?? ""}");
        }
    }

    /// <summary>
    /// Append the shared insert-position hints (--index / --after / --before) that
    /// add and move accept. Order-neutral; officecli resolves precedence.
    /// </summary>
    private static void AppendPositionArgs(List<string> args, JsonElement root)
    {
        if (root.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
        { args.Add("--index"); args.Add(idxEl.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (root.TryGetProperty("after", out var afEl) && afEl.GetString() is { } af)
        { args.Add("--after"); args.Add(af); }
        if (root.TryGetProperty("before", out var beEl) && beEl.GetString() is { } be)
        { args.Add("--before"); args.Add(be); }
    }
}
