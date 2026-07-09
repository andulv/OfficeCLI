// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using OfficeCli.Core;
using OfficeCli.Handlers;

namespace OfficeCli;

static partial class CommandBuilder
{
    // Shown as the `batch` command's help/description. The flags alone don't
    // tell a caller (or an agent) what each JSON array ITEM looks like — the
    // single most common batch mistake is stuffing a whole CLI line into
    // "command" (e.g. {"command":"add /slide[1] --type shape --prop ..."}),
    // which fails with "Unknown command". Document the per-item shape and a
    // concrete example here so `help batch` actually teaches it.
    private const string BatchHelpDescription =
        "Execute multiple commands from a JSON array in a single pass. Standalone, this is one open/save cycle; through a live resident the items apply in memory and the disk write is deferred to save/close/idle-autosave — adaptive 2-10s after going idle, or before the batch returns under OFFICECLI_RESIDENT_FLUSH=each (officecli's own reads still see the changes immediately).\n\n"
        + "Each array item is an OBJECT whose \"command\" is the bare verb "
        + "(add/set/remove/move/swap/get/query/...); the verb's arguments are SIBLING fields, "
        + "not a CLI string inside \"command\". Common fields: \"parent\" (add target), "
        + "\"path\" (set/remove/get target), \"selector\" (query filter; \"path\" is accepted as an alias), \"type\" (element type for add), "
        + "\"props\" (a key->value map of --prop values), \"to\"/\"after\"/\"before\" (move), "
        + "\"path2\" (swap's second path).\n\n"
        + "Pass the array via --commands, or as the same JSON on stdin / --input <file>. Example:\n"
        + "[\n"
        + "  {\"command\":\"add\",\"parent\":\"/slide[1]\",\"type\":\"shape\",\"props\":{\"text\":\"Hi\",\"x\":\"1cm\",\"y\":\"2cm\"}},\n"
        + "  {\"command\":\"set\",\"path\":\"/slide[1]/shape[1]\",\"props\":{\"bold\":\"true\"}},\n"
        + "  {\"command\":\"remove\",\"path\":\"/slide[2]/shape[3]\"}\n"
        + "]";

    /// <summary>
    /// Apply a batch of commands against an already-open handler. This is the
    /// single shared replay loop behind all three batch surfaces — the
    /// non-resident CLI path, the MCP server, and the resident server — so the
    /// try/catch/stop-on-error semantics can never drift between them again.
    ///
    /// Save deferral and protection gating are intentionally NOT done here:
    /// they differ by handler lifetime. The dispose-based callers (CLI
    /// non-resident, MCP) leave <c>DeferSave=true</c> and rely on the
    /// Dispose-time <c>FinalizeDeferredIds</c> flush — see
    /// <see cref="RunNonResidentBatch"/>; the long-lived resident saves and
    /// restores <c>DeferSave</c> and runs <c>ReconcileGlobalIds</c> itself.
    ///
    /// <paramref name="skipResidentOnlyCommands"/> is set by the resident, which
    /// already holds the file open: an <c>open</c>/<c>close</c> inside the batch
    /// would conflict, so they are reported as skipped instead of executed.
    /// </summary>
    internal static List<BatchResult> ApplyBatchItems(
        OfficeCli.Core.IDocumentHandler handler, List<BatchItem> items,
        bool stopOnError, bool json, bool skipResidentOnlyCommands = false,
        ICollection<string>? unrecognizedLatex = null)
    {
        var results = new List<BatchResult>();
        for (int bi = 0; bi < items.Count; bi++)
        {
            var item = items[bi];
            if (skipResidentOnlyCommands)
            {
                var cmd = (item.Command ?? "").ToLowerInvariant();
                if (cmd is "open" or "close")
                {
                    results.Add(new BatchResult { Index = bi, Success = true, Output = $"Skipped '{cmd}' (resident mode)" });
                    continue;
                }
            }
            try
            {
                var output = ExecuteBatchItem(handler, item, json);
                results.Add(new BatchResult { Index = bi, Success = true, Output = output });
            }
            catch (Exception ex)
            {
                results.Add(new BatchResult { Index = bi, Success = false, Item = item, Error = ex.Message });
                if (stopOnError) break;
            }
            // BUG-BT2: per-item unrecognized-LaTeX diagnostics. The handler
            // resets LastUnrecognizedLatex on every Add/Set, so its tokens are
            // only valid for the item just executed — collect them now,
            // de-duplicated, so the caller can surface the same
            // unrecognized_latex_command warning (and exit 2) that single-shot
            // add/set produce. Without this the warnings were silently
            // swallowed by the batch path.
            if (unrecognizedLatex != null)
            {
                var toks = handler switch
                {
                    OfficeCli.Handlers.WordHandler wlx => wlx.LastUnrecognizedLatex,
                    OfficeCli.Handlers.PowerPointHandler plx => plx.LastUnrecognizedLatex,
                    _ => null,
                };
                if (toks is { Count: > 0 })
                    foreach (var t in toks)
                        if (!unrecognizedLatex.Contains(t)) unrecognizedLatex.Add(t);
            }
        }
        return results;
    }

