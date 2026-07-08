// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace OfficeCli.Core;

/// <summary>
/// Host-agnostic watch state and logic, independent of any web/pipe transport:
/// it owns no sockets and never opens the document. It is being extracted from
/// <see cref="WatchServer"/> incrementally; this step holds the shared selection
/// state, with the cached HTML/version, marks, and message dispatch to follow.
/// </summary>
public sealed class WatchEngine
{
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

}
