---
type: task
description: "Task 001-03 — Verify before/after and confirm PR readiness (Rule 1 + Rule 2)"
status: implemented
created: 2026-07-10T00:01:21+02:00
updated: 2026-07-10T01:10:00+02:00
---
## Required Context

Load and follow these skills:
- `plan-task-standards`

Read:
- [plan001-spec.md](../plan001-spec.md)
- [CONTRIBUTING.md](../../../../CONTRIBUTING.md) — Rule 1 + Rule 2
- T01 baseline numbers and T02 change

## Objective

Prove the fix works (faster + byte-identical), confirm the change is a single
atomic unit with the validation method attached, and stage everything for an
upstream PR.

## Scope

Included:
- Re-run the bench on the fixed build to capture **after** numbers (synthetic +
  trigger file if available).
- Assert: HTML byte-identical to baseline; render time materially lower.
- Confirm the diff is one atomic change (Rule 1): code fix + validation script
  only; no bundled unrelated optimization; `.catherder/` and generated artifacts
  excluded (`.gitignore`).
- Draft the Rule 2 validation text (command sequence + before/after numbers) for
  the PR description.

Excluded:
- Opening the PR / CI / upstream review (out of scope for this plan).

## Steps

1. Build the fixed officecli; run `bench.sh`; capture **after** numbers.
2. Compare HTML bytes to the T01 baseline (must be identical).
3. `git diff main` review: confirm only (a) the indexing code change and (b) the
   committed `bench/` script are present; confirm `.catherder/` is gitignored and
   absent from the diff.
4. Write the PR-validation block (before vs. after table) into the execution
   record.

## Verification

- After-fix render of the synthetic file is materially faster than the T01
  baseline (record both numbers); trigger file drops to sub-second.
- `diff` of before/after HTML is empty (byte-identical).
- `git diff main --stat` shows only the indexing source change(s) and the
  `bench/excel-html-stylesheet/` script; no `.catherder/` files.
- `.gitignore` excludes `.catherder/` and generated bench artifacts.
- PR-validation text (Rule 2) is written and references the runnable script.

---

Everything above this line is the task specification. Everything below is the
execution record.

# Execution

## Executor Notes
By: pi (executor) @ 2026-07-10T01:10:00+02:00

**Before vs after** (officecli v1.0.134, same machine, same files):

| File | before (main) | after (fix) | speedup | output |
|---|---|---|---|---|
| Synthetic (cellXfs=16000, 1000×28) | 36.7s min / 54.8s med | 0.36s min / 0.43s med | ~100× | byte-identical |
| Real trigger file (`Mal til AI …(1).xlsx`) | 9.3s min / 9.5s med | 0.69s min / 0.77s med | ~13× | byte-identical |

**Correctness.** Rendered the SAME file on pristine `main` (single-file `git stash`
+ rebuild) and on the fix branch; `diff` is empty for both the synthetic file
(2,450,626 bytes) and the real trigger file (12,472,760 bytes).

**Atomic (Rule 1).** `git diff main --stat` shows exactly three files:
`bench/excel-html-stylesheet/bench.sh`, `bench/excel-html-stylesheet/gen_bloat.py`,
and `src/officecli/Handlers/Excel/ExcelHandler.HtmlPreview.cs` (+383/-54). No
bundled optimization; `.catherder/` excluded (gitignored).

**Local-only scaffolding NOT part of the PR** (revert before upstreaming): `.gitignore`,
`Directory.Build.props`, `Directory.Packages.props`, and the temporary
`.csproj` publish-flag changes — all untracked/working-tree, as agreed.

## Executor Verification
By: pi (executor) @ 2026-07-10T01:10:00+02:00

- After-fix render materially faster than baseline (numbers above); trigger file
  drops to sub-second (0.69s).
- `diff` before/after HTML empty for both files (byte-identical).
- `git diff main --stat` = bench/ + one .cs only; `.catherder/` absent (gitignored).

**Rule 2 validation text (for the PR description):**

```bash
# Before the fix (main):
bash bench/excel-html-stylesheet/bench.sh --cellxfs 16000 --rows 1000 --cols 28 --runs 3
# → min_seconds=36.734  median_seconds=54.767

# After the fix:
bash bench/excel-html-stylesheet/bench.sh --cellxfs 16000 --rows 1000 --cols 28 --runs 3
# → min_seconds=0.361  median_seconds=0.431   (~100× faster)

# Output is byte-identical (same file rendered on main vs fix → diff is empty):
diff <(git stash && dotnet build ... && officecli view f.xlsx html && git stash pop) ...
```

The script also accepts a real `.xlsx` path: `bash bench.sh path/to/file.xlsx`.

## Reviewer Verification

## Review Notes
