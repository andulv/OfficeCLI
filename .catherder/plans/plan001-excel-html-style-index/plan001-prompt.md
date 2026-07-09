---
type: plan-prompt
description: "Plan 001 - original request: speed up Excel HTML preview on files with bloated style tables"
status: active
created: 2026-07-10T00:01:21+02:00
updated: 2026-07-10T00:01:21+02:00
---
# Plan 001 Prompt — Fast Excel HTML preview on bloated style tables

## Original prompt

> We are now back on main in officecli. Make a plan for it here in main.
> If this fix is successful we plan to make pr upstream. See contribution guide
> here: https://github.com/iOfficeAI/OfficeCLI/blob/main/CONTRIBUTING.md
> Note! We are now focused on the fix and not on upstreaming itself. But we like
> our fix to follow their guidelines. This is the important bit now: Rule 2:
> Every PR must include a verifiable validation method. We need script that times
> rendering of complex file(s) before and after fix.

Context carried over from the investigation in the parent project (catherder-dev,
plan117): the file
`/filespaces/anders-omnishop-no/Mal til AI genererte spørsmål(1).xlsx`
renders slowly in the officecli HTML preview. The cause was traced into officecli
itself, so the fix belongs here, on `main`, as a self-contained change that can
later be upstreamed to iOfficeAI/OfficeCLI.

## Interpreted prompt

The Excel HTML preview path (`ExcelHandler.HtmlPreview.cs`) resolves a CSS style
for every cell by repeatedly enumerating the OpenXml stylesheet collections with
`stylesheet.CellFormats.Elements<CellFormat>().Count()` /
`.ElementAt(i)` — each call is an O(N) live walk. For a workbook with a
pathologically large `cellXfs` table (the trigger file has ~16,174 cell formats,
~8k fonts/fills/borders in a 1.88 MB `styles.xml`, produced by a style-bloating
generator), this becomes O(cells × styles) and dominates render time, even though
the cell data itself is small (two 28×1001 sheets, zero formulas).

The plan: index the stylesheet collections once per render and do O(1) lookups in
the hot path — a single, atomic, output-preserving performance fix. Per the
upstream contribution guide, the PR must be **one atomic change (Rule 1)** and
must ship a **verifiable validation method (Rule 2)**: a standalone script that
reproduces the slow render deterministically (a synthetic style-bloated .xlsx
generator) and times `officecli view <file> html` before and after the fix,
asserting the rendered HTML is byte-identical (correctness) and materially faster
(performance). The script must also accept real .xlsx paths so the trigger file
can be checked directly.
