#!/bin/bash
# ast_top5.sh <logs dir> <rules dir> <out dir> <tag> [extra RuleSim args]
# Replays the five top FFLogs Astrologians in Dancing Mad (converted with WrathCombo's utils/convert.py into
# <logs dir>/ast1..ast5, one fight per folder) against the rule files in <rules dir>, all five in parallel.
# Writes <out dir>/out_<tag>_<n>.txt and prints each fight's SUMMARY line.
# Their log ids (0x100F0000 + FFLogs actor id) as of the 2026-11-01/11-03 rankings: Yamii Chan, anonymous,
# Koyuki'' Veritas, Kurisu Tan, anonymous (docs/results/ast_results.md).
set -e
LOGS=$1; RULES=$2; OUT=$3; TAG=$4; shift 4
IDS=(x 100F02F3 100F0002 100F0023 100F0372 100F0006)
HERE=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUT"
cd "$HERE/src/RuleSim/bin/Release/net10.0"
export SIM_AUTOS=1
w() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
for i in 1 2 3 4 5; do
  RR_LOGS="$(w "$LOGS/ast$i")" dotnet RuleSim.dll run all ast --me=${IDS[$i]} --from=20200101 --rules="$(w "$RULES")" \
    --step=0.5 --slide=900 --slotlist --tanks --cfhp "$@" > "$OUT/out_${TAG}_$i.txt" 2>&1 &
done
wait
grep -h "^SUMMARY" "$OUT"/out_${TAG}_*.txt
