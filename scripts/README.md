# scripts

Shareable analysis and run scripts (the user, 2026-11-03: every shareable script lives here, not in a session scratchpad).

- `ast_cards.py <ACT log> [--from ISO] [--to ISO] [--me ID] [--zones dungeon|all] [--dalamud dalamud.log ...] [--detail]`:
  an Astrologian's draws, cards, Lord/Lady and Divination per zone visit, each marked rules / manual from AnoMech's
  "Action pressed" lines in dalamud.log, with Divination's cooldown, how long the Play I card was held and how long the
  draw waited. Pure stdlib Python.
- `ast_top5.sh <logs dir> <rules dir> <out dir> <tag> [RuleSim args]`: replays the five top FFLogs Astrologians
  (`<logs dir>/ast1..5`, converted by WrathCombo's utils/convert.py) against a rules folder, five in parallel.
