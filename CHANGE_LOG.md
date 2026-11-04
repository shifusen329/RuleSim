# Changelog

Notable changes to RuleSim. It has no versions; entries are by date.

## 2026-11-03

### Added

- **`RuleSim downtime <log>... [--zone=1363] [--min=30] [--detail]`:** when the party had nothing to hit, for each pull, worked out three ways:
  - **meter:** Raid Replay's live DPS meter (`TargetWatch`), run through the same indexer the plugin uses;
  - **world:** this sim's "no target" rule (`World.Target`: no boss present, targetable and not hidden), from the loaded pull;
  - **gaps:** stretches of 3 s or more with no party damage on an enemy, which both should match.

  It lists every span for up to 3 pulls (or with `--detail`), then totals by phase, the gaps the meter didn't call downtime, and any meter downtime while the party was dealing damage.
  - On the 10-05 clear (18:27) the gaps add up to 93.3 s, the meter to 91.4 s and the world rule to 48.2 s.
  - Over the 92 Dancing Mad pulls of 09-27, 10-05 and 10-22, the meter called no downtime while the party was dealing damage, and covered 612 of the 688 s of gaps.
- **RuleSim is a submodule of raid-replay** at `RuleSim/`. `Directory.Build.props` already finds Core in the parent repository there.

### Known issues

- **`World.Target` misses Dancing Mad downtime.**
  - It misses the P2→P3 cutscene (46 s in the clear) and a moment in P3 at 12:03 with nothing targetable (5 s).
  - It starts P4's Kefka Says downtime 6.8 s early.

  The giant Kefka that spawns for the cutscene has no 34 line, so it counts as targetable, and the sims target it through the cutscene. P4's Kefka (base 18475), whom the party is hitting, is a helper in the pack, not a boss. Raid Replay's `TargetWatch` avoids both: an enemy counts only once the log shows it is targetable, through its name toggling on or a party hit. Not fixed here yet.

## 2026-11-02

### Added

- **`RuleSim.Actions`: action resolution shared by AnoMech, WrathCombo and raid-replay.** A dependency-free library that answers an action the way the game server does: the statuses it spends and grants, the job gauge, the combo, who it hits and the animation lock. AnoMech now answers the player's and the bots' presses with it.
  - Every job's statuses, gauges, combos, procs and Swiftcast/Dualcast/Triplecast rules, ported from AnoMech's job tables and per-job handlers. Gauge fields are named by their FFXIVClientStructs offsets and read and write the same 16 bytes the server sends.
  - `Rules.GrantsOf` and `Rules.StatusesSpentBy` let a planner ask what an action grants or spends without resolving it.
  - Damage and heal numbers are not modelled yet.
- **Log traces.** `RuleSim traces <log>... --out=DIR` writes one JSON line per logged action: what was pressed, the statuses and gauge before it, and everything the server sent back for it (ActionEffect, statuses, gauge, EffectResults). 14 fixtures from the Dancing Mad logs are checked in under `tests/RuleSim.Actions.Tests/Traces`.
- **`tests/RuleSim.Actions.Tests`.** For every traced job at level 100, the resolver agrees with what the server sent, including Junno's own Single Standard Finish. The few differences left are listed in `TraceTests` with a reason each.
- **`docs/jobs`:** how completely each job is resolved, moved here from AnoMech.

### Fixed

Compared with the rules carried over from AnoMech, most of them found by the traces:

- Dancer finishes after steps (16191–16196) end the dance. Before, only the no-step finishes (16003/16004) did.
- Technical Finish's party buff is a separate action (33215–33218) about 0.6s after the finish.
- Raid buffs reach the party within their radius, not just the caster.
- Each Samurai Kaeshi spends its own Tsubame-gaeshi status, and Kaeshi no longer spends Meikyo Shisui.
- Red Mage's Dualcast is status 1249, the server's id, not 1393.
- Barrage at level 100 is status 128.
- Viper's serpent and dread combos match the server's gauge. The first twin bite grants the other bite's venom, and Ready to Reawaken waives Reawaken's cost.
- Black Mage's Paradox grants Firestarter in Astral Fire.
