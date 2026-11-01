# Astrologian Custom Rules against the top five FFLogs Astrologians (2026-11-01)

The Astrologian replay (`src/RuleSim/AstSim.cs`, `AstCommands.cs`) ran WrathCombo's `AST_ST_DPS.json` and
`DMU_Mit_AST.json` (custom-rules branch, uncommitted) against the five best Dancing Mad (Ultimate) Astrologians by rDPS
on FFLogs (encounter 1085), each fight converted to an ACT log by WrathCombo's `utils/convert.py`. Flags:
`run all ast --me=<hex> --from=20200101 --step=0.5 --slide=900 --slotlist --tanks --cfhp` with `SIM_AUTOS=1`.
Judge potency on the single-boss phases (pot1: P1, P2, P5); P3 and P4 score the rules' Gravity II on more bosses than the
fight has.

| # | Astrologian | report / fight | rDPS | party |
|---|---|---|---|---|
| 1 | Yamii Chan (Gilgamesh) | Kkm3ZRB1xLDndHPW / 8 | 24,787 | SAM BRD PCT SGE NIN PLD DRK |
| 2 | anonymous | a:F8Xf92m7WYxRr6pd / 1 | 24,766 | DNC PCT SCH RDM MNK PLD DRK |
| 3 | Koyuki'' Veritas (Anima) | c6BFMGkgWrPbtpa2 / 9 | 24,304 | DRK SGE PLD VPR SMN RPR BRD |
| 4 | Kurisu Tan (Hades) | AD96x4p3vftkawXB / 9 | 24,300 | DRK PLD RDM DNC DRG SAM SCH |
| 5 | anonymous | a:cjVYWg6t3LyMrFqB / 5 | 24,256 | DRK SMN PLD SAM DRG DNC SCH |

## What the five did

- Opener: Fall Malefic lands at 0:00, Divination in the first weave (0.7-2.3 s), Combust III, Lightspeed, The Balance,
  Lord of Crowns, Umbral Draw, The Spear, Oracle by 6.4-8.2 s. Divination then on cooldown with the party's buffs.
- Cards: The Balance on the top melee (SAM, MNK, VPR), The Spear on the top ranged (PCT, RDM, SMN): Wrath's card target
  picks the same.
- Earthly Star: on the boss (0-3.5 y from its centre in the single-boss phases), 17-18 a fight, 7.8-8.0 of 8 healed at
  each explosion, 10-13 set off by hand, mostly inside Divination. Phase 3: placed 0.8 s before to 4.1 s after the second
  Decisive Battle (Earthquake 11.8-12.6 s after it), gone off 7.1-8.3 s into Earthquake.
- 0 cast cancels; Swiftcast 8-17, Lightspeed 16-20 a fight (one before each Divination, the rest for movement).
- Fell Forces (P5) is a party-wide hit that does 2-4x to the tanks; their answers varied (nothing, Intersection,
  Essential Dignity).

## Baseline (the files as installed, 2026-10-30) against them

