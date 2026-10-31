# White Mage audit (2026-10-23): WHM_ST_DPS.json + DMU_Mit_WHM.json

Data: `../whm/JobList` (index scan) found 1,798 Dancing Mad pulls with a White Mage (34 players, 79 player-nights,
Sep 19 - Oct 23, 3,835 min of target-up time; 1,111+ ended in P1, ~280 in P2, 9 in P3) -> `../whm/whm_pulls.csv`, `sets.txt`.
`../whm/WhmScan` = DncScan with job 24 (CSV dump of every White Mage's actions/statuses, `../whm/scan`).
Replay: copy of the shared rulesim (WhmSim) with `--events` (EVP presses, EVC enemy casts, EVH party hits, EVE end).
`ev.sh <variant> a|b [--events]` runs every set 6 at a time (`ev_retry.sh` reruns index-save collisions), `agg.py` totals,
`whm_slots.py` per sheet row, `a_loj.py` (Plenary vs LoJ + Hyperdrive), `a_rel.py` (hit offsets after a cast), `a_gap.py`,
`a_gi3.py`, `idle.py`. Variants in `v/` (base = repo before; final = repo after).

## Findings
1. Weave-only oGCDs never fire while the DPS file holds the GCD (moving with nothing instant, or no target): Wrath's
   CanWeave needs RemainingGCD > 0.6 + lock, and a held GCD has 0. Trace example: 09-22 #79, the Indulgent Will Caress
   window spent entirely moving. Fixed with `gcdHeld` (not casting, GCD < 0.5 s, moving >= 0.5 s with nothing instant, or
   no target) on the plan entries and the DPS file's heals/mitigation; PoM, Assize, Lucid, 2-charge Benison stay weave-only
   (PoM during a hold put Glare IVs outside buffs: clear -0.46%). Same pattern in SGE_ST_DPS, RDM_ST, RPR_ST/AoE,
   WHM_AoE_Heal (moving Waits + weave rules): reported, not changed; an engine fix (held GCD = weave window) would cover all.
2. LoJ Plenary backup in WHM_ST_DPS (castIn < 4.5) pressed ~4 s before the hit: Confession ended before Hyperdrive's
   3rd hit (Hyperdrive 3.2/5.3/7.4 s after LoJ; LoJ hit 0.29 s after cast end). Sheet footnote: late in the cast.
   Now castIn < 2.8 (+ non-weave last chance < 1.2): Hyperdrive hits covered LoJ1 79% -> 99.9%, LoJ2 69% -> 99.8%.
3. dtt3 Caress was eaten by the trap (Indulgent Will Caress 139/640 -> 629/640).
4. dtt3 Temperance at GI3+3..7 expired before Mystery Magic (FF3 at GI3+26.5; trap 7.1; IW 12.8): now 5.9..12 with a
   non-weave last chance 6.0-6.5: MM carry 23 -> 527/619, trap 605 -> 462/631, IW carry 619 -> 632.
5. Wings Plenary at cast start ran out before Ultimate Embrace (Wings hit +4.1, UE +11.1): castIn < 3 -> 20 -> 36/36.
6. P3-P5 (clear only): Stray Flames 2 Temperance 20.7-22.2 s after Thunder III 2 (carries to Vacuum Wave), Grand Cross
   1/2 castIn < 5 (carry to Inferno / GC3), P5 Forsaken hit 1 castIn < 4 (carry to the 5th hit); the DPS file's DMU backups
   (Towers VI from 8.5 s, Slap Happy long casts, Vacuum Wave < 5, Forsaken 5th/6th one cast earlier, Fell Forces Assist
   Tanks) moved into the plan's windows (they also ignored optOnDpsButton and the heal button). Towers V window now runs to
   ATE#2 + 5.3 s (the later dtt3 Temperance comes back ~2.5 s later).
7. lilyCapping `lily == 3` = identical potency (10,327 both): drops the unverified gauge(LilyTimer) direction.

## Results (step 0.25/slide 700 = a; 0.5/900 = b)
| | base a | final a | base b | final b | logged |
|---|---|---|---|---|---|
| WHM sets pot/min | 10,335 | 10,327 (-0.07%) | 10,707 | 10,700 (-0.06%) | 8,990 |
| clear (AST stand-in) | 10,932 | 10,900 (-0.29%, P3 timing noise) | 11,399 | 11,397 (-0.02%) | |
| sheet slots /13,436 | 11,921 | 12,825 | 11,996 | 12,821 | 8,682 |
| 20%+ hits with WHM mit /6,305 | 4,562 | 4,787 | 4,590 | 4,785 | 3,770 |
| clear slots /61 | 52 | 59 | 53 | 59 | |
P1 slots 89.0% -> 96.1%, P2 88.6% -> 88.9% (a+b). Costs: last-chance Temperance ~-0.02%, gcdHeld clips ~-0.03%.
Healing counterfactual mean 5.2 -> 4.4 (Caress/Aura moved later; mitigation isn't in that metric).

Ablation on the final (a): PoM/Assize at first buff -0.60% if removed, opener Misery wait -0.34%, no-target Lily -0.06%,
Glare IV at end of buffs -0.01% (clear -0.10%). All Oct 21 choices hold on 1,798 pulls.
Plugin engine (Release dll) compiles both; harness Unit 77, E2E all passed. Not installed live.
