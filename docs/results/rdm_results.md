# Red Mage replay (2026-11-18)

Repo files: `WrathCombo/CustomRules/Examples/RDM_ST.json` (and its Dancing Mad layer `DMU_Mit_RDM.json`), on the Jolt button.
Model: `src/RuleSim/RdmSim.cs`, commands `src/RuleSim/RdmCommands.cs`, symbols `rdm_symbols.json` (`gen_symbols.py RDM`).

```
dotnet RuleSim.dll run all rdm [--me=HEX] [--rules=DIR] [--set=a=b,...] [--ab=a=b,...] [--seeds=5] [--from=YYYYMMDD] [--detail] [--fires]
dotnet RuleSim.dll timeline <log> <pull> rdm --me=HEX [--from=s --to=s] [--trace] [--set=a=b,...]
```

## The model

- Numbers from the game data at level 100 (`tools/GameDump actions ... --job=35`): Jolt III 360 (2.0 s cast), Verthunder III /
  Veraero III 440 (5.0 s), Verfire / Verstone 380 (2.0 s), Grand Impact 600, Verflare / Verholy 650, Scorch 750, Resolution 850,
  Enchanted Riposte 340 / Zwerchhau 380 / Redoublement 560 in combo (recasts 1.5 / 1.5 / 2.2 s), Fleche 480 (25 s), Contre Sixte 420
  (45 s), Engagement 180 (35 s x2), Corps-a-corps 130, Prefulgence 1200, Vice of Thorns 950, Acceleration 55 s x2, Swiftcast 40 s,
  Embolden 120 s (own magic damage +10%), Manafication 110 s (3 Magicked Swordplay, Prefulgence Ready).
- Casts scale with the logged GCD; a hard cast resolves at its end and grants Dualcast (15 s). Instant order for a cast: Acceleration
  (Verthunder/Veraero III; it keeps Dualcast), Dualcast, Swiftcast. Every GCD but Grand Impact spends Dualcast. While Grand Impact
  Ready is up, Jolt III is Grand Impact (the game's button change). Procs rolled: Verthunder/Veraero III 50% (100% with
  Acceleration), Verflare/Verholy 20% (100% for the lower colour).
- Movement from the log, with two corrections: inside the logged player's completed casts they stood still (the position samples
  come about once a second, and the World puts a step at the end of its interval), and a cast only starts with no movement in the
  next 1.5 s (players see it coming). Without those the sim cancelled 69 casts per 10 minutes against 5.5 in the logs; with them 4.3.
- Not modelled: the mana imbalance rule (the rules keep the colours within 24), MP, raise.

## Calibration: 894 Dancing Mad Red Mage pulls (Sep 18 - Nov 18), 22 Red Mages, 5 seeds

| | sim | real |
|---|---|---|
| potency/min | 16,242 | 16,385 (-0.9%) |
| GCD/min | 23.18 | 23.68 |
| Acceleration / 10 min | 14.7 | 12.5 |
| Grand Impact / 10 min | 14.4 | 13.6 |
| cancelled casts / 10 min | 4.3 | 5.5 |

The user's 476 pulls: sim 16,343 against 15,781 real (+3.6%); the best real Red Mages are 4-9% above the rules' potency on
their own pulls.

## Acceleration before the pull (optPrepullAcceleration)

The guide's "pre-pull Acceleration for an additional Grand Impact". The static counts down from 16 s (697 of 894 pulls; the rest
10-17 s), too short for the Acceleration buff (20 s) to run out before the 5 s Veraero III precast (that needs 26 s), and while it is
up Veraero III is instant (it would pull early).

**First version (instant opener), rejected.** No precast: Acceleration with 19 s or less left, its instant Veraero III at the pull,
Grand Impact, then Manafication's combo. `--ab=optPrepullAcceleration=1`, 5 seeds: -0.08% overall (Phase 1 -0.14%, 3:20-9:00 -0.18%,
9 min or more +1.24% on 21 pulls); the first 30 s -0.4% (the precast and its Dualcast Verthunder III lost).

**Final version (click-off).** The user: Wrath's own Red Mage opener clicks the Acceleration off before the pull (RDM.cs,
`StatusManager.ExecuteStatusOff`, the First GCD opener). The rule engine got a `statusOff` rule field for it, and the option is
now: Acceleration with 25 s or less left, clicked off at once (its Grand Impact Ready, 30 s, stays), the usual Veraero III precast,
the Dualcast Verthunder III, Grand Impact, then Manafication's combo (one GCD later).

| pulls | n | potency/min | better / worse |
|---|---|---|---|
| all | 896 | 16,246 -> 16,367 (+0.75%) | 538 / 345 |
| under 3:20 (Phase 1) | 444 | +1.18% | 283 / 157 |
| 3:20 - 9:00 | 431 | +0.51% | 245 / 177 |
| 9:00 or more | 21 | +1.15% | 10 / 11 |
| the user's | 478 | +1.03% | 303 / 162 |

First 30 s 12,320 -> 12,460 a pull (+1.1%); Acceleration 4.90 -> 5.46 and Grand Impact 4.81 -> 5.41 a pull. Off by default (the user
asked for an option); it needs the plugin with `statusOff`.

## The gap to the top 5 through P2: luck, buffs and execution (2026-11-18)

