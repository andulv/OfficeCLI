// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

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
}