Divination 0.3-5.7 s after theirs (#2: 4-6 s every time). Trace (#2, 1:56-2:04): moving with nothing instant,
`Hold the cast while moving` held the GCD; with no GCD rolling Wrath sees no weave window, and Divination, the burst
Lightspeed and the cards were weave-only; `lsSpendable` kept the Lightspeed charge for the burst, so nothing broke the
hold. Earthly Star where the Astrologian stood: 7.33-7.89 of 8 healed (a 4/8 and a 5/8). Ultima Repeater 1 Collective
Unconscious missed in #4 (a Fall Malefic cast covered its 1.4 s window).

## Changes and results

1. Burst rules (Divination, Play I, Lightspeed, Lord, the before-draw cards, the draw, Oracle): `(canWeave || gcdHeld)`.
2. `enemy` target kind (WrathCombo RuleSet.cs/RuleBuiltins.cs; AstSim.Retarget): Earthly Star `"enemy, self"`
   (optStarOnTarget, default 1).
3. Stellar Detonation inside Divination when it would go off after it (optStarInBurst, default 1).
4. `starKeep` from the second Decisive Battle + 7 s (the plan's pre-Accretion detonation at Earthquake + 5.2 still finds it
   charged).
5. DMU_Mit_AST: `cuRepeaterSoon` look-ahead (no cast in the 1.5 s before the Repeater's CU window).
6. DMU_Mit_AST: Microcosmos for the last Accretion (`microLastAcc`: one left, no Earth Resistance Down II, 3.5 s after the
   first came off). Four of the five removed the second Accretion with Microcosmos 4.0-7.2 s after the first; the old plan
   waited for both to be gone, so Macrocosmos's own heal (Earthquake +16 to +19.4) fell on the last holder (6.2-6.4 s after
   the first removal in two top-five replays, 2.3 s after it in one of the user's 483 pulls: inside the 3.2 s danger).
   After: Microcosmos on the last holder 3.5-4.6 s after the first removal in four of the five, no heal inside 3.2 s.
7. DMU_Mit_AST: Earthquake's Macrocosmos from 2.6 s into the cast (was 1 s), the GCD held from 1.1 s (`macroEqSoon`, a
   Wait: the shared look-ahead's instant Combust III ran its GCD past the window). Its own heal then comes at +17.6 or
   later. With 5 and 6 only, that user pull (#506 of Network_30304_20261023, Astrologian 100726D6, first Accretion off at
   +14.1) had Macrocosmos pressed at +1.0 and its heal on the last holder 2.3 s after the first came off; now 3.5 s. All
   five top-five Earthquake Macrocosmos slots kept.

Top five, base -> final (rules / real):

| | base | final | real |
|---|---|---|---|
| pot1 (sum of the five) | 41,817 | 41,732 (-0.2%) | 43,280 |
| party damage from Divination + cards | 34.41M | 35.89M (+4.3%) | 36.26M |
| Divination overlap with other raid buffs (#2) | 18.4 s | 19.9 s | 20.0 s |
| Earthly Star party healed per explosion | 7.33-7.89 | 7.89-8.00 | 7.78-8.00 |
| cast cancels | 193 | 177 | 0 |
| sheet slots | 246/260 | 246/260 | 131/260 |
| tanks the rules would lose | 3 | 2 | - |

pot1 moves +-1-3% per phase on one fight with small timing shifts (cast cancels against the coarse remote movement), so
the 483-pull scan below is the judge. Tried and dropped: optDivinationAt 0.6 (party -1.2%, pot1 #4/#5 -1.2..-1.5%).

## The 483 pulls of 22 Astrologians in the user's logs (mostly P1/P2)

`run23.sh` in the session scratchpad (the first Astrologian alone, then six at a time: parallel runs raced to save the
index of the growing current ACT log). Base reproduces the 2026-10-30 v7 totals exactly.

| | base | final | logged |
|---|---|---|---|
| pot1 (per minute) | 8,316 | 8,309 (-0.08%) | 7,792 |
| party damage from Divination + cards | 533.9M | 535.1M (+0.2%) | 525.1M |
| Earthly Star party healed per explosion | 7.33 | 7.62 | 7.87 |
| stars going off inside Divination | 827/1,499 | 821/1,502 | 745/1,367 |
| cast cancels | 5,727 | 5,713 | 596 |
| sheet slots | 4,654/4,982 | 4,655 | 3,413 |
| Accretion: rules' heals inside 3.2 s of another removal | 0 | 0 | |
| tank-s under 30% p1 / p2 / p3 | 982 / 294 / 127 | 950 / 291 / 146 | 1,663 / 358 / 246 |
| tanks the rules would lose | 23 | 24 | |

Their bursts were mostly already on time (the user's groups' buffs; Divination overlap 19.1 s both), so the held-GCD burst
shows in the top five, not here. Tried and dropped: holding Hyperdrive's second Intersection for its third hit
(`hyperdriveNext`, two forms: 23 and 24 tanks lost against 24 without it; the Hyperdrive losses only moved between pulls).
The in-burst Stellar Detonation fires 0-2 times a fight (whole-fight potency +0.06%, kept).
