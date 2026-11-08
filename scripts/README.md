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
- `install_now.sh <staged plugin dir> <rules source dir> <backup dir> [rule file ...]`: when the user says "install",
  installs a staged build right away, game running or not: backs up, copies WrathCombo.pdb then WrathCombo.dll, waits for
  Dalamud's "Finished loading WrathCombo", then copies the rule files.
- `fflogs_pull.sh <out dir> <tag> <job or name> <zone "hex:name"> <code>/<fight> [...]`: downloads FFLogs fights and
  rebuilds each as an ACT log (WrathCombo utils/fetch.py + convert.py) in `<out dir>/<tag>1..N`, written as the given
  player and zone ("522:Mistwake" for a dungeon run, "553:Dancing Mad (Ultimate)" for DMU). FFLogs ranks dungeons by DPS,
  HPS or speed (no rDPS): `characterRankings(specName, metric: dps)` on the dungeon's encounter (Dawntrail expert
  dungeons: zone 57, Mistwake 4549, The Clyteum 4551).
- `act_runs.py <ACT log> [...] [--from ISO] [--job AST] [--dungeon] [--last N] [--out DIR] [--dalamud dalamud.log ...]`:
  your zone visits in ACT logs (job, level, combat time, completed), dungeons told apart by the duty's director id
  (InstanceContent under 10000; trials are 20000+); --out cuts each listed run into its own log with its "Action pressed"
  lines, ready for ast_manual.py / ast_cards.py.
- `ast_patterns.py <ACT log> [...] [--csv out.csv]`: how Astrologians play a run, in the rule files' terms: per action the
  rate, boss/trash, seconds into the pull, enemies alive and near the target, pack HP, the target's HP, Divination up,
  moving, and the next/last raidwide; the Malefic/Gravity split by enemies; Combust on trash. Reconciled the AST files with
  the top five Mistwake Astrologians (2026-11-04).
- `rules_dump.py <rule file> [...]`: a rule file's defines and rules in short form (name, action, target, when).
- `dmu_ue_hits.py <ACT log or folder> [...] [--since 20260915] [--mt-job WAR] [--invuln Holmgang] [--solo] [--csv out.csv]`: every
  Ultimate Embrace in Dancing Mad: who it was cast on, who it hit and for how much, invulnerabilities up at the hit, deaths, whether
  the other tank was alive, your job. `--solo` keeps hits that landed on one player.
- `dmu_buster_mits.py <ACT log or folder> [...] --cast "Ultimate Embrace" --n 2 [--you-job WHM]`: what was on each tank when the Nth cast of a
  Dancing Mad tankbuster landed: main tank and other tank, how long before or after the cast each was applied and by which job.
- `dmu_rr3_sge.py <ACT log or folder> [...] [--since 20261023] [--detail N]`: the first Revolting Ruin III as a Sage plays it: when its two hits land, how much
  they do, the tank's HP (from the real HP updates), when Taurochole went out and its overheal, and an estimate of landing it between the hits.
- `check_rules_docs.py <WrathCombo repo root>`: checks docs/CustomRules.md against the code and bundled rule files: options, files, rule values and
  functions, target kinds. Numbers in the prose still need reading by hand.
