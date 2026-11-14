#!/bin/bash
# install_rules_out_of_combat.sh <rules source dir> <backup dir> <max hours> <rule file> [...]
#
# Copies rule files (JSON only, no plugin) into the live Custom Rules folder once you are out of combat: the newest ACT
# network log's last 260 (combat state) line must show both ACT and the game out of combat, for 3 s in a row (a pull
# starts with ACT in combat 0.9 s before the game). Backs up the live rules folder first, then waits for Dalamud's
# "Loaded N rule set(s)" after the copy. For "install when I'm out of combat" (2026-11-14). Windows (Git Bash).
set -u
RULESRC=$1; BACKUP=$2; HOURS=$3; shift 3
LIVE_RULES="$APPDATA/XIVLauncher/pluginConfigs/WrathCombo/rules"
DLOG="$APPDATA/XIVLauncher/dalamud.log"
LOGDIR="$APPDATA/Advanced Combat Tracker/FFXIVLogs"

state() {   # prints "in" or "out"
  local log line
  log=$(ls -t "$LOGDIR"/Network_*.log 2>/dev/null | head -1)
  [ -n "$log" ] || { echo out; return; }
  line=$(tail -c 3000000 "$log" | LC_ALL=C grep -a '^260|' | tail -1)
  [ -z "$line" ] && { echo out; return; }
  IFS='|' read -r _ _ act game _ <<< "$line"
  [ "$act" = 0 ] && [ "$game" = 0 ] && echo out || echo in
}

deadline=$(( $(date +%s) + HOURS * 3600 ))
quiet=0
echo "waiting for out of combat ($(date +%T))"
while :; do
  if [ "$(state)" = out ]; then quiet=$((quiet + 1)); else quiet=0; fi
  [ $quiet -ge 3 ] && break
  [ "$(date +%s)" -ge $deadline ] && { echo "gave up after $HOURS h"; exit 2; }
  sleep 1
done
echo "out of combat at $(date +%T)"

mkdir -p "$BACKUP"
cp -p "$LIVE_RULES"/*.json "$BACKUP/"
echo "backup: $(ls "$BACKUP" | wc -l) rule files in $BACKUP"
mark=$(stat -c %s "$DLOG")
for f in "$@"; do cp "$RULESRC/$f" "$LIVE_RULES/$f"; echo "copied $f"; done
for _ in $(seq 1 20); do
  sleep 1
  tail -c +$((mark + 1)) "$DLOG" | grep -aq "Loaded [0-9]* rule set" && break
done
tail -c +$((mark + 1)) "$DLOG" | grep -a -E "Custom Rules\].*(Loaded|rror|nknown)" | tail -5
for f in "$@"; do cmp -s "$RULESRC/$f" "$LIVE_RULES/$f" && echo "$f matches" || echo "$f DIFFERS"; done
