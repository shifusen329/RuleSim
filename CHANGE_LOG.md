# Changelog

Notable changes to RuleSim. It has no versions; entries are by date.

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
