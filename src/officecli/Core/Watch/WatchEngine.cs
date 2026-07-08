// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace OfficeCli.Core;

/// <summary>
/// Host-agnostic watch state and logic, independent of any web/pipe transport:
/// it owns no sockets and never opens the document. It is being extracted from
/// <see cref="WatchServer"/> incrementally; this step holds the shared selection
/// state, the cached HTML/version, and marks, with message dispatch to follow.
/// </summary>
public sealed class WatchEngine
{
    // Outbound SSE sink. The engine emits mark-update envelopes through this
    // seam so it stays transport-agnostic: the host (e.g. WatchServer) decides
    // how events reach connected browsers.
    private readonly IWatchBroadcaster _broadcaster;

    /// <summary>
    /// Create an engine that emits SSE events through <paramref name="broadcaster"/>.
    /// </summary>
    public WatchEngine(IWatchBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    // Cached full document HTML the watch serves and diffs against, plus a
    // monotonic version counter. HTML is written by the message dispatch and
    // read at an instant by marks/serving — matching the pre-extraction model
    // (no lock around HTML/version).
    private string _currentHtml = "";
    private int _version = 0;

    /// <summary>The cached full document HTML (never null).</summary>
    public string CurrentHtml
    {
        get => _currentHtml;
        set => _currentHtml = value ?? "";
    }

    /// <summary>Monotonic version counter, bumped on each applied update.</summary>
    public int Version => _version;

    /// <summary>Increment the update version counter.</summary>
    public void BumpVersion() => _version++;

    // Current selection — paths of elements selected in any connected browser.
    // Single shared list (last-write-wins): all browsers viewing the same file see
    // the same selection. The CLI reads it via the named-pipe "get-selection" command.
    //
    // CONSISTENCY(path-stability): selection and mark share the same naive positional addressing
    // contract — no fingerprinting, no drift detection. To upgrade to stable IDs,
    // grep "CONSISTENCY(path-stability)" and update every deferred site project-wide in one pass.
    // See CLAUDE.md "Design Principles".
    private List<string> _currentSelection = new();
    private readonly object _selectionLock = new();

    /// <summary>Snapshot of the currently-selected element paths (never null).</summary>
    public string[] GetSelectionSnapshot()
    {
        lock (_selectionLock) { return _currentSelection.ToArray(); }
    }

    /// <summary>Replace the shared selection (last-write-wins across all browsers).</summary>
    public void SetSelection(List<string> paths)
    {
        lock (_selectionLock) { _currentSelection = paths; }
    }

    // Current marks — advisory annotations attached to document paths. Live in
    // memory only: the engine never opens the document and never inspects DOM —
    // marks are pure metadata; the browser computes match positions client-side.
    //
    // CONSISTENCY(path-stability): element-deletion / position-drift handling deliberately matches
    // selection — naive positional addressing, no fingerprint, no drift detection. `stale` is only
    // set when the client reports a path-resolution failure or a `find` miss.
    // See CLAUDE.md "Design Principles" + "Watch Server Rules".
    // To migrate to stable-ID paths, grep "CONSISTENCY(path-stability)" and update every deferred
    // site (selection / mark / any future path consumer) project-wide — never patch mark alone.
    private readonly List<WatchMark> _currentMarks = new();
    private readonly object _marksLock = new();
    private int _marksVersion = 0;
    private int _nextMarkId = 1;

    /// <summary>Atomically snapshot the current marks together with their version.</summary>
    internal (WatchMark[] Marks, int Version) GetMarksAndVersion()
    {
        lock (_marksLock) { return (_currentMarks.ToArray(), _marksVersion); }
    }

    // ==================== Marks ====================

    /// <summary>
    /// Add a new mark. Normalizes find: if regex flag (truthy via the find
    /// payload's "regex" field would be parsed by the CLI side; the server
    /// receives the canonical form already wrapped as r"..." or literal).
    /// However we ALSO accept the bare-find form here so that callers that
    /// don't pre-wrap still get correct behaviour. The CLI passes either
    /// the literal or a pre-wrapped r"..." string.
    /// </summary>
    internal string HandleMarkAdd(string json)
    {
        try
        {
            var req = JsonSerializer.Deserialize(json, WatchMarkJsonContext.Default.MarkRequest);
            if (req == null)
                return "{\"error\":\"invalid request\"}";

            // BUG-FUZZER-003/004: path hardening.
            //   1. Normalize: Trim() strips ASCII + Unicode whitespace from edges.
            //   2. Reject whitespace-only paths (IsNullOrWhiteSpace catches NBSP,
            //      U+3000 ideographic space, etc.).
            //   3. Require leading '/': zero-width space U+200B and BOM U+FEFF
            //      are not .NET whitespace but are never valid data-path prefixes,
            //      so a StartsWith('/') check also filters them out.
            //   4. Store the trimmed form so later `unmark --path /body/p[1]`
            //      matches what the user typed, not `" /body/p[1] "` with padding.
            // BUG-BT-R303: error messages must be actionable for AI agents — say
            // what the accepted format is, not just "invalid".
            var trimmedPath = req.Path?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(trimmedPath) || !trimmedPath.StartsWith("/"))
                return "{\"error\":\"invalid path: must start with '/' (e.g. /body/p[1] for Word, /slide[1]/shape[@id=N] for PowerPoint)\"}";

            // BUG-TESTER-002: validate color server-side. The browser sets
            // el.style.backgroundColor = mark.color verbatim, so an unsanitized
            // value injects CSS into every connected SSE client. Server is the
            // single trust boundary for both human-typed CLI and machine agents.
            // CONSISTENCY(mark-color-validation): one validator, both Add and
            // any future Set/update path must call IsValidMarkColor.
            //
            // BUG-FUZZER-001: Trim() before validation AND before storage, so
            // `"red\n"` doesn't end up stored as `"red\n"` after being accepted
            // (the validator trims for matching but used to leave the raw form
            // in the stored mark, causing a validator-vs-storage inconsistency).
            var trimmedColor = req.Color?.Trim();
            // BUG-A-R2-M01: accept bare hex (FF00FF, F0F) for consistency with the
            // rest of officecli's color parsers. The validator below requires the
            // canonical #-prefixed form, so promote 3/6/8-digit bare hex to that
            // form before validation. Anything else (named colors, rgb(...),
            // already-hashed hex) passes through unchanged.
            trimmedColor = NormalizeMarkColorInput(trimmedColor);
            // BUG-BT-R303: actionable error message — list the accepted formats
            // so AI agents can self-correct without reading the source.
            if (!string.IsNullOrEmpty(trimmedColor) && !IsValidMarkColor(trimmedColor))
                return "{\"error\":\"invalid color: accepted forms are #RGB / #RRGGBB / #RRGGBBAA hex (with or without # prefix), rgb(r,g,b), rgba(r,g,b,a), or named colors (red, blue, yellow, orange, green, purple, ...)\"}";

            var mark = new WatchMark
            {
                Path = trimmedPath,
                Find = req.Find,
                Color = string.IsNullOrEmpty(trimmedColor) ? "#ffeb3b" : trimmedColor,
                Note = req.Note,
                Tofix = req.Tofix,
                MatchedText = Array.Empty<string>(),
                Stale = false,
                CreatedAt = DateTime.UtcNow,
            };

            string assignedId;
            WatchMark[] snapshot;
            string htmlSnapshot;
            lock (_marksLock)
            {
                assignedId = _nextMarkId.ToString();
                _nextMarkId++;
                mark.Id = assignedId;
                // Snapshot _currentHtml under the lock so a concurrent
                // full-refresh can't race the resolve step.
                htmlSnapshot = CurrentHtml;
                var resolved = ResolveMark(mark, htmlSnapshot);
                _currentMarks.Add(resolved);
                _marksVersion++;
                snapshot = _currentMarks.ToArray();
            }
            BroadcastMarkUpdate(snapshot);

            return JsonSerializer.Serialize(
                new MarkResponse { Id = assignedId },
                WatchMarkJsonContext.Default.MarkResponse);
        }
        catch
        {
            return "{\"error\":\"parse failed\"}";
        }
    }

