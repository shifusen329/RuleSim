#!/bin/bash
# fflogs_pull.sh <out dir> <tag> <job or player name> <zone "hex:name"> <report code>/<fight id> [...]
#
# Downloads FFLogs fights (WrathCombo utils/fetch.py) and rebuilds each as an ACT network log (utils/convert.py), one folder
# per fight: <out dir>/<tag>1, <tag>2, ... in argument order, each with the download (<code>_<fight>/) and
# Network_39999_<date>.log, the given job/name written as the log's own player. Zone: the game's zone the log says it is in,
# "553:Dancing Mad (Ultimate)" for Dancing Mad, "522:Mistwake" for Mistwake (hex ids from the 01 lines of a real ACT log).
# Prints each converted log's own player id (the --me for the analysis scripts). UTILS: WrathCombo's utils folder (default
# ../../WrathCombo/utils from this script). FFLogs credentials come from PartyVet's config through ff.py; never printed.
set -eu
OUT=$1; TAG=$2; WHO=$3; ZONE=$4; shift 4
HERE=$(cd "$(dirname "$0")" && pwd)
UTILS=${UTILS:-$HERE/../../WrathCombo/utils}
export PYTHONIOENCODING=utf-8
n=0
for cf in "$@"; do
  n=$((n + 1)); code=${cf%/*}; fight=${cf#*/}; dir="$OUT/$TAG$n"
  mkdir -p "$dir"
  echo "== $TAG$n  $code/$fight"
  # Not python -I: fetch.py imports ff.py from its own folder.
  [ -f "$dir/${code}_$fight/events.json" ] || python "$UTILS/fetch.py" "$code" "$fight" "$dir"
  python "$UTILS/convert.py" "$dir/${code}_$fight" "$dir" "$WHO" "$ZONE"
  log=$(ls -t "$dir"/Network_39999_*.log | head -1)
  echo "   $log: own player $(grep -a -m1 '^02|' "$log" | cut -d'|' -f3,4)"
done
