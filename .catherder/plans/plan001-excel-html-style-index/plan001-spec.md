---
type: plan-spec
description: "Plan 001 - speed up Excel HTML preview by indexing the stylesheet instead of per-cell O(N) enumeration"
status: ready
created: 2026-07-10T00:01:21+02:00
updated: 2026-07-10T00:01:21+02:00
---
# Plan 001 Spec — Fast Excel HTML preview on bloated style tables

## 0. Required Context

- `plan-task-standards` (CatHerder method) — plan lives in this repo's
  `.catherder/plans/`; method is self-contained.
- [CONTRIBUTING.md](../../../CONTRIBUTING.md) — upstream rules. This plan targets
  a fix that can be submitted to iOfficeAI/OfficeCLI: **Rule 1** (one atomic PR)
  and **Rule 2** (verifiable validation method).
- [src/officecli/Handlers/Excel/ExcelHandler.HtmlPreview.cs](../../../src/officecli/Handlers/Excel/ExcelHandler.HtmlPreview.cs)
  — the hot path. Per-cell style resolution repeatedly enumerates the OpenXml
  stylesheet.
- [src/officecli/Handlers/Excel/ExcelHandler.cs](../../../src/officecli/Handlers/Excel/ExcelHandler.cs)
  — `Stylesheet` ownership, `ExcelStyleManager`, render entry.
- Parent-project investigation (catherder-dev plan117): the trigger file's
  `styles.xml` is ~1.88 MB with ~16k `cellXfs`, ~8k each of fonts/fills/borders,
  but only ~28×1001 cells per sheet and zero formulas. The bloat is the cost
  driver, not the data.

## 1. Goal

Make `officecli view <file> html` (and therefore the watch initial render) fast on
Excel workbooks whose style table is large or bloated, by removing repeated O(N)
stylesheet enumeration from the per-cell hot path.

## 2. Context / Why

A real, AI-generated workbook (`Mal til AI genererte spørsmål(1).xlsx`) takes a
long time to load in the HTML preview. The data is small; the cost is the style
table. The renderer walks `stylesheet.CellFormats.Elements<CellFormat>()` (a live
LINQ enumeration) per cell — calling `.Count()` and `.ElementAt(i)` — each an
O(N) traversal. With ~16k cell formats and ~56k cells, the per-cell style lookup
becomes O(cells × styles) and dominates wall-clock. This is a latent footgun for
any workbook produced by a style-bloating generator (a common AI/codegen
signature), so fixing it improves robustness generally, not just for one file.

The same render path is used by `officecli watch` for its initial HTML, so the
fix also speeds up live-preview startup in CatHerder (plan117).

## 3. What We Want To Achieve (Outcomes)

- `officecli view <file> html` on a style-bloated workbook completes in a small
  fraction of the pre-fix time (target: well under 1s for the synthetic repro;
  order-of-magnitude improvement on the trigger file).
- Rendered HTML is **byte-for-byte identical** before and after the change
  (pure performance; no behavior/output change).
- A standalone, dependency-light validation script is added that deterministically
  reproduces the slow render and reports before/after timing, suitable as the
  Rule 2 validation method in an upstream PR.
- The change is one atomic, reviewable unit (Rule 1).

## 4. Key Principles / Constraints

- **Output-preserving.** The rendered HTML must not change. The validation script
  asserts byte-equality before/after. Style resolution semantics are unchanged —
  only the lookup cost.
- **One atomic change (Rule 1).** This PR is only the stylesheet-indexing
  performance fix plus its validation script. It does **not** also (a) dedupe the
  saved `styles.xml` on write, (b) memoize per-cell CSS strings, or (c) add row
  virtualization — each of those is a separate, independently valuable PR.
- **No new runtime dependencies.** officecli stays single-binary, zero-dep. The
  validation script may use Python 3 stdlib only (no openpyxl/etc.), so it is
  reproducible by any reviewer without setup.
- **Excel-only scope.** `cellXfs` / the bloated-stylesheet model is Excel-specific;
  Word and PowerPoint are untouched.
- **Follow upstream conventions.** No test project exists in this repo; the
  validation method is a script (CONTRIBUTING.md Rule 2, preference 2), runnable
  as a command sequence (preference 1) for before/after.

## 5. Out of Scope

- Deduping/compacting `styles.xml` on write (separate PR; would shrink files like
  the trigger file on disk but is a different layer).
- Per-cell CSS memoization / caching rendered fragments (separate PR).
- Row/column virtualization for the open-source static renderer (separate PR).
- Formula-evaluation caching (the trigger file has zero formulas; not the cost
  here).
- Any change to Word or PowerPoint rendering.
- The upstreaming process itself (open the PR, CI, review) — this plan is the fix
  only; "we plan to upstream" informs the shape, not the work.

## 6. Implementation Notes

Direction only — tasks are defined in the implementation.

- The cost is the repeated enumeration: `stylesheet.CellFormats.Elements<CellFormat>().Count()`
  and `.ElementAt(i)` (and the equivalent for Fonts/Fills/Borders/NumFmts) are
  called per cell in several methods (e.g. `GetCellStyleCss`, the spill/width and
  rotated-height helpers, `GetCellNumberFormatColor`, merge/freeze scans). Each
  call re-walks the child-element list from the start.
- Likely shape: build a lightweight per-render index (materialize
  `CellFormats`/`Fonts`/`Fills`/`Borders`/`NumFmts` into arrays once — or use the
  O(1) `ChildElements[i]` indexer / a safe bounds check — and pass it through the
  render context that already threads `ctx.Stylesheet`). Replace the `.Count() +
  .ElementAt()` pairs with an indexed lookup + bounds guard that preserves the
  existing "return default / skip on out-of-range index" behavior exactly.
- Keep the change mechanical and local: same data, same semantics, just O(1)
  access. Guard against regressions in the "styleIndex >= count → default" paths
  so bounds handling is identical.
- Add the validation script under a new top-level `bench/` directory (see resolved
  Q1). It generates a deterministic style-bloated .xlsx via stdlib `zipfile` +
  string templates (configurable cellXfs count, default ~16000; a modest cell grid
  mirroring the trigger file; zero formulas to isolate the style cost), times
  `officecli view <file> html`, and also accepts real .xlsx paths.

## 7. Open Questions

1. Where does the validation script live in the repo? **(resolved:** a new
   top-level `bench/` directory, e.g. `bench/excel-html-stylesheet/`. This repo
   has no `scripts/`/`tests/` convention; `bench/` is clear and self-describing
   for a perf repro. The script is the Rule 2 deliverable and is included in the
   PR; the `.catherder/` plan artifacts are local-only and excluded from the
   PR — see Q5.**)**
2. Could the fix alter rendered output? **(resolved:** no — it is a pure access-cost
   change; the script asserts byte-identical HTML before/after as the correctness
   guard.**)**
3. Should CSS-string memoization be bundled in? **(resolved:** no — separate PR;
   bundling would violate Rule 1. This plan indexes lookups only.**)**
4. Synthetic repro vs. the real trigger file for the PR validation? **(resolved:**
   ship a deterministic synthetic generator (no private data, reproducible by any
   reviewer) as the PR's validation method; the script additionally accepts real
   `.xlsx` paths so the trigger file can be checked locally.**)**
5. How are local plan artifacts kept out of the upstream PR? **(resolved:** officecli
   `main` has an untracked/empty `.gitignore`; add `.catherder/` and generated
   bench artifacts to `.gitignore` so the PR contains only the code fix + the
   committed validation script.**)**
6. Does this affect Word/PowerPoint? **(resolved:** no — `cellXfs` is Excel-only.**)**
