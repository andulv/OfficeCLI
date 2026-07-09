---
type: plan-implementation
description: "Plan 001 - Excel HTML preview style-table indexing: validation script + O(1) lookup fix"
status: active
created: 2026-07-10T00:01:21+02:00
updated: 2026-07-10T01:10:00+02:00
---
# Plan 001 Implementation — Excel HTML preview style-table indexing

## 0. Required Context

- Spec: [plan001-spec.md](plan001-spec.md)
- `plan-task-standards` (CatHerder method)
- [CONTRIBUTING.md](../../../CONTRIBUTING.md) — Rule 1 (atomic), Rule 2 (validation)
- [src/officecli/Handlers/Excel/ExcelHandler.HtmlPreview.cs](../../../src/officecli/Handlers/Excel/ExcelHandler.HtmlPreview.cs)
  — hot path; repeated `Elements<CellFormat>().Count()`/`.ElementAt(i)` per cell
- [src/officecli/Handlers/Excel/ExcelHandler.cs](../../../src/officecli/Handlers/Excel/ExcelHandler.cs)
  — `Stylesheet` / `ExcelStyleManager`, render entry
- Build: `dotnet build src/officecli/officecli.csproj` (or `bash build.sh` for a
  published single-file binary). Render surface: `officecli view <file> html`.

> Note: this plan's artifacts (`.catherder/`) are local working files and are
> excluded from the eventual upstream PR (see spec Q5 / T03). The PR ships only
> the code fix + the committed validation script.

## 1. Tasks

Allowed task statuses: not-started, in-progress, blocked, implemented, reviewed, completed.

| Status | Task |
|---|---|
| `implemented` | [Task P001-T01: Add style-bloat render benchmark + record baseline (before fix)](tasks/task001-01-bench-and-baseline.md) |
| `implemented` | [Task P001-T02: Index stylesheet for O(1) per-cell lookups in Excel HTML preview](tasks/task001-02-index-stylesheet-lookups.md) |
| `implemented` | [Task P001-T03: Verify before/after + PR readiness (Rule 1 + Rule 2)](tasks/task001-03-verify-and-pr-readiness.md) |

## 2. Task Parallelism

Strictly sequential. T01 produces the repro + the **before** measurement on
unmodified `main` (the baseline the fix is judged against). T02 is the fix, built
on top of T01's repro. T03 re-runs the repro to capture **after**, asserts
byte-identical output + speedup, and confirms the diff is one atomic change with
the validation method present. Nothing runs in parallel.

## 3. Acceptance Criteria

- [ ] A deterministic, stdlib-only Python generator can produce a style-bloated
      `.xlsx` (configurable `cellXfs` count; default ~16000) and the bench script
      times `officecli view <file> html` over multiple runs.
- [ ] The bench script also accepts real `.xlsx` paths and prints human-readable
      + machine-parseable timing + HTML byte size.
- [ ] The stylesheet-indexing fix replaces the per-cell `Elements<X>().Count() /
      .ElementAt(i)` enumeration with O(1) indexed lookups, preserving the
      existing out-of-range "default/skip" behavior exactly.
- [ ] Rendered HTML is byte-for-byte identical before and after the fix (the
      bench asserts this).
- [ ] Render time on the synthetic repro and on the trigger file is materially
      lower after the fix (order-of-magnitude class on the repro; the trigger
      file drops from "long" to sub-second).
- [ ] `dotnet build src/officecli/officecli.csproj` succeeds with no new warnings.
- [ ] The combined change is a single atomic unit (Rule 1): code fix + validation
      script only; no bundled unrelated optimizations; `.catherder/` excluded.
- [ ] The PR description carries the Rule 2 validation method (the command
      sequence / script with before vs. after numbers).
