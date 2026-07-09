#!/usr/bin/env python3
"""Generate a style-bloated .xlsx to reproduce slow officecli HTML rendering.

The Excel HTML preview resolves a CSS style per cell by enumerating the
stylesheet collections. A workbook whose cellXfs table is large therefore makes
the render O(cells x styles), even with tiny data. That is exactly the signature
of files produced by style-bloating generators (one style entry appended per cell
write instead of deduped).

This generator writes a *minimal valid* .xlsx using only the Python standard
library (zipfile + string templates), so any reviewer can reproduce it without
installing anything. It produces a configurable number of cellXfs entries, a
modest cell grid, and zero formulas (to isolate the style cost from formula
evaluation).

Usage:
    python3 gen_bloat.py --cellxfs 16000 --rows 1000 --cols 28 --out /tmp/bloat.xlsx
    python3 gen_bloat.py                       # defaults below
"""
from __future__ import annotations

import argparse
import datetime as _dt
import zipfile
from xml.sax.saxutils import escape


def _content_types() -> str:
    return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
</Types>"""


def _rels() -> str:
    return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>"""


def _workbook() -> str:
    return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
<sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
</workbook>"""


def _workbook_rels() -> str:
    return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
</Relationships>"""


def _shared_strings(count: int) -> str:
    # A small shared-strings table; cells reference indices into it.
    n = min(count, 16)
    items = "\n".join(
        f"<si><t>str{i}</t></si>" for i in range(n)
    )
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        f'<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{n}" uniqueCount="{n}">\n'
        f"{items}\n</sst>"
    )


def _col_letter(n: int) -> str:
    # 1-based -> A, B, ..., Z, AA, ...
    s = ""
    while n > 0:
        n, r = divmod(n - 1, 26)
        s = chr(65 + r) + s
    return s


def _styles(cellxfs: int) -> str:
    """styles.xml with the requested number of cellXfs entries.

    cellXfs is the cell-format table that the HTML preview enumerates per cell.
    We give it `cellxfs` entries (default 16000) to reproduce a bloated table,
    plus one default numFmt (id 164 = "General") to match the trigger file's
    shape. Fonts/fills/borders are kept small so the measured cost is the
    cellXfs walk, not auxiliary tables.
    """
    num_cellxfs = max(1, cellxfs)
    xfs = "\n".join(
        '<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="0" applyFont="0" applyFill="0" applyBorder="0" applyAlignment="0" applyProtection="0"/>'
        for _ in range(num_cellxfs)
    )
    return f"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
<numFmts count="1"><numFmt numFmtId="164" formatCode="General"/></numFmts>
<fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
<fills count="1"><fill><patternFill><bgColor indexed="64"/></patternFill></fill></fills>
<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
<cellXfs count="{num_cellxfs}">
{xfs}
</cellXfs>
<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>"""


def _sheet(rows: int, cols: int, cellxfs: int) -> str:
    """One sheet with a rows x cols grid; each cell references a cellXfs entry.

    Cells cycle through style indices 0..cellxfs-1 so the preview must resolve
    many distinct (and far-apart) entries — exercising the worst case for an
    O(N) ElementAt walk.
    """
    body = []
    for r in range(1, rows + 1):
        cells = []
        for c in range(1, cols + 1):
            ref = f"{_col_letter(c)}{r}"
            si = (r * cols + c) % max(1, cellxfs)
            si = min(si, max(0, cellxfs - 1))
            ssi = (r + c) % 16  # shared-string index
            cells.append(
                f'<c r="{ref}" s="{si}" t="s"><v>{ssi}</v></c>'
            )
        body.append(f'<row r="{r}">' + "".join(cells) + "</row>")
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        f'<dimension ref="A1:{_col_letter(cols)}{rows}"/>'
        "<sheetData>"
        + "\n".join(body)
        + "</sheetData></worksheet>"
    )


def build(out: str, cellxfs: int, rows: int, cols: int) -> None:
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", _content_types())
        z.writestr("_rels/.rels", _rels())
        z.writestr("xl/workbook.xml", _workbook())
        z.writestr("xl/_rels/workbook.xml.rels", _workbook_rels())
        z.writestr("xl/sharedStrings.xml", _shared_strings(rows * cols))
        z.writestr("xl/styles.xml", _styles(cellxfs))
        z.writestr("xl/worksheets/sheet1.xml", _sheet(rows, cols, cellxfs))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cellxfs", type=int, default=16000, help="number of cellXfs entries (default 16000, mirrors the trigger file)")
    p.add_argument("--rows", type=int, default=1000, help="rows of data (default 1000)")
    p.add_argument("--cols", type=int, default=28, help="columns of data (default 28, mirrors the trigger file's 28-col sheets)")
    p.add_argument("--out", required=True, help="output .xlsx path")
    args = p.parse_args()

    build(args.out, args.cellxfs, args.rows, args.cols)

    import os
    print(
        f"wrote {args.out} ({os.path.getsize(args.out)} bytes): "
        f"cellXfs={args.cellxfs}, grid={args.rows}x{args.cols} cells={args.rows*args.cols}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
