# scripts

Shareable analysis and run scripts (the user, 2026-11-03: every shareable script lives here, not in a session scratchpad).

- `ast_cards.py <ACT log> [--from ISO] [--to ISO] [--me ID] [--zones dungeon|all] [--dalamud dalamud.log ...] [--detail]`:
  an Astrologian's draws, cards, Lord/Lady and Divination per zone visit, each marked rules / manual from AnoMech's
  "Action pressed" lines in dalamud.log, with Divination's cooldown, how long the Play I card was held and how long the
  draw waited. Pure stdlib Python.
- `ast_top5.sh <logs dir> <rules dir> <out dir> <tag> [RuleSim args]`: replays the five top FFLogs Astrologians
  (`<logs dir>/ast1..5`, converted by WrathCombo's utils/convert.py) against a rules folder, five in parallel.
- `ast_manual.py <ACT log> --dalamud dalamud.log [...] [--from ISO] [--to ISO] [--zones dungeon|all] [--detail ACTION]`:
  every action an Astrologian used, by the rules or pressed by hand, and for the manual ones the situation (enemies, the
  tank's / party's / lowest HP, target HP, your Combust's time left). What the user does by hand is what the rules miss;
  it set DUN_AST.json's thresholds (2026-11-03).
- `install_when_closed.sh <staged plugin dir> <rules source dir> <backup dir> <hours> <rule file> [...]`: installs a staged
  WrathCombo Debug build and rule files only once the game (ffxiv_dx11.exe) is closed, with backups; a plugin reload
  under a running game crashed it (2026-11-03).
