# Changelog

Machine-readable output surfaces (`--json` structure, `view text` line format,
`query --compact` lines, path/coordinate forms) are treated as API: entries
below call out every change to them. Additive changes extend; breaking
changes are avoided, and announced here when unavoidable.

## v1.0.134

### Added
- `query --compact` (pptx/docx): one TSV line per element in document order —
  `path<TAB>[label]<TAB>"text(≤60, … mark)"`; empty text shows `(empty)`;
  tables fold to `[table RxC]`; `--fields k1,k2` appends opt-in `k=v` columns.
  Final line is always `total: N of M elements / K slides` (pptx) or
  `total: N of M elements` (docx; never gains a container segment) —
  `lineCount - 1 == N` proves the reader saw every match. Full-document
  listing: selector `'*'` (pptx) or `'paragraph, table'` (docx). This line
  format is a **stability contract**: columns and label values may be added,
  never changed or reordered. xlsx rejects `--compact` (use `view text`).
- `view text --range` (xlsx): `'Sheet1!A1:C10'`, `'/Sheet1/A1:C10'`, or a
  single cell emits only the rows/cells inside the rectangle, same
  `<A1>=<value>` line format; `--json` subsets identically.
- `officecli --output-schema-crc`: CRC32 fingerprint of the embedded help
  schemas. Unchanged value across an upgrade → the documented property
  surface is byte-identical. (Code-level serialization behavior is outside
  this fingerprint.)

### Fixed
- pptx `view issues` text-fit now measures against the preset's inscribed
  text rectangle (diamond = the centered 50%×50% rect, ellipse ≈ 71%, …),
  so text spilling past a diamond's slanted edges is reported; the message
  names the geometry and `suggest.height` is in bounding-box terms.

### Contract notes
- The xlsx `view text` row line format (`[/Sheet1/row[N]] A1=v<TAB>B1=v`) is
  now an explicit stability contract (it carries the locate workflow).