`peers all rdm --from=20260918 --extra=<the top 5 converted FFLogs logs>`: the user's 24 complete pulls through P2 against FFLogs'
top 5 rDPS Red Mages. Gear is equal (the user's sheet x 1.05 party bonus = FFLogs' Intelligence; Jamie Jams' Crit 3,548 / DH 2,338
with food against the user's 3,457 / 2,230 without, both about 27.5% expected crit). The new columns:

- **no-luck DPS:** each hit divided by 1.62 if it crit and 1.25 if it direct-hit, and by the party's damage buffs (World.RaidBuffs
  without the rate buffs, plus a Dancer's Standard Finish on the player).
- **clean crit% / clean DH%:** hits under none of the rate buffs (Battle Litany, Battle Voice, Chain Stratagem, Devilment). With equal
  gear these differ only by luck.

| | pulls | DPS | no-buff | no-luck | crit% | clean crit% (hits) | clean DH% | GCD/min |
|---|---|---|---|---|---|---|---|---|
| the user | 24 | 36,385 | 35,464 | 27,696 | 28.5 | 27.7 (5,557) | 36.5 | 23.1 |
| FFLogs top 5 | 5 | 40,394 | 38,586 | 29,086 | 33.7 | 31.7 (1,187) | 36.2 | 24.6 |
| other Red Mages in the user's logs | 36 | 38,011 | 36,800 | 28,784 | 29.5 | 27.8 (8,509) | 33.9 | 24.3 |

- The user's crit is what their gear gives (27.7% against about 27.5%). The top 5's clean crit is 31.7%: 3.2 standard deviations
  (1.3 points on 1,187 hits) above it, and each of the five (30.4-34.1%) is above. Top parses are partly picked by luck. Direct hits
  are even.
- **Split of the -9.9%:**

  | Cause | Points |
  |---|---|
  | The party's damage buffs | 1.8 |
  | Crits | about 3.3: about 2 luck (the top 5's side), the rest the party's crit buffs covering more hits |
  | **Execution** | **4.8** |

  The 4.8 points remain with luck and buffs removed. They line up with GCDs per minute (23.1 against 24.6, -6%; against the other
  Red Mages in the user's logs, -3.8% and -4.9%).
- No Dancer partnered a Red Mage in any of these pulls (Devilment 0%).

### Interrupted casts through P2 (`peers ... --casts`, 2026-11-18)

An interrupt is a cast bar whose spell never landed before the player's next cast start (a 2 s cast normally lands about 1.47 s
in). ACT's cancel lines (23) don't decide it. 90 of the user's 408 sat on a cast that was restarted at once and landed, and many
interrupts have no cancel line. FFLogs logs (no cancel lines) are read the same way. `PEER_CAST_LOG=1` lists the user's interrupts.

| | pulls | casts/pull | P1 | P2 | per pull | % of casts | cast time lost/pull | restarted within 1 s |
|---|---|---|---|---|---|---|---|---|
| the user | 24 | 40.0 | 3.58 | 2.38 | **5.96** | 14.9% | 4.7 s | 1.83 |
| FFLogs top 5 | 5 | 35.4 | 0 | 0 | **0** | 0% | 0 | 0 |
| other Red Mages in the user's logs | 36 | 35.8 | 1.19 | 0.56 | 1.75 | 4.9% | 1.4 s | 0.56 |

- **Quick restarts.** The user had 666 casts restarted within 1 s over all their pulls. The other Red Mages had 116 over a similar
  number of pulls. The gaps spread over 0.1-0.7 s and the second cast always landed: a cast started just as the player stopped or
  started moving.
- **Cost.** 4.7 s of casting a pull is about 2 of the ~10 GCDs a pull the user is behind the top 5 on.

## The two cross-room runs (`moves`, 2026-11-18)

`moves all rdm --anchor=<cast>#n --win=a,b`: the user's longest standstill-to-standstill journey in the window. Steps are joined
across stops under 2.5 s, so a run around the edge stays one journey. Route: "middle" if it passes within 6 y of the centre, else
"edge" clockwise or counter-clockwise. Times are from the cast's start.

**P1, the second Graven Image** (cast at about 1:17.6; `--anchor="Graven Image#2" --win=9,25`; 419 of 428 pulls 15 y or more).
You wait at the north spot (y about -12), then run to the south (y about +10), down the middle or counter-clockwise around the west
edge. Since 10/20 (159 runs):

| route | pulls | start (p10-p90) | end | duration | moving | path |
|---|---|---|---|---|---|---|
| middle | 103 | +10.8 (+10.3-+11.1) | +15.1 (+14.6-+20.1) | 4.5 s | 3.1 s | 27 y |
| edge CCW | 56 | +10.5 (+10.3-+11.0) | +19.7 (+17.7-+21.7) | 8.8 s | 5.4 s | 47 y |

- **Start.** +5.5 s after the third Blizzard III Blowout's cast start (p10-p90 +4.4-+6.0). That is about 0.8 s after the Blowout
  resolves (4.7 s cast), and 4.4 s before Revolting Ruin III #2 starts.
- **Early runs.** Before 10/20, a few started from somewhere other than the north spot.

**P3, Bowels of Agony** (Chaos, cast at about 7:25.5; `--anchor="Bowels of Agony#1" --win=0,17 --min-disp=8`). You stand a few
yalms east of the centre and run to the wall, a different way each time (N, S, W, SE, NE, SW). Chaos moves from the centre to a
corner during it, but not one that maps to your direction.

| pulls | start (p10-p90) | end | duration | moving | displacement |
|---|---|---|---|---|---|
| 16 since 10/20 | +8.5 (+7.5-+9.9) | +13.9 (+13.0-+16.7) | 5.8 s | 3.4 s | 16.7 y |
| all 23 | +9.2 (+7.5-+12.1) | +14.6 | 5.4 s | 3.3 s | 16.2 y |

- **Start.** About 3.8 s after Bowels of Agony resolves (4.7 s cast). The first Thunder III starts 8.7 s later (+17.1).
- **Markers.** No head marker, tether or enemy status lands on you in the window.
