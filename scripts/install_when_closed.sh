#!/bin/bash
# install_when_closed.sh <staged plugin dir> <rules source dir> <backup dir> <hours to wait> <rule file> [...]
#
# Installs a staged WrathCombo build (a Debug build made with -p:DalamudDevPlugins=<staged plugin dir>\) and rule files only
# while the game is closed: a WrathCombo reload under a running game crashed it on 2026-11-03 (a press landed while the
# plugin was half loaded). Waits until ffxiv_dx11.exe has been gone for two checks 10 s apart, then backs up the live rule
# folder and the plugin files it replaces, copies the rule files, then the changed plugin files (WrathCombo.dll last).
# Gives up after <hours to wait>. Windows (Git Bash): uses tasklist and %APPDATA%.
set -u
STAGE=$1; RULESRC=$2; BACKUP=$3; HOURS=$4; shift 4
LIVE_PLUGIN="$APPDATA/XIVLauncher/devPlugins/WrathCombo"
LIVE_RULES="$APPDATA/XIVLauncher/pluginConfigs/WrathCombo/rules"
running() { tasklist 2>/dev/null | grep -qi '^ffxiv_dx11.exe'; }
end=$((SECONDS + HOURS * 3600))
while [ $SECONDS -lt $end ]; do
  if ! running; then
    sleep 10
    if ! running; then
      mkdir -p "$BACKUP/rules" "$BACKUP/plugin"
      cp -p "$LIVE_RULES"/*.json "$BACKUP/rules/"
      changed=$(cd "$STAGE" && find . -type f | sed 's|^\./||' | while read -r f; do cmp -s "$STAGE/$f" "$LIVE_PLUGIN/$f" || echo "$f"; done)
      for f in $changed; do [ -f "$LIVE_PLUGIN/$f" ] && { mkdir -p "$BACKUP/plugin/$(dirname "$f")"; cp -p "$LIVE_PLUGIN/$f" "$BACKUP/plugin/$f"; }; done
      for r in "$@"; do cp "$RULESRC/$r" "$LIVE_RULES/$r"; done
      for f in $changed; do [ "$f" = "WrathCombo.dll" ] || { mkdir -p "$LIVE_PLUGIN/$(dirname "$f")"; cp "$STAGE/$f" "$LIVE_PLUGIN/$f"; }; done
      echo "$changed" | grep -qx "WrathCombo.dll" && cp "$STAGE/WrathCombo.dll" "$LIVE_PLUGIN/WrathCombo.dll"
      bad=0
      for r in "$@"; do cmp -s "$RULESRC/$r" "$LIVE_RULES/$r" || { echo "MISMATCH rule $r"; bad=1; }; done
      for f in $changed; do cmp -s "$STAGE/$f" "$LIVE_PLUGIN/$f" || { echo "MISMATCH plugin $f"; bad=1; }; done
      echo "INSTALLED $(date +%T) with the game closed: rules [$*], plugin [$(echo $changed | tr '\n' ' ')]; backup $BACKUP"
      exit $bad
    fi
  fi
  sleep 15
done
echo "TIMEOUT: the game was not closed within $HOURS h; nothing installed"
exit 2
