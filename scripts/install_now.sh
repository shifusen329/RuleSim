#!/bin/bash
# install_now.sh <staged plugin dir> <rules source dir> <backup dir> [rule file ...]
#
# Installs a staged WrathCombo Debug build (made with -p:DalamudDevPlugins=<staged plugin dir>\) right away, game running
# or not: when the user says "install", install then (2026-11-04: no waiting for an idle moment). Never on its own: a
# plugin reload under a running game crashed it on 2026-11-03 14:07 (8 files copied mid-play); install_when_closed.sh is
# for installs the user hasn't asked for. Backs up the live WrathCombo.dll/.pdb and rule folder, copies only
# WrathCombo.pdb then WrathCombo.dll (one reload; the resource DLLs differ only by build stamps), waits for Dalamud's
# "Finished loading WrathCombo", and only then copies the rule files (the old plugin would compile a file that needs a
# new builtin first: one "Unknown name" error on 2026-11-03). Windows (Git Bash): uses tasklist and %APPDATA%.
set -u
STAGE=$1; RULESRC=$2; BACKUP=$3; shift 3
LIVE_PLUGIN="$APPDATA/XIVLauncher/devPlugins/WrathCombo"
LIVE_RULES="$APPDATA/XIVLauncher/pluginConfigs/WrathCombo/rules"
DLOG="$APPDATA/XIVLauncher/dalamud.log"
running() { tasklist 2>/dev/null | grep -qi '^ffxiv_dx11.exe'; }

mkdir -p "$BACKUP/plugin" "$BACKUP/rules"
cp -p "$LIVE_PLUGIN/WrathCombo.dll" "$LIVE_PLUGIN/WrathCombo.pdb" "$BACKUP/plugin/"
cp -p "$LIVE_RULES"/*.json "$BACKUP/rules/"
echo "backup: plugin dll/pdb + $(ls "$BACKUP/rules" | wc -l) rule files in $BACKUP"

if running; then
  mark=$(stat -c %s "$DLOG")
  cp "$STAGE/WrathCombo.pdb" "$LIVE_PLUGIN/WrathCombo.pdb"
  cp "$STAGE/WrathCombo.dll" "$LIVE_PLUGIN/WrathCombo.dll"
  echo "COPIED $(date +%T); waiting for the reload"
  loaded=0
  for _ in $(seq 1 60); do
    sleep 1
    tail -c +$((mark + 1)) "$DLOG" | grep -aq "Finished loading WrathCombo" && { loaded=1; break; }
  done
  tail -c +$((mark + 1)) "$DLOG" | grep -a "WrathCombo" | grep -a -E "LocalPlugin|ERR" | head -20
  [ $loaded = 1 ] || echo "WARNING: no 'Finished loading WrathCombo' within 60 s; rule files not copied"
  [ $loaded = 1 ] || exit 3
else
  cp "$STAGE/WrathCombo.pdb" "$LIVE_PLUGIN/WrathCombo.pdb"
  cp "$STAGE/WrathCombo.dll" "$LIVE_PLUGIN/WrathCombo.dll"
  echo "COPIED $(date +%T) with the game closed"
fi

for r in "$@"; do cp "$RULESRC/$r" "$LIVE_RULES/$r"; done
bad=0
for f in WrathCombo.dll WrathCombo.pdb; do cmp -s "$STAGE/$f" "$LIVE_PLUGIN/$f" || { echo "MISMATCH plugin $f"; bad=1; }; done
for r in "$@"; do cmp -s "$RULESRC/$r" "$LIVE_RULES/$r" || { echo "MISMATCH rule $r"; bad=1; }; done
[ $bad = 0 ] && echo "files match"
sleep 10
running && echo "game still running" || echo "game NOT running"
exit $bad
