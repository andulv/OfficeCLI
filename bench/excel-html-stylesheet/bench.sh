#!/usr/bin/env bash
# bench/excel-html-stylesheet/bench.sh
#
# Time `officecli view <file> html` on a style-bloated Excel workbook.
# This is the Rule 2 validation method for the Excel-HTML-style-index fix.
#
# It can either generate a deterministic synthetic bloat workbook
# (bench/excel-html-stylesheet/gen_bloat.py) or accept real .xlsx paths
# (e.g. the trigger file). It runs N renders and prints min/median wall-clock
# plus rendered-HTML byte size, both human-readable and as `key=value` lines
# (machine-parseable for diffing before/after).
#
# Usage:
#   bash bench/excel-html-stylesheet/bench.sh                     # synthetic, default binary + args
#   bash bench/excel-html-stylesheet/bench.sh --binary /path/to/officecli --runs 5
#   bash bench/excel-html-stylesheet/bench.sh path/to/real.xlsx path/to/other.xlsx
#
# Typical before/after flow:
#   git checkout main           && bash bench.sh --runs 5 | tee before.txt
#   git checkout fix-branch     && bash bench.sh --runs 5 | tee after.txt
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="${BIN:-}"
RUNS=5
CELLXFS=16000
ROWS=1000
COLS=28
FILES=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --binary) BIN="$2"; shift 2;;
    --runs)   RUNS="$2"; shift 2;;
    --cellxfs) CELLXFS="$2"; shift 2;;
    --rows)   ROWS="$2"; shift 2;;
    --cols)   COLS="$2"; shift 2;;
    -h|--help)
      sed -n '2,20p' "$0"; exit 0;;
    *)
      FILES+=("$1"); shift;;
  esac
done

# Resolve the officecli binary: explicit > repo build output > build it.
if [[ -z "$BIN" ]]; then
  REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
  BIN="$REPO_ROOT/src/officecli/bin/Debug/net10.0/officecli"
  if [[ ! -x "$BIN" ]]; then
    echo "officecli binary not found at $BIN; building..." >&2
    ( cd "$REPO_ROOT" && dotnet build src/officecli/officecli.csproj -clp:ErrorsOnly ) >&2
  fi
fi
if [[ ! -x "$BIN" ]]; then
  echo "error: officecli binary not found/executable: $BIN" >&2
  exit 2
fi
VERSION="$("$BIN" --version 2>/dev/null | head -1 || echo unknown)"

# If no real files were passed, generate the synthetic bloat workbook.
if [[ ${#FILES[@]} -eq 0 ]]; then
  TMPXLSX="$(mktemp -t bloat-XXXXXX.xlsx)"
  trap 'rm -f "$TMPXLSX"' EXIT
  python3 "$SCRIPT_DIR/gen_bloat.py" --cellxfs "$CELLXFS" --rows "$ROWS" --cols "$COLS" --out "$TMPXLSX" >&2
  FILES=("$TMPXLSX")
  SOURCE="synthetic (cellXfs=$CELLXFS, ${ROWS}x${COLS})"
else
  SOURCE="provided"
fi

echo "# officecli excel html render bench"
echo "# binary=$BIN version=$VERSION"
echo "# runs=$RUNS source=$SOURCE"
echo "# timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo

# Render once to a fixed output to capture byte size; reuse it for diffing.
for f in "${FILES[@]}"; do
  base="$(basename "$f")"
  out_html="$(mktemp -t "${base}-html-XXXXXX")"
  times_file="$(mktemp -t "${base}-times-XXXXXX")"
  trap 'rm -f "$out_html" "$times_file"' EXIT

  # Warmup + capture a canonical HTML for byte-size + before/after diff.
  "$BIN" view "$f" html > "$out_html" 2>/dev/null || { echo "render failed for $f" >&2; exit 1; }
  html_bytes=$(wc -c < "$out_html")

  for _ in $(seq 1 "$RUNS"); do
    # Bash + /usr/bin/time gives wall-clock seconds robustly without python deps.
    start=$(date +%s.%N)
    "$BIN" view "$f" html > /dev/null 2>&1 || true
    end=$(date +%s.%N)
    awk -v s="$start" -v e="$end" 'BEGIN{printf "%.3f\n", e-s}' >> "$times_file"
  done

  # min + median
  mapfile -t sorted < <(sort -g "$times_file")
  n=${#sorted[@]}
  min=${sorted[0]}
  mid=$(( n / 2 ))
  median=${sorted[$mid]}

  echo "## $base"
  echo "html_bytes=$html_bytes"
  echo "min_seconds=$min"
  echo "median_seconds=$median"
  echo "runs=$n all=$(paste -sd, "$times_file")"
  echo
done

echo "# canonical html (first file) written for diff:"
echo "# diff before/after should be empty when output is byte-identical."
