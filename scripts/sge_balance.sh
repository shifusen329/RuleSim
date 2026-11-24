#!/bin/bash
# sge_balance.sh <out dir> <variant name> [<--set defines>]
#
# Replays every Dancing Mad pull where the user is on Sage (both character ids, each log they appear in) through the Sage rules
# with `run <log> sge --party` and the given --set defines, one output file per log/id in <out dir>/<variant name>/; all logs in
# parallel (2026-11-24, user: "Find me a balanced SGE that doesn't leave everyone exposed <25% hp while optimizing DPS").
# Compare the folders with sge_variants.py (potency, Eukrasian Prognosis II per 10 min, players lost, time and dips under 25%).
#   SGE_LOGS: "<log>:<id> ..." pairs (default: the user's Sage logs as of 2026-11-24, `list all sge --me=<id>`).
set -u
OUT=$1; NAME=$2; SET=${3:-}
HERE=$(cd "$(dirname "$0")" && pwd)
DLL=${RULESIM_DLL:-$HERE/../src/RuleSim/bin/Release/net10.0/RuleSim.dll}
LOGS_DIR=${RR_LOGS:-$APPDATA/Advanced Combat Tracker/FFXIVLogs}
PAIRS=${SGE_LOGS:-"Network_30301_20260920.log:1001A4A8 Network_30301_20261006.log:1001A4A8 Network_30303_20261015.log:1001A4A8
Network_30304_20261020.log:1001A4A8 Network_30304_20261023.log:1001A4A8 Network_30304_20261023.log:10041112 Network_30304_20261104.log:10041112
Network_30304_20261106.log:10041112 Network_30305_20261110.log:10041112 Network_30305_20261112.log:10041112 Network_30305_20261114.log:10041112
Network_30305_20261115.log:10041112 Network_30305_20261116.log:10041112 Network_30305_20261118.log:10041112 Network_30305_20261119.log:10041112
Network_30305_20261121.log:10041112"}
mkdir -p "$OUT/$NAME"
for p in $PAIRS; do
  log=${p%%:*}; id=${p##*:}
  ( dotnet "$DLL" run "$LOGS_DIR/$log" sge --me=$id --party ${SET:+"--set=$SET"} > "$OUT/$NAME/${log%.log}_$id.txt" 2>&1 ) &
done
wait
echo "$NAME: $(ls "$OUT/$NAME" | wc -l) files"
