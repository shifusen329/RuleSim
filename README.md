# RuleSim

Offline tools for tuning WrathCombo's Custom Rules against real fights.

- **RuleSim** replays a logged pull through the rule files and scores what the rules would have done against what the real player did.
- **RuleHarness** compiles every rule file and checks it against the engine's unit and end-to-end tests.

## How the replay works

1. **The world comes from the log.** raid-replay's `RaidReplay.Core` rebuilds the pull from an ACT network log (`PullLoader.Load`). `World.cs` reads that rebuild:
   - boss casts, with WrathCombo's `EnemyCasts` meaning;
   - every actor's position, facing and HP;
   - movement;
   - other players' buffs and debuffs;
   - hits on the party, raidwides and deaths.
2. **The simulated player comes from the rules.** A job model (`Sage.cs`, `SamSim.cs`, `WhmSim.cs`, `DncSim.cs`, `AstSim.cs`) stands in for one player in the log, chosen with `--me=<actor id>`. It tracks that player's own:
   - GCD, weaving and animation lock;
   - cast times, and casts cancelled by movement;
   - MP and job gauges;
   - charges and cooldowns;
   - statuses, DoTs and HoTs;
   - shields broken by hits.

   Everything else, including the player's own movement, still comes from the log.
3. **The rules are evaluated like the plugin evaluates them.** `RuleSet.cs` and `RuleExpressions.cs` are compiled straight from the WrathCombo checkout. Every 20 ms the job model "presses" the button: the compiled rule files are evaluated, layers first, and the chosen action is used if it is ready.
4. **Scoring.** `Metrics.cs` and the per-job `*Commands.cs` files compare the sim with the real player. They measure potency per minute, GCDs, cast cancels, healing, mitigation coverage and raid-buff alignment.

Movement is the main approximation. The log gives a position about once a second, so a step between two samples is treated as running for the time it needs. That model was calibrated against real cast cancels.

## Layout

| Path | What |
| --- | --- |
| `src/RuleSim` | The replay. One executable; the job is picked by arguments (see the header of `Program.cs` and each `*Commands.cs`). `*_symbols.json` are generated from WrathCombo's job constants by `gen_symbols.py`. |
| `src/RuleHarness` | The rule harness (unit and E2E checks over every rule file). |
| `legacy/SamSim` | The first Samurai simulator: fixed GCD, full uptime, no log. Superseded by `src/RuleSim/SamSim.cs`; kept for reference. |
| `docs/results` | Tuning write-ups produced with the replay (Sage, Samurai, White Mage, Dancer). |

## Requirements

- The .NET 10 SDK.
- A WrathCombo checkout (the rule engine source and the rule files) and a raid-replay checkout (`RaidReplay.Core`). `Directory.Build.props` finds each one in this order:
  1. The repository RuleSim is a submodule of, when it is that one (`WrathCombo/RuleSim`).
  2. A sibling checkout: `../WrathCombo`, `../raid-replay`.
  3. One beside the repository RuleSim is a submodule of: `../../WrathCombo`, `../../raid-replay` (e.g. from `AnoMech/RuleSim`).

  To point elsewhere, add `-p:WrathComboDir=...` or `-p:RaidReplayDir=...` to `dotnet build`.
- For RuleHarness only: a Release build of WrathCombo.

## Build and run

```
dotnet build RuleSim.slnx -c Release
```

RuleSim (run from `src/RuleSim/bin/Release/net10.0`):

```
dotnet RuleSim.dll pulls <log> [pulls]                    # list pulls ("1-500" for all; default: your Sage pulls)
dotnet RuleSim.dll run <log> <pulls> --me=<actor id>      # Sage: sim vs actual, per pull and in total
dotnet RuleSim.dll timeline <log> <pull> --me=<actor id>  # merged timeline: sim, actual, boss casts, raidwides
dotnet RuleSim.dll run <log> dnc|whm|sam|ast ...          # other jobs; see DncCommands.cs, WhmCommands.cs, SamCommands.cs, AstCommands.cs
```

Common options:

- `--rules=DIR`: which rule files to use.
- `--set=name=value,...`: override rule defines, for A/B runs.
- `--trace --from=s --to=s`: print each decision in a time window.

RuleHarness (run from `../WrathCombo/WrathCombo/CustomRules/Examples`; the two Samurai files must come first):

```
dotnet <this repo>/src/RuleHarness/bin/Release/net10.0-windows/RuleHarness.dll <WrathCombo bin/Release> SAM_Simple.json SAM_Lv100_SlowMAL.json <every other *.json>
```

To get a log of someone else's pull, use WrathCombo's `utils/fetch.py` (downloads one FFLogs fight) and `utils/convert.py` (turns it into an ACT network log that raid-replay reads).

## Known gaps

- Default paths are hard-coded to `C:\Users\quanh\Workspace\WrathCombo`: the rules folder (`--rules`), the guide sheets (`--sheet`) and `gen_symbols.py`'s root argument. Pass them explicitly on another machine.
- The sim does not implement every rule builtin. For example, `enemiesHp` is missing, so `SGE_AoE_DPS.json` fails to compile in the sim, and the run reports this at the start.

## Provenance

Moved on 2026-10-31 out of Claude Code session scratchpads, where these tools had been copied forward from session to session:

| Here | Taken from (newest copy) | Earlier copies |
| --- | --- | --- |
| `src/RuleSim` | `sgeaudit/sim` (2026-10-30) | `rulesim`, `whmsim`, `dncsim`, `samba/sim`, `dncaudit/sim`, `tankcare_whm/sim_orig` |
| `src/RuleHarness` | `harness` (2026-10-31) | `harness` (10-24), `harness2` |
| `legacy/SamSim` | `sim` (2026-10-21) | |
| `docs/results` | `rulesim`, `dncsim`, `whmsim` | |

Two changes were made in the move:

- The projects now build against the live WrathCombo and raid-replay checkouts. Before, they used a copied pair of rule engine files and a `RaidReplay.Core.dll` snapshot from 2026-10-26.
- `legacy/SamSim` gained a one-line `Retarget`, so it builds against the current rule engine.

Checked on 2026-10-31 with the old and new builds on the same inputs. The outputs were identical in both cases:

- a Sage replay of the full Dancing Mad clear;
- the harness over every rule file.
