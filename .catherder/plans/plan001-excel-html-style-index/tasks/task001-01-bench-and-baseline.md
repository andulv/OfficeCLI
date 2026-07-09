---
type: task
description: "Task 001-01 — Add style-bloat render benchmark and record baseline (before fix)"
status: implemented
created: 2026-07-10T00:01:21+02:00
updated: 2026-07-10T00:34:00+02:00
---
## Required Context

Load and follow these skills:
- `plan-task-standards`

Read:
- [plan001-spec.md](../plan001-spec.md)
- [src/officecli/CommandBuilder.View.cs](../../../../src/officecli/CommandBuilder.View.cs) — `view <file> html` surface
- [CONTRIBUTING.md](../../../../CONTRIBUTING.md) — Rule 2 validation method

## Objective

Add a standalone, reproducible benchmark that deterministically creates a
style-bloated Excel file, times `officecli view <file> html`, and reports timing
+ output size. Record the **before** baseline on unmodified `main`. This script is
the Rule 2 validation method for the eventual PR.

## Scope

Included:
- `bench/excel-html-stylesheet/gen_bloat.py` — stdlib-only generator: writes a
  valid `.xlsx` via `zipfile` + string templates with a configurable number of
  `cellXfs` entries (default ~16000 to mirror the trigger file), a modest cell
  grid (e.g. 28 cols × 1000 rows), and zero formulas (isolate the style cost).
- `bench/excel-html-stylesheet/bench.sh` — builds officecli (or accepts a binary
  path), generates the synthetic file (or accepts real `.xlsx` paths), runs
  `officecli view <file> html` N times, and prints median/min wall-clock + HTML
  byte size, both human-readable and as `key=value` lines.
- A small `bench/excel-html-stylesheet/README.md` explaining before/after usage.
- Recorded baseline numbers (commit the numbers in the task execution record, not
  a generated file, to avoid bloating the repo).

Excluded:
- The actual performance fix (T02).
- Any change to officecli source.

## Steps

1. Decide the generator's minimal-valid-xlsx shape (workbook.xml, one sheet,
   styles.xml with N cellXfs, sheetData referencing cells across the grid).
2. Implement `gen_bloat.py` (stdlib only); verify the output opens and renders
   with the current `officecli view`.
3. Implement `bench.sh`: accept `--binary`, `--runs`, optional file path(s);
   generate the synthetic file if none given; time renders; print results.
4. Run the baseline on `main`: synthetic file and, if available locally, the real
   trigger file. Save the numbers in the execution record.

## Verification

- `python3 bench/excel-html-stylesheet/gen_bloat.py --cellxfs 16000 --out /tmp/bloat.xlsx` produces a file `officecli view /tmp/bloat.xlsx html` can render (exits 0, non-empty HTML).
- `bash bench/excel-html-stylesheet/bench.sh` prints timing + HTML bytes for the synthetic file and exits 0.
- Baseline numbers (synthetic + trigger file if available) recorded under Executor Verification.

---

Everything above this line is the task specification. Everything below is the
execution record.

# Execution

## Executor Notes
By: pi (executor) @ 2026-07-10T00:34:00+02:00

Added the bench and recorded the BEFORE baseline on `main` (officecli v1.0.134).

**Deliverables**
- `bench/excel-html-stylesheet/gen_bloat.py` — stdlib-only generator; writes a minimal valid `.xlsx` with a configurable `cellXfs` count (default 16000), a rows×cols grid, zero formulas. Cells cycle style indices across the full cellXfs range to exercise worst-case O(N) `ElementAt` walks. Validates: officecli renders its output.
- `bench/excel-html-stylesheet/bench.sh` — resolves officecli (explicit `--binary`, repo build output, or auto-build), generates the synthetic file or accepts real `.xlsx` paths, runs N renders, prints `html_bytes` / `min_seconds` / `median_seconds` / `runs` (human-readable + `key=value`).

**BEFORE baseline (main, v1.0.134)**

Synthetic (cellXfs=16000, 1000×28, 28000 cells):
- min=36.734s, median=54.767s, html_bytes=2,450,638

Real trigger file (`Mal til AI genererte spørsmål(1).xlsx`):
- min=9.315s, median=9.529s, html_bytes=12,472,760

Canonical HTMLs saved for byte-diff after the fix:
- `/tmp/synth-before.html` (2,450,638 bytes)
- `/tmp/trigger-before.html` (12,472,760 bytes)

## Executor Verification
By: pi (executor) @ 2026-07-10T00:34:00+02:00

- `python3 bench/excel-html-stylesheet/gen_bloat.py --cellxfs 16000 --rows 1000 --cols 28 --out /tmp/bloat.xlsx` — produced a file `officecli view … html` renders (exit 0).
- `bash bench/excel-html-stylesheet/bench.sh` — ran, printed timing + HTML bytes, exit 0 (synthetic + real-path modes).
- Baseline numbers recorded above.

## Reviewer Verification

## Review Notes