    /// <summary>
    /// Remove marks. UnmarkRequest must have either Path set, or All=true,
    /// not both. Returns the number of marks removed.
    /// </summary>
    internal string HandleMarkRemove(string json)
    {
        try
        {
            var req = JsonSerializer.Deserialize(json, WatchMarkJsonContext.Default.UnmarkRequest);
            if (req == null) return "{\"removed\":0}";

            int removed = 0;
            WatchMark[] snapshot;
            lock (_marksLock)
            {
                if (req.All)
                {
                    removed = _currentMarks.Count;
                    _currentMarks.Clear();
                }
                else
                {
                    // BUG-FUZZER-003/004: Trim and require leading '/' for symmetry
                    // with HandleMarkAdd. Without Trim a `unmark --path " /p[1] "`
                    // would silently miss a mark added as `/p[1]` and vice versa.
                    var unmarkPath = req.Path?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(unmarkPath) && unmarkPath.StartsWith("/"))
                    {
                        removed = _currentMarks.RemoveAll(m =>
                            string.Equals(m.Path, unmarkPath, StringComparison.Ordinal));
                    }
                }
                if (removed > 0) _marksVersion++;
                snapshot = _currentMarks.ToArray();
            }
            if (removed > 0) BroadcastMarkUpdate(snapshot);

            return JsonSerializer.Serialize(
                new UnmarkResponse { Removed = removed },
                WatchMarkJsonContext.Default.UnmarkResponse);
        }
        catch
        {
            return "{\"removed\":0}";
        }
    }

    /// <summary>Test-only accessor for current marks snapshot.</summary>
    internal WatchMark[] GetMarksSnapshot()
    {
        lock (_marksLock) { return _currentMarks.ToArray(); }
    }

    /// <summary>Test-only accessor for the current marks version.</summary>
    internal int GetMarksVersion()
    {
        lock (_marksLock) { return _marksVersion; }
    }

    /// <summary>
    /// Test-only hook: install a full HTML snapshot synchronously and trigger
    /// mark reconciliation. Used by WatchMarkTests to verify ResolveMark without
    /// racing the pipe's "ack first, process later" ordering.
    /// </summary>
    internal void ApplyFullHtmlForTests(string html)
    {
        CurrentHtml = html;
        BumpVersion();
        ReconcileAllMarks();
    }

    /// <summary>
    /// Re-run ResolveMark on every mark in the current list. Called when the
    /// cached HTML snapshot changes (document reload / full refresh). Updates
    /// each mark's MatchedText and Stale in place and bumps _marksVersion so
    /// clients that missed the change can detect it.
    /// </summary>
    internal void ReconcileAllMarks()
    {
        WatchMark[] snapshot;
        lock (_marksLock)
        {
            if (_currentMarks.Count == 0) return;
            for (int i = 0; i < _currentMarks.Count; i++)
            {
                _currentMarks[i] = ResolveMark(_currentMarks[i], CurrentHtml);
            }
            _marksVersion++;
            snapshot = _currentMarks.ToArray();
        }
        BroadcastMarkUpdate(snapshot);
    }

    /// <summary>
    /// Wrap a WatchMark[] snapshot in a "mark-update" SSE envelope. Called
    /// after every mark add/remove, and during initial SSE client handshake.
    /// The version field is a monotonically-increasing counter that clients
    /// can use for CAS-style update detection.
    ///
    /// Uses the Relaxed encoder so CJK find/note/tofix bytes flow through
    /// as literal characters instead of \uXXXX escapes.
    /// </summary>
    internal static string BuildMarkUpdateJson(WatchMark[] marks, int version)
    {
        var marksJson = JsonSerializer.Serialize(marks, WatchMarkJsonOptions.WatchMarkArrayInfo);
        return $"{{\"action\":\"mark-update\",\"version\":{version},\"marks\":{marksJson}}}";
    }

    private void BroadcastMarkUpdate(WatchMark[] marks)
    {
        int version;
        lock (_marksLock) { version = _marksVersion; }
        _broadcaster.Broadcast(new WatchSseEvent(BuildMarkUpdateJson(marks, version)));
    }

    // -------- Mark resolution (server-side reconcile) --------
    //
    // CONSISTENCY(path-stability): resolution uses naive positional
    // data-path lookup — no fingerprinting, no drift detection. If an
    // element is later removed or its find target no longer matches,
    // the mark is flipped to Stale=true with MatchedText=[]. Same
    // limitations as selection. grep "CONSISTENCY(path-stability)" for
    // all deferred sites that should move together if we ever switch
    // to stable IDs. See CLAUDE.md "Watch Server Rules".
    //
    // watch-isolation: this code runs pure-regex string-scraping on
    // the html snapshot already cached in _currentHtml. It does not
    // open the document, does not depend on OfficeCli.Handlers, and
    // does not reference any DOM parser. A real HTML parser would be
    // more correct but would introduce coupling; the MVP trades
    // precision for isolation and matches the browser-side
    // applyMarks() fallback behaviour.

    private static readonly System.Text.RegularExpressions.Regex _tagStripRx =
        new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    // BUG-TESTER-001: ResolveMark accepts arbitrary user regex via r"..." find
    // strings. A catastrophically backtracking pattern (e.g. r"(a+)+$") against
    // a long input would freeze the watch reconcile loop indefinitely. Bound
    // every user-supplied regex evaluation with this match timeout.
    private static readonly TimeSpan MarkRegexMatchTimeout = TimeSpan.FromMilliseconds(500);

    // BUG-TESTER-003: <script> and <style> bodies must be removed entirely
    // before tag-stripping, otherwise their inner text leaks into find matching
    // (e.g. find="secret" hits "<script>secret data</script>"). These regexes
    // strip the element including children, case-insensitive, dot-matches-newline.
    private static readonly System.Text.RegularExpressions.Regex _scriptBodyRx =
        new("<script\\b[^>]*>.*?</script\\s*>",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex _styleBodyRx =
        new("<style\\b[^>]*>.*?</style\\s*>",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);

    // BUG-TESTER-002: server-side color whitelist for mark.color. Anything
    // accepted here gets written verbatim into el.style.backgroundColor on
    // every connected browser, so the validator must REJECT anything that
    // isn't unambiguously a color value. Three accepted shapes:
    //   1. #RGB / #RRGGBB / #RRGGBBAA hex
    //   2. rgb(r,g,b) / rgba(r,g,b,a) with numeric components
    //   3. one of the named colors in MarkNamedColors
    // CONSISTENCY(mark-color-validation): grep this tag if expanding the set.
    private static readonly System.Text.RegularExpressions.Regex _hexColorRx =
        new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _rgbFuncRx =
        new("^rgba?\\(\\s*\\d+(?:\\.\\d+)?\\s*,\\s*\\d+(?:\\.\\d+)?\\s*,\\s*\\d+(?:\\.\\d+)?(?:\\s*,\\s*\\d+(?:\\.\\d+)?)?\\s*\\)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly HashSet<string> MarkNamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "red", "green", "blue", "yellow", "orange", "purple", "pink", "cyan",
        "magenta", "brown", "black", "white", "gray", "grey", "lime", "teal",
        "navy", "olive", "maroon", "silver", "gold", "transparent",
    };

    // BUG-A-R2-M01 / BUG-TESTER-R302: Promote bare 3-, 6-, or 8-digit hex to
    // #-prefixed form so the validator and storage match the rest of officecli's
    // color convention. Returns the input unchanged for any other shape (named,
    // rgb(...), already #-prefixed, or null/empty). Idempotent.
    private static readonly System.Text.RegularExpressions.Regex _bareHex6Rx =
        new("^[0-9a-fA-F]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _bareHex3Rx =
        new("^[0-9a-fA-F]{3}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _bareHex8Rx =
        new("^[0-9a-fA-F]{8}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    internal static string? NormalizeMarkColorInput(string? color)
    {
        if (string.IsNullOrEmpty(color)) return color;
        if (color[0] == '#') return color;
        if (_bareHex6Rx.IsMatch(color))
            return "#" + color.ToUpperInvariant();
        if (_bareHex8Rx.IsMatch(color))
            return "#" + color.ToUpperInvariant();
        if (_bareHex3Rx.IsMatch(color))
        {
            var c = color.ToUpperInvariant();
            return $"#{c[0]}{c[0]}{c[1]}{c[1]}{c[2]}{c[2]}";
        }
        return color;
    }

    internal static bool IsValidMarkColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        var c = color.Trim();
        if (c.Length > 64) return false; // defensive bound
        if (MarkNamedColors.Contains(c)) return true;
        if (_hexColorRx.IsMatch(c)) return true;
        if (_rgbFuncRx.IsMatch(c)) return true;
        return false;
    }

    /// <summary>
    /// HTML-encode an attribute value mirroring how the renderer escapes
    /// data-path. Only the characters that change inside double-quoted
    /// attribute values matter (&, &lt;, &gt;, &quot;, &#39; / &apos;).
    /// </summary>
    private static string HtmlEncodeAttributeValue(string value)
    {
        // Order matters: replace '&' first so subsequent ampersand-introducing
        // entities aren't re-encoded.
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Locate the element with the given data-path in the cached HTML snapshot
    /// and return its inner HTML fragment (start tag + children + end tag).
    /// Uses bracket-depth counting of sibling tags to find the matching close.
    /// Returns null if the path is not present.
    /// </summary>
    private static string? FindDataPathInHtml(string html, string path)
    {
        // CONSISTENCY(pptx-group-flatten): query may emit paths that point
        // inside a group (`/slide[1]/group[2]/shape[3]`), but HtmlPreview
        // currently only emits data-path on the outer group — see the
        // CONSISTENCY note in PowerPointHandler.HtmlPreview.Shapes.cs:~1040.
        // If the exact path isn't in the rendered HTML, walk up one segment
        // at a time and try again so a mark on a group-internal shape
        // resolves to the nearest ancestor that *is* rendered. Text-based
        // find/replace still runs against the ancestor's full text content,
        // so highlighting + find still work — only the visual outline drops
        // to the group level.
        var direct = FindDataPathInHtmlExact(html, path);
        if (direct != null) return direct;

        var current = path;
        while (true)
        {
            var lastSlash = current.LastIndexOf('/');
            if (lastSlash <= 0) return null;
            current = current.Substring(0, lastSlash);
            var hit = FindDataPathInHtmlExact(html, current);
            if (hit != null) return hit;
        }
    }

    private static string? FindDataPathInHtmlExact(string html, string path)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(path)) return null;
        // Anchor the search on the data-path attribute. Path may contain [] so
        // we match it as a literal substring inside quotes.
        // BUG-FIX(B9): the HTML emitter encodes attribute values, so a path
        // like /shape[@name="Foo"] is rendered as data-path="/shape[@name=&quot;Foo&quot;]".
        // Match against the encoded form so paths containing ", ', <, >, & don't
        // always come back stale.
        var encodedPath = HtmlEncodeAttributeValue(path);
        var marker = "data-path=\"" + encodedPath + "\"";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        // Walk back to the opening '<' of this element's start tag.
        var start = html.LastIndexOf('<', idx);
        if (start < 0) return null;
        // Find the end of the start tag.
        var startEnd = html.IndexOf('>', idx);
        if (startEnd < 0) return null;
        // Self-closing tag? (extremely unlikely for data-path targets but be safe)
        if (html[startEnd - 1] == '/')
            return html.Substring(start, startEnd - start + 1);
        // Extract the tag name so we can match its close.
        var tagEnd = start + 1;
        while (tagEnd < html.Length && !char.IsWhiteSpace(html[tagEnd]) && html[tagEnd] != '>')
            tagEnd++;
        var tag = html.Substring(start + 1, tagEnd - start - 1).ToLowerInvariant();
        var openToken = "<" + tag;
        var closeToken = "</" + tag;
        // Count nested open/close to find the matching close tag.
        var depth = 1;
        var cursor = startEnd + 1;
        while (cursor < html.Length && depth > 0)
        {
            var nextOpen = html.IndexOf(openToken, cursor, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf(closeToken, cursor, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return null;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Ensure the candidate open isn't actually part of a longer tag name
                var after = nextOpen + openToken.Length;
                if (after < html.Length && (html[after] == ' ' || html[after] == '>' || html[after] == '\t' || html[after] == '\n'))
                {
                    depth++;
                    cursor = after;
                    continue;
                }
                cursor = nextOpen + openToken.Length;
                continue;
            }
            depth--;
            cursor = nextClose + closeToken.Length;
            if (depth == 0)
            {
                // Advance past the close tag's '>'
                var gt = html.IndexOf('>', cursor);
                if (gt < 0) return null;
                return html.Substring(start, gt - start + 1);
            }
        }
        return null;
    }

    /// <summary>
    /// Existence check for the small set of CSS selectors emitted by
    /// WatchNotifier.ExtractWordScrollTarget — `#anchor` (id=) or
    /// `[data-path="..."]`. Pure substring scan over the cached HTML;
    /// no DOM parser, mirrors FindDataPathInHtml's design.
    /// CONSISTENCY(watch-isolation): only the cached HTML is read.
    /// </summary>
    internal static bool SelectorExistsInHtml(string html, string selector)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(selector)) return false;

        // [data-path="..."] form
        var dpMatch = System.Text.RegularExpressions.Regex.Match(
            selector, @"^\[data-path=""(.+)""\]$");
        if (dpMatch.Success)
        {
            var path = dpMatch.Groups[1].Value;
            // CONSISTENCY(pptx-group-flatten): mirror FindDataPathInHtml's
            // ancestor fallback so a goto target inside a group still scrolls
            // to the nearest rendered ancestor instead of being rejected.
            return FindDataPathInHtml(html, path) != null;
        }

        // #anchor-id form
        if (selector.StartsWith("#"))
        {
            var id = selector.Substring(1);
            return html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal) >= 0
                || html.IndexOf("id='" + id + "'", StringComparison.Ordinal) >= 0;
        }

        // Unknown selector form — let it through (best-effort) so future
        // anchor styles aren't blocked.
        return true;
    }

    /// <summary>
    /// Extract plain text content from an HTML fragment: strip all tags, decode
    /// HTML entities, collapse whitespace minimally, and NFC-normalize. Pure
    /// regex — no DOM parser dependency.
    /// </summary>
    internal static string ExtractTextContent(string htmlFragment)
    {
        if (string.IsNullOrEmpty(htmlFragment)) return "";
        // BUG-TESTER-003: drop <script>...</script> and <style>...</style> bodies
        // BEFORE per-tag stripping. _tagStripRx only removes tags, so without
        // this step inner JS/CSS text leaks into find matching.
        var noScript = _scriptBodyRx.Replace(htmlFragment, "");
        var noStyle = _styleBodyRx.Replace(noScript, "");
        var stripped = _tagStripRx.Replace(noStyle, "");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        try { return decoded.Normalize(System.Text.NormalizationForm.FormC); }
        catch { return decoded; }
    }

    /// <summary>
    /// Resolve a mark against the current HTML snapshot: populate
    /// MatchedText and Stale based on whether the path still resolves
    /// and whether find still matches.
    ///
    /// Pure function: returns a new WatchMark, does not mutate the input.
    /// The caller is responsible for locking _marksLock if it's writing back
    /// into _currentMarks.
    /// </summary>
    internal static WatchMark ResolveMark(WatchMark mark, string currentHtml)
    {
        var resolved = new WatchMark
        {
            Id = mark.Id,
            Path = mark.Path,
            Find = mark.Find,
            Color = mark.Color,
            Note = mark.Note,
            Tofix = mark.Tofix,
            CreatedAt = mark.CreatedAt,
            // Defaults get overwritten below.
            MatchedText = Array.Empty<string>(),
            Stale = false,
        };

        if (string.IsNullOrEmpty(currentHtml))
        {
            // No snapshot yet (watch just started, first refresh not arrived) —
            // treat as "not resolvable yet" but don't flag stale: the CLI may
            // be adding marks before the first render. Stale stays false.
            return resolved;
        }

        var fragment = FindDataPathInHtml(currentHtml, mark.Path);
        if (fragment == null)
        {
            resolved.Stale = true;
            return resolved;
        }

        if (string.IsNullOrEmpty(mark.Find))
        {
            // Whole-element mark — no text matching needed.
            return resolved;
        }

        var text = ExtractTextContent(fragment);
        var find = mark.Find;

        // CONSISTENCY(find-regex): r"..." / r'...' raw-string prefix detection
        // matches WordHandler.Set.cs:60-61 and CommandBuilder.Mark.cs. Keep in
        // sync. grep "CONSISTENCY(find-regex)" for every project-wide site.
        bool isRegex = find.Length >= 3
            && find[0] == 'r'
            && (find[1] == '"' || find[1] == '\'')
            && find[^1] == find[1];

        if (isRegex)
        {
            var pattern = find.Substring(2, find.Length - 3);
            try
            {
                // BUG-TESTER-001: bound the match with MarkRegexMatchTimeout so a
                // catastrophic backtracker cannot freeze the reconcile loop.
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    text, pattern,
                    System.Text.RegularExpressions.RegexOptions.None,
                    MarkRegexMatchTimeout);
                if (matches.Count == 0)
                {
                    resolved.Stale = true;
                    return resolved;
                }
                var list = new string[matches.Count];
                for (int i = 0; i < matches.Count; i++) list[i] = matches[i].Value;
                resolved.MatchedText = list;
                return resolved;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // Pattern took too long against this input → treat as stale with
                // empty matches. Future reconciles will retry against fresh HTML.
                resolved.Stale = true;
                resolved.MatchedText = Array.Empty<string>();
                return resolved;
            }
            catch
            {
                // Bad regex → treat as no match, stale.
                resolved.Stale = true;
                return resolved;
            }
        }
        else
        {
            var needle = find;
            try { needle = needle.Normalize(System.Text.NormalizationForm.FormC); } catch { }
            if (text.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                resolved.Stale = true;
                return resolved;
            }
            resolved.MatchedText = new[] { needle };
            return resolved;
        }
    }


    // -------- HTML patch/diff helpers (pure string transforms) --------

    /// <summary>Replace a single slide fragment in the full HTML by data-slide number.</summary>
    internal static string PatchSlideInHtml(string html, int slideNum, string newFragment)
    {
        var (start, end) = FindSlideFragmentRange(html, slideNum);
        if (start < 0) return html;
        return string.Concat(html.AsSpan(0, start), newFragment, html.AsSpan(end));
    }

    /// <summary>Append a slide fragment before the last closing tag of the main container.</summary>
    internal static string AppendSlideToHtml(string html, string fragment)
    {
        // Find the last </div> before </body> — that's the .main container's closing tag
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0) return html + fragment;
        // Find the </div> just before </body>
        var mainClose = html.LastIndexOf("</div>", bodyClose, StringComparison.OrdinalIgnoreCase);
        if (mainClose < 0) return html;
        return string.Concat(html.AsSpan(0, mainClose), fragment, "\n", html.AsSpan(mainClose));
    }

    /// <summary>Remove a slide fragment from the full HTML.</summary>
    internal static string RemoveSlideFromHtml(string html, int slideNum)
    {
        var (start, end) = FindSlideFragmentRange(html, slideNum);
        if (start < 0) return html;
        return string.Concat(html.AsSpan(0, start), html.AsSpan(end));
    }

    /// <summary>Find the start/end character positions of a slide-container div in the HTML.</summary>
    private static (int Start, int End) FindSlideFragmentRange(string html, int slideNum)
    {
        // The sidebar also emits `<div class="thumb" data-slide="N">`, so matching
        // on `data-slide="N"` alone hits the thumb first and leaves the main
        // slide-container stale — user-visible as a white main view on every
        // incremental update. Pin to the slide-container class.
        var marker = $"class=\"slide-container\" data-slide=\"{slideNum}\"";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return (-1, -1);

        var start = html.LastIndexOf("<div ", idx, StringComparison.Ordinal);
        if (start < 0) return (-1, -1);

        // Find matching closing </div> by counting nesting
        var depth = 0;
        var pos = start;
        while (pos < html.Length)
        {
            var nextOpen = html.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) break;

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                pos = nextOpen + 4;
            }
            else
            {
                depth--;
                if (depth == 0)
                    return (start, nextClose + 6);
                pos = nextClose + 6;
            }
        }

        return (-1, -1);
    }

    /// <summary>Extract all &lt;style&gt; blocks from HTML head, concatenated.</summary>
    internal static string? ExtractStyleBlock(string html)
    {
        var sb = new StringBuilder();
        var idx = 0;
        while (true)
        {
            var start = html.IndexOf("<style>", idx, StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = html.IndexOf("<style ", idx, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = html.IndexOf("</style>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;
            end += 8; // include </style>
            sb.Append(html, start, end - start);
            idx = end;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Split Word HTML into blocks keyed by block number. Returns dict of blockNum → content.</summary>
    private static Dictionary<int, string> SplitWordBlocks(string html)
    {
        var blocks = new Dictionary<int, string>();
        var beginRx = new System.Text.RegularExpressions.Regex(@"<span class=""wb"" data-block=""(\d+)"" style=""display:none""></span>");
        var matches = beginRx.Matches(html);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var blockNum = int.Parse(m.Groups[1].Value);
            var contentStart = m.Index + m.Length;
            var endMarker = $"<span class=\"we\" data-block=\"{blockNum}\" style=\"display:none\"></span>";
            var endIdx = html.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
            if (endIdx >= 0)
                blocks[blockNum] = html[contentStart..endIdx];
        }
        return blocks;
    }

    /// <summary>Compute block-level patches between old and new Word HTML. Returns null if diff is too large (fallback to full).</summary>
    internal static List<WordPatch>? ComputeWordPatches(string oldHtml, string newHtml)
    {
        // Only diff if both are Word documents with block markers
        if (string.IsNullOrEmpty(oldHtml) || string.IsNullOrEmpty(newHtml))
            return null;
        if (!oldHtml.Contains("data-block=\"1\"") || !newHtml.Contains("data-block=\"1\""))
            return null;

        // Section count change → fall back to full diff. Block <wb>/<we>
        // markers can straddle a section boundary (e.g. when a new section
        // is appended, the trailing block's <wb> sits in the prior section's
        // page-body and its <we> in the new section's page-body). Treating
        // that span as block content would inject structural markup
        // (</page-body></page></page-wrapper><page-wrapper data-section="N">…)
        // into the previous section's page-body, producing nested pages.
        var oldSecCount = System.Text.RegularExpressions.Regex.Matches(oldHtml, @"data-section=""\d+""").Count;
        var newSecCount = System.Text.RegularExpressions.Regex.Matches(newHtml, @"data-section=""\d+""").Count;
        if (oldSecCount != newSecCount) return null;

        var oldBlocks = SplitWordBlocks(oldHtml);
        var newBlocks = SplitWordBlocks(newHtml);

        if (oldBlocks.Count == 0 && newBlocks.Count == 0) return null;

        var patches = new List<WordPatch>();

        // Find max block number across both
        var maxBlock = 0;
        foreach (var k in oldBlocks.Keys) if (k > maxBlock) maxBlock = k;
        foreach (var k in newBlocks.Keys) if (k > maxBlock) maxBlock = k;

        for (int b = 1; b <= maxBlock; b++)
        {
            var inOld = oldBlocks.TryGetValue(b, out var oldContent);
            var inNew = newBlocks.TryGetValue(b, out var newContent);

            if (inOld && inNew)
            {
                if (oldContent != newContent)
                    patches.Add(new WordPatch { Op = "replace", Block = b, Html = newContent });
                // else: unchanged, skip
            }
            else if (!inOld && inNew)
            {
                patches.Add(new WordPatch { Op = "add", Block = b, Html = newContent });
            }
            else if (inOld && !inNew)
            {
                patches.Add(new WordPatch { Op = "remove", Block = b });
            }
        }

        if (patches.Count == 0) return null; // no changes

        // A block's <wb>…<we> markers can straddle a structural container, so its
        // captured content is structurally unbalanced — it opens a container it
        // never closes, or closes one it never opened. Known cases:
        //   • a paragraph with an inline <w:br type="page"/> — its span includes
        //     </page-body></page></page-wrapper><div class="page-wrapper">…<page-body>
        //     (page count is unchanged, so the section-count guard misses it);
        //   • a list — the <ol>/<ul> opens in the list block but the matching
        //     </ol>/</ul> closes inside the NEXT block's span;
        //   • multi-column / drop-cap wrappers split across blocks the same way.
        // Re-applying such a payload via innerHTML corrupts the live DOM (the
        // sibling-walk in wordPatchUpdate can't cross the container boundary):
        // an injected page-wrapper nests a page inside a page; an orphaned
        // </ol> wipes the list. Detect the straddle on the patch payload and
        // fall back to a full refresh, which rebuilds the structure correctly.
        foreach (var p in patches)
            if (WordPatchPayloadStraddlesStructure(p.Html))
                return null;

        // If more than 60% of blocks changed (and enough blocks to matter), fallback to full refresh
        var totalBlocks = Math.Max(oldBlocks.Count, newBlocks.Count);
        if (totalBlocks >= 5 && patches.Count > totalBlocks * 0.6)
            return null;

        return patches;
    }

    // Matches any HTML start/end tag: group1 = "/" for an end tag, group2 = tag
    // name, group3 = "/" for an explicit self-close (<x/>). Comments (<!-- -->)
    // and the XML/doctype declarations don't match — group2 requires a leading
    // ASCII letter. Attribute values never contain a raw '>' (the renderer
    // HTML-encodes them), so a greedy `[^>]*?` to the tag's own '>' is safe.
    private static readonly System.Text.RegularExpressions.Regex _htmlTagRx =
        new(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)\b[^>]*?(/?)>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Tags excluded from the balance count. Two groups, same reason — neither
    // can make a block straddle a structural boundary:
    //   • void elements — never carry children (<br>, <img>, <col> …);
    //   • inline elements — the renderer always opens AND closes them within a
    //     single run/paragraph render, so they are self-contained inside one
    //     block by construction. Skipping them also hardens the balance count
    //     against malformed inline markup buried in an attribute value (a raw
    //     '>' the real renderer would have encoded as &gt;).
    // Everything NOT in this set is treated as a potential block-level container
    // and counted — so a future block container the renderer starts emitting is
    // covered without editing this list. grep CONSISTENCY(word-patch-straddle).
    private static readonly HashSet<string> _inlineOrVoidHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // void
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
        // inline / phrasing
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "dfn", "em",
        "font", "i", "kbd", "label", "mark", "q", "rp", "rt", "ruby", "s",
        "samp", "small", "span", "strong", "sub", "sup", "time", "u", "var",
    };

    /// <summary>
    /// True when a Word block-diff patch payload is unsafe to splice into the
    /// live DOM incrementally — i.e. the source block's &lt;wb&gt;/&lt;we&gt;
    /// markers straddle a structural element.
    ///
    /// Root invariant (not a list of known cases): the client splice
    /// (wordPatchUpdate) walks DOM *siblings* between the &lt;wb&gt; and
    /// &lt;we&gt; markers. That only works when both markers sit at the same DOM
    /// depth, which holds **iff** the captured payload is a well-balanced HTML
    /// fragment with no leading orphan-close. So we test exactly that, over
    /// EVERY element tag — no enumeration of containers (page-wrapper, ol/ul,
    /// multi-column / drop-cap div, table, …). Any present-or-future renderer
    /// shape that straddles a container is rejected, and the caller falls back
    /// to a full refresh. CONSISTENCY(word-patch-straddle).
    /// </summary>
    internal static bool WordPatchPayloadStraddlesStructure(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;

        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in _htmlTagRx.Matches(html))
        {
            var tag = m.Groups[2].Value;
            if (m.Groups[3].Value == "/") continue;            // explicit self-close <x/>
            if (_inlineOrVoidHtmlTags.Contains(tag)) continue; // inline / void — never straddles

            if (m.Groups[1].Value == "/")
            {
                // A close whose matching open was never seen in this payload —
                // it lives in a sibling block (e.g. </ol> after a list block,
                // </page-wrapper> from a mid-paragraph page break). The markers
                // are at different DOM depths → unsafe.
                var d = depth.GetValueOrDefault(tag) - 1;
                if (d < 0) return true;
                depth[tag] = d;
            }
            else
            {
                depth[tag] = depth.GetValueOrDefault(tag) + 1;
            }
        }
        // Any element left open at the end straddles into the next block.
        foreach (var d in depth.Values) if (d != 0) return true;
        return false;
    }

    /// <summary>
    /// Signature of chart overlay positions — concatenation of all data-from-row/col
    /// values in document order. Different signature → chart was moved → need full refresh.
    /// </summary>
    private static string ChartOverlaySignature(string html)
    {
        var sb = new System.Text.StringBuilder();
        var rx = new System.Text.RegularExpressions.Regex(@"data-from-(?:row|col)=""(\d+)""");
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(html))
            sb.Append(m.Value).Append(',');
        return sb.ToString();
    }

    /// <summary>
    /// Signature of Excel table chrome — concatenates each sheet's &lt;colgroup&gt;,
    /// &lt;thead&gt;, and the &lt;table&gt; open tag (which carries table width style).
    /// Row-level patches only swap &lt;tr&gt; nodes, so if this signature changes
    /// between old and new HTML (column added/removed, column width changed,
    /// thead style changed) the browser needs a full body refresh — otherwise
    /// new headers/widths stay stale until a manual reload.
    /// </summary>
    internal static string TableChromeSignature(string html)
    {
        var sb = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                html, @"<colgroup>.*?</colgroup>",
                System.Text.RegularExpressions.RegexOptions.Singleline))
            sb.Append(m.Value).Append('|');
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                html, @"<thead>.*?</thead>",
                System.Text.RegularExpressions.RegexOptions.Singleline))
            sb.Append(m.Value).Append('|');
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(html, @"<table[^>]*>"))
            sb.Append(m.Value).Append('|');
        return sb.ToString();
    }

    /// <summary>Split Excel HTML into rows keyed by "sheetIdx-rowNum" from data-row attributes.</summary>
    private static Dictionary<string, string> SplitExcelRows(string html)
    {
        var rows = new Dictionary<string, string>();

        // Static mode: extract <tr data-row="sheetIdx-rowNum"> elements
        var rx = new System.Text.RegularExpressions.Regex(@"<tr\s[^>]*data-row=""([^""]+)""[^>]*>");
        var matches = rx.Matches(html);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var key = m.Groups[1].Value;
            var contentStart = m.Index;
            var endTag = "</tr>";
            var endIdx = html.IndexOf(endTag, contentStart + m.Length, StringComparison.Ordinal);
            if (endIdx >= 0)
                rows[key] = html[contentStart..(endIdx + endTag.Length)];
        }

        // Virt mode: extract rows from <script type="application/json" id="virt-data-N">
        // Format: [{"r":R,"frozen":bool[,"h":H],"html":"<escaped inner html>"},...]
        var scriptRx = new System.Text.RegularExpressions.Regex(
            @"<script[^>]*id=""virt-data-(\d+)""[^>]*>([\s\S]*?)</script>");
        var rowRx = new System.Text.RegularExpressions.Regex(
            @"""r"":(\d+).*?""html"":""((?:[^""\\]|\\.)*)""");
        var heightRx = new System.Text.RegularExpressions.Regex(@"""h"":(\d+(?:\.\d+)?)");
        foreach (System.Text.RegularExpressions.Match scriptMatch in scriptRx.Matches(html))
        {
            var sheetIdx = scriptMatch.Groups[1].Value;
            var json = scriptMatch.Groups[2].Value;
            foreach (System.Text.RegularExpressions.Match rowMatch in rowRx.Matches(json))
            {
                var rowNum = rowMatch.Groups[1].Value;
                var key = $"{sheetIdx}-{rowNum}";
                if (rows.ContainsKey(key)) continue; // frozen row already captured from static <tr>
                var innerHtml = rowMatch.Groups[2].Value
                    .Replace("\\\"", "\"").Replace("\\\\", "\\")
                    .Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
                // Extract row height from metadata fields (the portion before "html":)
                var htmlFieldOffset = rowMatch.Value.IndexOf("\"html\":", StringComparison.Ordinal);
                var metaStr = htmlFieldOffset >= 0 ? rowMatch.Value.Substring(0, htmlFieldOffset) : "";
                var hm = heightRx.Match(metaStr);
                var heightStyle = hm.Success ? $" style=\"height:{hm.Groups[1].Value}pt\"" : "";
                rows[key] = $"<tr data-row=\"{key}\"{heightStyle}>{innerHtml}</tr>";
            }
        }

        return rows;
    }

    /// <summary>Compute row-level patches between old and new Excel HTML. Returns null if diff is too large (fallback to full).</summary>
    internal static List<(string Op, string Row, string? Html)>? ComputeExcelPatches(string oldHtml, string newHtml)
    {
        if (string.IsNullOrEmpty(oldHtml) || string.IsNullOrEmpty(newHtml))
            return null;
        // Two valid row-data signals:
        //  static: data-row="X..." where the value starts with an alphanumeric char (real keys
        //          are "N-M" or "word-N-M"; JS template literals have data-row="' + ... which
        //          starts with a single-quote, not alphanumeric).
        //  virt:   id="virt-data-N" on <script> data elements (numeric suffix, not "{n}" template
        //          used by the virt JS implementation script).
        static bool HasRowData(string h) =>
            System.Text.RegularExpressions.Regex.IsMatch(h, @"data-row=""[a-zA-Z0-9]") ||
            System.Text.RegularExpressions.Regex.IsMatch(h, @"id=""virt-data-\d+""");
        if (!HasRowData(oldHtml) || !HasRowData(newHtml))
            return null;

        // If chart overlay positions changed, fall back to full refresh.
        // excel-patch only patches <tr> rows; overlay divs are outside the table
        // and won't be updated by row-level patching.
        if (ChartOverlaySignature(oldHtml) != ChartOverlaySignature(newHtml))
            return null;

        var oldRows = SplitExcelRows(oldHtml);
        var newRows = SplitExcelRows(newHtml);

        if (oldRows.Count == 0 && newRows.Count == 0) return null;

        var patches = new List<(string Op, string Row, string? Html)>();

        // Check all keys from both old and new
        var allKeys = new HashSet<string>(oldRows.Keys);
        allKeys.UnionWith(newRows.Keys);

        foreach (var key in allKeys)
        {
            var inOld = oldRows.TryGetValue(key, out var oldContent);
            var inNew = newRows.TryGetValue(key, out var newContent);

            if (inOld && inNew)
            {
                if (oldContent != newContent)
                    patches.Add(("replace", key, newContent));
            }
            else if (!inOld && inNew)
            {
                patches.Add(("add", key, newContent));
            }
            else if (inOld && !inNew)
            {
                patches.Add(("remove", key, null));
            }
        }

        if (patches.Count == 0) return null;

        // If more than 60% of rows changed, fallback to full refresh
        var totalRows = Math.Max(oldRows.Count, newRows.Count);
        if (totalRows >= 5 && patches.Count > totalRows * 0.6)
            return null;

        return patches;
    }
}