    /// <summary>
    /// Run a batch against a freshly-opened, dispose-on-return handler (the
    /// non-resident CLI path and the MCP server). Sets <c>DeferSave</c> so the
    /// N commands serialize the part once at Dispose instead of N times — the
    /// O(N²) re-serialize that dominates large replays. The handler is NOT
    /// disposed here; the caller's <c>using</c> performs the single
    /// <c>FinalizeDeferredIds + Save</c> flush. Output formatting and the
    /// protection gate stay with the caller (their surfaces differ).
    /// </summary>
    internal static List<BatchResult> RunNonResidentBatch(
        OfficeCli.Core.IDocumentHandler handler, List<BatchItem> items,
        bool stopOnError, bool json, ICollection<string>? unrecognizedLatex = null)
    {
        if (handler is OfficeCli.Handlers.WordHandler wh) wh.DeferSave = true;
        return ApplyBatchItems(handler, items, stopOnError, json, unrecognizedLatex: unrecognizedLatex);
    }

    private static Command BuildBatchCommand(Option<bool> jsonOption)
    {
        var batchFileArg = new Argument<FileInfo>("file") { Description = "Office document path" };
        var batchInputOpt = new Option<FileInfo?>("--input") { Description = "JSON file containing batch commands. If omitted, reads from stdin" };
        var batchCommandsOpt = new Option<string?>("--commands") { Description = "Inline JSON array of batch commands (alternative to --input or stdin)" };
        // BUG-R4-BT2: default flipped to continue-on-error. A 700-command
        // dump replay losing 80% of the document on the first failing item
        // (e.g. one unsupported prop) is a far worse default than reporting
        // the failure and letting the rest of the batch through. Errors are
        // still surfaced individually (BatchResult.Error) and the overall
        // exit code is 1 if any item failed, so callers can still tell
        // "everything succeeded". `--stop-on-error` opts back into the
        // strict abort-on-first-failure flow for callers who depend on it.
        var batchForceOpt = new Option<bool>("--force") { Description = "Deprecated alias for the default continue-on-error mode (kept for compatibility)" };
        var batchStopOpt = new Option<bool>("--stop-on-error") { Description = "Abort the batch as soon as any command fails (default: continue and report per-item errors)" };
        var batchCommand = new Command("batch", BatchHelpDescription);
        batchCommand.Add(batchFileArg);
        batchCommand.Add(batchInputOpt);
        batchCommand.Add(batchCommandsOpt);
        batchCommand.Add(batchForceOpt);
        batchCommand.Add(batchStopOpt);
        batchCommand.Add(jsonOption);

        batchCommand.SetAction(result => { var json = result.GetValue(jsonOption); return SafeRun(() =>
        {
            var file = result.GetValue(batchFileArg)!;
            var inputFile = result.GetValue(batchInputOpt);
            var inlineCommands = result.GetValue(batchCommandsOpt);
            // Default: continue on error. --stop-on-error flips it to strict.
            // --force still acts as the docx-protection bypass (matches set
            // --force semantics) but no longer doubles as the continue-on-
            // error switch.
            var stopOnError = result.GetValue(batchStopOpt);
            var forceFlag = result.GetValue(batchForceOpt);

            string jsonText;
            // BUG-R7-09 (F-6): previously --commands/--input/stdin were
            // silently prioritized in that order — passing two of them at
            // once dropped the lower-priority source with no warning, so
            // scripts could fail subtly when an agent piped data into a
            // command that already had --commands set. Reject the
            // combination loudly. (Detect stdin via Console.IsInputRedirected
            // to avoid spurious failures from interactive terminals.)
            // IsInputRedirected alone is true for every non-interactive
            // invocation (cron, CI, `< /dev/null`, systemd), so the warning
            // below fired on effectively all scripted batch runs with only
            // one source supplied. Refine: a seekable stdin (regular file or
            // /dev/null redirect) with zero length carries no second payload
            // — skip the warning. Pipes (CanSeek=false) still warn: someone
            // is actively piping data that will be ignored.
            bool stdinHasInput = Console.IsInputRedirected;
            if (stdinHasInput)
            {
                // Peek with a short timeout: /dev/null and closed stdin hit
                // EOF instantly (no payload → no warning); a pipe carrying a
                // real second payload has data ready (warn); an open-but-idle
                // pipe times out and is treated as no payload — batch never
                // reads stdin on this path anyway, so nothing is lost. The
                // possibly-blocked Peek thread is abandoned; the process
                // exits normally.
                try
                {
                    var stdinPeek = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { return Console.In.Peek() != -1; }
                        catch { return false; }
                    });
                    stdinHasInput = stdinPeek.Wait(TimeSpan.FromMilliseconds(50)) && stdinPeek.Result;
                }
                catch { /* keep IsInputRedirected verdict */ }
            }
            if (inlineCommands != null && inputFile != null)
                throw new ArgumentException(
                    "batch: --commands and --input are mutually exclusive. Pick one source.");
            // '--input -' explicitly opts INTO stdin — don't emit the
            // "stdin will be ignored" warning in that case, since stdin
            // is exactly what will be read.
            var inputIsStdinAlias = inputFile != null && inputFile.Name == "-";
            if ((inlineCommands != null || (inputFile != null && !inputIsStdinAlias)) && stdinHasInput
                && Environment.GetEnvironmentVariable("OFFICECLI_BATCH_ALLOW_STDIN_REDIRECT") == null)
            {
                Console.Error.WriteLine(
                    "Warning: batch is reading from --commands/--input but stdin is also redirected; "
                    + "stdin will be ignored. Pass only one source to silence this warning, or set "
                    + "OFFICECLI_BATCH_ALLOW_STDIN_REDIRECT=1.");
            }
            if (inlineCommands != null)
            {
                jsonText = inlineCommands;
            }
            else if (inputFile != null)
            {
                // Accept the conventional Unix '-' alias for stdin so
                // pipelines like `cat ops.json | officecli batch foo.pptx --input -`
                // don't have to drop --input entirely. Matches the implicit
                // "no --input ⇒ read stdin" branch below; using --input -
                // makes the intent explicit instead of relying on the
                // absent-flag default. (TargetMode/Exists checks are
                // skipped on purpose — '-' is not a path.)
                if (inputFile.Name == "-")
                {
                    jsonText = StripBom(Console.In.ReadToEnd());
                }
                else
                {
                    if (!inputFile.Exists)
                    {
                        throw new FileNotFoundException($"Input file not found: {inputFile.FullName}");
                    }
                    jsonText = File.ReadAllText(inputFile.FullName);
                }
            }
            else
            {
                // Read from stdin. File.ReadAllText auto-detects and strips
                // UTF-8 BOM; Console.In does not. Without an explicit strip,
                // `cat utf8bom.json | officecli batch foo.pptx` failed
                // System.Text.Json.Parse with "'﻿' is an invalid start of
                // a value" while `batch --input utf8bom.json` succeeded —
                // splitting the contract on the input source.
                jsonText = StripBom(Console.In.ReadToEnd());
            }

            // Pre-validate: check for unknown JSON fields before deserializing
            var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);
            // CONSISTENCY(dump-batch-pipeline): `dump --json` wraps the
            // BatchItem array in an envelope object (`{"success":true,
            // "data":[…]}` via OutputFormatter.WrapEnvelope). The natural
            // pipeline `dump --json > out.json && batch --input out.json`
            // otherwise threw "Batch input must be a JSON array" because the
            // root is an object. Auto-unwrap when the envelope has a `data`
            // array so the pipeline just works without an extra `jq .data`
            // step. Bare-array inputs (dump without --json) are unaffected.
            if (jsonDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && jsonDoc.RootElement.TryGetProperty("data", out var envData)
                && envData.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                jsonText = envData.GetRawText();
                jsonDoc.Dispose();
                jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);
            }
            using var _jsonDocOwner = jsonDoc;
            var rootKind = jsonDoc.RootElement.ValueKind;
            if (rootKind != System.Text.Json.JsonValueKind.Array
                && rootKind != System.Text.Json.JsonValueKind.Null)
            {
                // BUG-R7-10: when the batch input is a JSON object/string/etc.
                // (not an array), Deserialize<List<BatchItem>> threw a generic
                // JsonException whose message exposed the C# generic type name
                // (`System.Collections.Generic.List`1[OfficeCli.BatchItem]`).
                // Convert it to a human-friendly error first so AI agents and
                // humans see a stable, model-agnostic diagnostic.
                throw new ArgumentException(
                    $"Batch input must be a JSON array. Got: {rootKind.ToString().ToLowerInvariant()}. "
                    + "Wrap a single item like [{\"command\":\"get\",\"path\":\"/\"}].");
            }
            if (jsonDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int ri = 0;
                foreach (var elem in jsonDoc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var unknown = new List<string>();
                        foreach (var prop in elem.EnumerateObject())
                        {
                            if (!BatchItem.KnownFields.Contains(prop.Name))
                                unknown.Add(prop.Name);
                        }
                        if (unknown.Count > 0)
                            throw new ArgumentException($"batch item[{ri}]: unknown field(s) {string.Join(", ", unknown.Select(f => $"\"{f}\""))}. Valid fields: {string.Join(", ", BatchItem.KnownFields)}");
                    }
                    ri++;
                }
            }

            var items = System.Text.Json.JsonSerializer.Deserialize<List<BatchItem>>(jsonText, BatchJsonContext.Default.ListBatchItem) ?? new();
            // BUG-R40-B11: explicit null entries (e.g. `[null]`) deserialize
            // to a List<BatchItem> with a null slot and trip a NRE deeper in
            // ExecuteBatchItem. Reject up-front with a recognizable error
            // pointing at the offending index.
            for (int ni = 0; ni < items.Count; ni++)
            {
                if (items[ni] == null)
                    throw new ArgumentException(
                        $"batch item[{ni}] is null. Each entry must be a JSON object (e.g. {{\"command\":\"get\",\"path\":\"/\"}}).");
            }
            if (items.Count == 0)
            {
                // BUG-R6-07: empty command array previously short-circuited
                // before the file-existence check, so
                //   officecli batch /missing.docx --commands '[]' --json
                // returned a clean zero-result success instead of the
                // expected file_not_found. Validate the target file
                // exists first so empty-array semantics match the
                // non-empty path's diagnostics.
                if (!file.Exists)
                    throw new CliException($"File not found: {file.FullName}")
                        { Code = "file_not_found" };
                // BUG-R7-09: in --json mode an empty/null batch input
                // previously skipped the {"success":...,"data":{...}}
                // envelope used by the populated-array path, so AI agents
                // saw a missing `success` key. Apply the same envelope
                // wrap here for shape parity.
                if (json)
                {
                    using var sw = new System.IO.StringWriter();
                    PrintBatchResults(new List<BatchResult>(), json, 0, sw);
                    var inner = sw.ToString().TrimEnd('\n', '\r');
                    Console.WriteLine(OfficeCli.Core.OutputFormatter.WrapEnvelope(inner));
                }
                else
                {
                    PrintBatchResults(new List<BatchResult>(), json, 0);
                }
                return 0;
            }

            // BUG-FUZZER-R6-03: batch must honour the same .docx document
            // protection check that `set` enforces. Without this, a protected
            // doc could be silently modified via
            //   officecli batch protected.docx --commands '[{"command":"set",...}]'
            // even though the same set issued via the standalone `set` command
            // would be rejected. We piggy-back on `--force` (which already
            // means "ignore safety guards" for the continue-on-error path) so
            // agents that need to override protection use the same flag they
            // already know from `set --force`.
            // CONSISTENCY(docx-protection): if you change the protection
            // semantics, also update CommandBuilder.Set.cs at the matching
            // CheckDocxProtection call site.
            var force = forceFlag;
            // Document protection is gated ONCE per batch against the in-memory
            // DOM, not by reopening the file per item (the old per-item loop did
            // N full document opens — ~10s of I/O for a 16k batch). The check
            // runs where the live tree is available: the resident server checks
            // its in-memory _handler (see ExecuteBatch), and the non-resident
            // path checks just after it opens the handler (below). Reading the
            // live tree — not the on-disk copy — also keeps the gate correct
            // when a resident holds uncommitted in-memory protection changes
            // (resident sessions flush only on save/close/idle-autosave).

            // If a resident process is running, send the entire batch as a
            // single "batch" command so all items run in one pass inside the
            // resident. NOTE: unlike the standalone path, this does NOT save to
            // disk per batch — the resident applies the items in memory and
            // defers the flush to the next save/close/idle-autosave. A reader
            // that bypasses the resident must flush first (see command-open /
            // command-batch wiki "Persisting changes").
            if (ResidentClient.TryConnect(file.FullName, out _))
            {
                var req = new ResidentRequest
                {
                    Command = "batch",
                    Json = json,
                    Args =
                    {
                        ["batchJson"] = jsonText,
                        ["force"] = force.ToString(),
                        ["stopOnError"] = stopOnError.ToString()
                    }
                };
                // CONSISTENCY(resident-two-step): long connectTimeoutMs so the
                // batch waits for its turn in the main-pipe queue instead of
                // silently timing out under load. Matches TryResident in
                // CommandBuilder.cs.
                var response = ResidentClient.TrySend(file.FullName, req, maxRetries: 3, connectTimeoutMs: 30000);
                if (response == null)
                {
                    Console.Error.WriteLine($"Resident for {file.Name} is running but the batch could not be delivered (main pipe busy or unresponsive). Retry, or run 'officecli close {file.Name}' and try again.");
                    return 3;
                }
                // The resident returns the formatted batch output directly
                if (!string.IsNullOrEmpty(response.Stdout))
                    Console.Write(response.Stdout);
                if (!string.IsNullOrEmpty(response.Stderr))
                    Console.Error.Write(response.Stderr);
                return response.ExitCode;
            }

            // Non-resident: open file once, execute all commands, save once.
            // Defer per-mutation Document.Save() so N commands serialize the
            // part once (at Dispose) instead of N times — eliminates an O(N²)
            // re-serialize that dominates large replays. Save-once is the
            // documented intent of this path; per-op Save was redundant given
            // the Dispose-time flush.
            using var handler = DocumentHandlerFactory.Open(file.FullName, editable: true);
            // Protection gate against the just-opened in-memory DOM (one check
            // for the whole batch; no second file open).
            if (!force && file.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                var protBlock = GetBatchProtectionBlock(handler, items);
                if (protBlock != null)
                {
                    if (json)
                        Console.WriteLine(OfficeCli.Core.OutputFormatter.WrapEnvelopeError(protBlock, new List<OfficeCli.Core.CliWarning>()));
                    else
                        Console.Error.WriteLine($"ERROR: {protBlock}");
                    return 1;
                }
            }
            // DeferSave + replay loop, shared with the MCP batch surface. The
            // handler's using-Dispose performs the single FinalizeDeferredIds +
            // Save flush.
            var batchUnrecognizedLatex = new List<string>();
            var batchResults = RunNonResidentBatch(handler, items, stopOnError, json, batchUnrecognizedLatex);
            // BUG-R6-02: in --json mode the non-resident path emitted the raw
            // {"results":...,"summary":...} body while the resident path
            // wrapped it in {"success":..., "data":{...}} (resident server
            // calls OutputFormatter.WrapEnvelope on any JSON-shaped stdout).
            // Capture PrintBatchResults output and apply the same envelope
            // here so callers see the same shape regardless of resident state.
            // JSON Envelope contract: batch is a *judgment* command (root
            // the project conventions "Judgment: any batch step failed -> outer false").
            // Outer envelope.success is true only when every step succeeded;
            // a single failed step flips outer to false even if siblings
            // succeeded. Per-step verdicts still ride on
            // `data.results[].success`. Exit code stays in lockstep with
            // envelope.success so CI gates and shells can rely on the single
            // signal. Two `success` fields appear in the JSON (outer batch
            // verdict, inner per-step) — disambiguate by JSON path.
            var batchSuccess = batchResults.Count == 0 || !batchResults.Any(r => !r.Success);
            // BUG-BT2: surface per-item unrecognized-LaTeX warnings the same way
            // single-shot add/set do (warning + JSON envelope + exit 2). A batch
            // whose only issue is an unknown LaTeX command otherwise exited 0
            // with no diagnostic, silently writing the literal-text fallback.
            var batchWarnings = new List<OfficeCli.Core.CliWarning>();
            foreach (var tok in batchUnrecognizedLatex)
            {
                batchWarnings.Add(new OfficeCli.Core.CliWarning
                {
                    Message = $"unrecognized_latex_command: {tok}",
                    Code = "unrecognized_latex_command",
                    Suggestion = "Check the command spelling; see https://katex.org/docs/supported.html for supported syntax.",
                });
            }
            if (json)
            {
                using var sw = new System.IO.StringWriter();
                PrintBatchResults(batchResults, json, items.Count, sw);
                var inner = sw.ToString().TrimEnd('\n', '\r');
                Console.WriteLine(OfficeCli.Core.OutputFormatter.WrapEnvelope(inner, batchWarnings, success: batchSuccess));
            }
            else
            {
                PrintBatchResults(batchResults, json, items.Count);
                foreach (var w in batchWarnings)
                    Console.Error.WriteLine($"  WARNING: {w.Message}");
            }
            if (batchResults.Any(r => r.Success))
                NotifyWatch(handler, file.FullName, null);
            // Exit precedence: a failed item (exit 1) outranks an
            // unrecognized-LaTeX-only warning (exit 2 mirrors single-shot).
            if (!batchSuccess) return 1;
            return batchWarnings.Count > 0 ? 2 : 0;
        }, json); });

        return batchCommand;
    }

    // UTF-8 BOM trim. File.ReadAllText handles this implicitly via
    // StreamReader's detect-encoding; Console.In feeds raw chars.
    private static string StripBom(string s)
        => !string.IsNullOrEmpty(s) && s[0] == '﻿' ? s.Substring(1) : s;
}
