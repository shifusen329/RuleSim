# Sage Custom Rules optimisation for Dancing Mad (Ultimate): results log

Runs: `ev.sh <variant>` = the clear (Network_30301_20261005, --me=100EC7EB) at default movement (c25: step 0.25,
slide 700) and at step 0.5 / slide 900 (c50), plus the 31 Oct 20 pulls (o20, default movement, --me 1001A4A8).
Columns: potency/min, GCDs/min, cast cancels, rules' lowest MP, Sage shield up at party-wide hits of 20%+ HP,
any Sage mitigation at those hits, sheet slots covered (Sage column), healing segments where the rules heal at
least the real Sage's effective healing, lowest counterfactual HP%.

## Replay changes (in order)

1. **Metrics added** (Metrics.cs, Program.cs `run`): sheet-slot coverage (a C# port of mit/reconcile.py's row-to-hit
   matching, checked for the rules and for the real Sage; the port gives the real Sage 72/102 where reconcile.py
   gives 73, the difference is one Zoe-shields slot, see 3), big-hit shield / mitigation counts, a counterfactual
   healing metric, rules' MP low, `--slots` and `--heal` detail, and a `bighits` report.
2. **Coverage counts effects from 0.6 s after the press** (the log shows buffs landing 0.5-0.7 s after the press).
   Before, a press in the last instant before a hit counted. Shields at 20%+ hits for the repo rules: 11 -> 10.
3. **Zoe shields** use reconcile.py's rule: shields up and Zoe's status ended (taken by a heal GCD, or ran out) in the
   30 s before the hit.
4. **Heal events** recorded by the replay (Sage.cs): direct heals (Diagnosis 450, Eukrasian Diagnosis 300, Prognosis 300,
   Eukrasian Prognosis II 100, Pneuma 600, Ixochole 400, Druochole 600, Taurochole 700, Holos 300), HoT ticks
   (Kerachole 100 and Physis II 130, every 3 s for 15 s), Kardia 170 (Soteria +70%, 4 heals) and Eudaimonia 150 on each
   damaging spell; Zoe +50%, Philosophia +20%, Physis II +10% for its first 10 s, Krasis +20%. Shields are not healing.
   HP per potency is calibrated on the real Sage's direct heals of known potency in the same pulls (clear: 98, Oct 20: 102).
5. **Hits from abilities that never damage anyone are not hits** (World.cs). Tele-trouncing, the Path of Light and
   White Hole land on players with 0 damage every time (32 of 32, 16 of 16, 8 of 8 in the clear); they broke
   the replay's shields, the Path of Light 0.6 s before the Spell's Trouble stacks.
6. **Panhaima** loses a stack per hit on the Sage (hits within 200 ms count once, e.g. the four Gravity III helper hits;
   hits from an instance averaging under 3% of HP on the party take none), each stack restarts its 15 s timer, and the
   fifth ends it. The real Sage's Panhaimas in the clear lasted 21.2, 20.5, 15.0 (no hits), 15.6 (through the Ultima
   Blasters, ended at Cyclone), 17.7 s (through four Forsaken hits, ended on the fifth) and so on. Applied to the real
   Sage's own presses, the model agrees with the logged status at 19/19 Panhaima slots (run --slots prints the check).
   Before: 15 s flat. (A first version counted every hit; it disagreed at the Ultima Blasters.)
7. Checked and **not** changed: Eukrasia. The logs show Eukrasia + its Eukrasian GCD taking one GCD (2.5 s): in the game
   data Eukrasia is on the GCD group and the Eukrasian actions have a 1.5 s recast; the replay's 1.0 s + 1.5 s x GCD/2.5
   already matches. So a shield costs exactly one GCD.

The healing metric: segments start at each cluster of 20%+ party-wide hits (hits under 10 s apart are one cluster;
the clear has 27 segments). Within a segment, per player, counterfactual HP = logged HP - the real Sage's effective
healing so far in the segment (heal minus overheal, direct and ticks) + the rules' healing so far (each heal capped at
the counterfactual missing HP). A segment passes when the rules' effective healing >= the real Sage's. The lowest
counterfactual HP% across the segment is also reported against the lowest logged HP%.

## Baselines after the model changes

| variant | run | pot/min | GCD/min | cancels | MP low | shields 20%+ | any mit 20%+ | sheet slots | heal segs | cf HP low |
|---|---|---|---|---|---|---|---|---|---|---|
| real Sage (Lost Too) | clear | 7977 | 22.6 | 0 | 3450 | 35/40 | 40/40 | 72/102 | - | - |
| repo | c25 | 8782 | 23.6 | 85 | 6000 | 10/40 | 34 | 74/102 | 18/27 | -29 |
| repo | c50 | 9288 | 23.9 | 54 | 4950 | 12/40 | 34 | 70/102 | 18/27 | -29 |
| repo | o20 | 8822 | 24.7 | 163 | 4350 | 117/136 | 133 | 293/389 | 128/158 | -33 |
| real Sage (user) | o20 | 8505 | 22.0 | 123 | - | 101/136 | 123 | 281/389 | - | - |
| clear1 | c25 | 8805 | 23.6 | 82 | 6000 | 10/40 | 37 | 81/102 | 20/27 | -25 |
| clear1 | c50 | 9288 | 23.9 | 54 | 4950 | 12/40 | 37 | 78/102 | 19/27 | -20 |
| clear1 | o20 | 8822 | 24.7 | 163 | 4350 | 117/136 | 133 | 293/389 | 128/158 | -33 |

The real Sage's 73/102 matches reconcile.py's count (Holos 18, Kerachole 29, Panhaima 16, Philosophia 3, Zoe shields 7).

## Variants

### s1: shield windows for every party-wide hit (DMU_Mit_SGE.json)

New windows open after the last hit that breaks the previous shield (Explosion, Gravity III, the Spell's Trouble cone and
spreads, Ultima Blaster #8, Grand Cross, Inferno/Tsunami, Death Surge, Ultima Upsurge, each Fell Forces, each Forsaken
and Forsaken Bonds) and close when a shield pressed then still lands 0.6 s before the hit. In the 3-5 s chains a
"...Pre" window presses Eukrasia in the 1.2 s before the breaking hit and holds it, so the shield follows that hit.
Zoe only in the sheet's ten Zoe-shield windows (the first one before the first Graven Image, so Zoe is back for the
second Trap). Plan GCD rules wait for the end of the weave window (!canWeave).

| run | pot | GCD | canc | MP low | shields | any mit | slots | heal | cf low |
|---|---|---|---|---|---|---|---|---|---|
| c25 | 8546 | 23.6 | 75 | 5650 | 38/40 | 40 | 82/102 | 21/27 | -23 |
| c50 | 8778 | 24.0 | 47 | 4750 | 39/40 | 40 | 80/102 | 19/27 | -47 |
| o20 | 8816 | 24.7 | 153 | 4050 | 120/136 | 132 | 325/389 | 125/158 | -40 |

Shields cost 259 (c25) and 510 (c50) potency a minute here, mostly because the tank Eukrasian Diagnosis (under 2
Addersting) kept firing 29 times although the party shields now feed Addersting (Addersting capped 54 s).

### Tank shield (tankShieldWanted) options on top of s1

| variant | c25 | c50 | o20 |
|---|---|---|---|
| s1: tank shield whenever under 2 Addersting | 8546 | 8778 | 8816 |
| s1m: optTankShieldOnlyMoving 1 | 8692 (+146) | 9157 (+379) | 9181 (+365) |
| s1m0: moving and 0 Addersting | 8837 (+291) | 9083 (+305) | 9288 (+472) |
| s1x: no tank shield (measurement only; the user keeps the rule) | 8768 (+222) | 9157 (+379) | 9326 (+510) |

Coverage is the same in all four (shields 38-39/40, any 40/40). s1m adopted as the base; s1m0 revisited below.
s1m: c25 8692 / c50 9157 / o20 9181; shields 38/40, 39/40, 119/136; slots 79, 81, 326; heal 21/27, 18/27, 128/158.
Oct 20 regressions to fix: pull 39 lost Kerachole at the River of Light (5:23); pull 46 lost the shield at Wave Cannon
(0:42.6, 38% of HP; Wave Cannon is a 20%+ party hit in 4 of the 31 pulls and the new Trap window starts after it).

All rows below use the final replay model (changes 1-7 above). Format per variant: c25 / c50 / o20.

| variant | change | pot/min | shields 20%+ | any mit | sheet slots | heal segs | MP low |
|---|---|---|---|---|---|---|---|
| repo | baseline | 8782 / 9288 / 8822 | 10/40, 12/40, 117/136 | 34, 34, 133 | 74, 70, 293 | 18, 18, 128 | 6000, 4950, 4350 |
| clear1 | the four validated window fixes | 8805 / 9288 / 8822 | 10, 12, 117 | 37, 37, 133 | 81, 78, 293 | 20, 19, 128 | 6000, 4950, 4350 |
| s1 | shield windows for every party hit | 8546 / 8778 / 8816 | 38, 39, 120 | 40, 40, 132 | 82, 80, 325 | 21, 19, 125 | 5650, 4750, 4050 |
| s1m | + optTankShieldOnlyMoving 1 | 8692 / 9157 / 9181 | 38, 39, 119 | 40, 40, 132 | 79, 81, 326 | 21, 18, 128 | 5450, 5050, 4750 |
| s2 | + Kerachole chained through its cooldown, Wave Cannon shield | 8692 / 9135 / 9104 | 38, 39, 122 | 40, 40, 132 | 82, 83, 337 | 21, 18, 127 | 5450, 4800, 4950 |
| s3 | + P2 Light of Judgment Kerachole dropped for Wings + Ultimate Embrace; Towers VII from 1.2 s into the bar; Flood / Orchestra #2 tightened | 8692 / 9135 / 9104 | 38, 39, 122 | 40, 40, 133 | 86, 85, 337 | 21, 19, 127 | 5450, 5400, 4950 |
| s4 | + Zoe kept for the sheet's Zoe shields (no Zoe for Pneuma in DMU, held Zoe not spent on tank / spare shields), first Zoe 4 s earlier, Zoe right after Eukrasia | 8771 / 9148 / 9103 | 38, 38, 122 | 40, 40, 133 | 92, 88, 357 | 21, 19, 125 | 5650, 5500, 4950 |
| s5 | + Holos / Panhaima carry windows, Forsaken Panhaima before the 3rd hit, P3 Philosophia dropped for Grand Cross | 8771 / 9147 / 9103 | 38, 39, 122 | 40, 40, 133 | 94, 86, 357 | 20, 20, 125 | 5650, 5500, 4950 |
| s6 | + windows widened to what the carries allow (Wings, Flood, Orchestra #2, Forsaken Holos), Ultima Blaster last-chance presses | 8771 / 9148 / 9103 | 38, 38, 122 | 40, 40, 133 | 97, 97, 357 | 20, 20, 125 | 5650, 5700, 4950 |
| s7 | + Thunder III Panhaima 0.2-2 s before the bar ends (it must reach 10:51 with nothing hitting the Sage after 10:29) | 8737 / 9148 / 9103 | 38, 38, 122 | 40, 40, 133 | 98, 98, 357 | 20, 20, 125 | 5650, 5700, 4950 |

Notes:
- s2: Oct 20 pull 39 lost Kerachole at the River of Light (5:23, 25%): that Kerachole landed 15.0 s before the hit
  (All Things Ending #3 starts 16.4 s before it there, 15.3 s in the clear); fixed in s3 by starting 1.2 s into the bar.
- s3: three Kerachole presses 30 s apart for Towers VII (+ VIII carry), Light of Judgment (P2) and Wings fit only in
  1 s windows. Light of Judgment keeps Holos, the Zoe shield and Physis II.
- s4: "Zoe for Pneuma" took Zoe six times on the clear; Zoe shields 4/10 -> 10/10.
- s5: footnote 12 (Philosophia after both Accretions, 9:31) plus the 180 s cooldown leaves 0.4 s for the Grand Cross
  row (12:32); the Grand Cross row is kept. With the stack model a Panhaima between Forsaken Bonds #1 and Forsaken #3
  covers hits 3-7 (clear1's later press only 4-7).
- Remaining slot misses at s7 (both movement settings): Gravitas II Kerachole 1:46.8 (26 s before Light of Judgment's,
  impossible with a 30 s cooldown; Light of Judgment's 30% hit kept), Light of Judgment P2 Kerachole (above), Earthquake
  Kerachole 9:20.2 (a 9:19 press cannot reach Shocking Impact 9:39; the 9:24 press covers both Accretion Earthquakes and
  Shocking Impact), Earthquake Philosophia 9:20.2 (footnote 12).
- Shield misses at s7: Ultima Repeater #4 (16:22.5): its first hit 0.7 s earlier takes the shield; no shield can land in
  between (the real Sage had none either). Forsaken #4 at c25 (to investigate).
| s8 | + pre-Eukrasia windows open 1.3 s before the breaking hit (Forsaken, Grand Cross) and overlap the end of the shield window before them | 8738 / 9148 / 9103 | 39, 39, 122 | 40, 40, 133 | 98, 98, 357 | 20, 20, 125 | 5650, 5700, 4950 |
| h80 / h85 / h90 | + optPneumaBelow (Pneuma below 80 / 85 / 90% party HP instead of 70) | 8729-8742 / 9142-9145 / 9102-9107 | 39, 39, 122 | 40, 40, 133 | 98, 98, 357 | 20/19/127, 20/20/129, 20/19/126 | |
| s9 (= h85 +) | + last-chance presses for narrow windows, Towers VI Philosophia from the 2nd All Things Ending, Decisive Battle shield from 9:00 | 8742 / 9145 / 9100 | 39, 39, 122 | 40, 40, 134 | 98, 98, 359 | 21, 21, 130 | 5450, 4950, 4550 |
| s10 | + 0.2-0.3 s margins on every carry window (checked against the clear), Thunder III and Ultima Repeater Holos windows, Upsurge last chance | 8742 / 9145 / 9097 | 39, 39, 122 | 40, 40, 134 | 98, 98, 360 | 21, 21, 129 | 5450, 4950, 4550 |

Side experiments (not adopted):
- d1 / d1b / d2: tank shield only while moving and at 0 Addersting, on h85 / s9 / s10: c25 +1 / +21 / +21,
  c50 -37 / -37 / -65, o20 +130 / +127 / +130; c50 lost 4-5 slots on h85/s9 (window-edge presses, fixed in s10) and
  heal segments drop 2-3. Mixed, so the tank shield stays "under 2 Addersting, while moving".
- s8: Forsaken #4 / #5 shields were missed by 0.2 s because a GCD started 0.05 s before the pre-Eukrasia window.

Robustness of s10 at other movement settings (clear): step 0.35 / slide 800: 8904 pot (repo 8916), shields 39/40
(repo 11), slots 98 (repo 73); 0.25 / 900: 8928 (9053), 39 (9), 96 (75); 0.5 / 700: 9122 (8898), 39 (11), 96 (72);
0.75 / 900: 9201 (9376), 39 (11), 98 (72).
| s11 | + pre-Eukrasia before the second Trap (it takes the Zoe shield 2.7 s before Gravity III; 26% of HP in some Oct 20 pulls), Trap #2 Kerachole from +24.8 s | 8742 / 9145 / 9086 | 39, 39, 122 | 40, 40, 134 | 98, 98, 359 | 21, 21, 127 | 5450, 4950, 4750 |

Further replay metrics added at this point (Program.cs): per-segment healing detail by source and the worst
counterfactual moment; healing cumulative from the pull at each segment end and over each pair of neighbouring
segments; DoT ticks; Addersting lost to the cap; `--shields` (what took each of the rules' shields).

- Oct 20 per-raidwide coverage against the repo (cmp_cov.py): s10 dropped Gravity III shields 14 -> 11 (the second Trap
  breaks the Zoe shield 2.7 s before it; a Dosis started just before the Trap kept the GCD) and Trap #2 Kerachole 4 -> 0.
  s11 fixes Gravity III (14 -> 14). Trap #2 Kerachole stays 4 -> 2: covering both Trap #2 and Light of Judgment #2
  needs a press in 0.7 s (Revolting Ruin III + 24.6-25.3 s); s12 (window from +24.6 s and a last chance) restored
  Trap #2 but lost Light of Judgment #2 in one pull (16 -> 15). Light of Judgment #2 is the sheet's slot (30%), the
  Trap #2 Kerachole is not (the sheet has Panhaima and the Zoe shield there), so s11 is kept.
- s11b (s11 without the Wave Cannon shield): o20 9144 (+58), heal 131/158, but the Wave Cannon party-hit shield drops
  below the repo (2 -> 1 of 4 pulls). Kept the shield (the sheet: all mechanics require shields).
- Healing per segment flips between "ok" and "LOW" by 2-3 segments between variants with no trend (cooldown heals
  land in neighbouring segments); the cumulative check is the steadier one:
  repo: behind the real Sage at 0/27, 0/27, 8/158 segment ends (lowest ratio 1.11, 1.09, 0.74);
  s11:  behind at 0/27, 0/27, 5/158 (lowest 1.13, 1.14, 0.76); neighbouring pairs at least the real Sage's 19/26, 20/26,
  104/127 (repo 18/26, 15/26, 103/127).

## Final (s11 with comments cleaned up = variants/final = the repo files now; results identical to s11)

variants/orig is the original repo pair rebuilt (clear1 with its four window fixes reverted); it reproduces the
recorded baseline exactly. "Shields" = Sage shield up at the clear's 40 party-wide hits of 20%+ HP (Oct 20: 136).

| | clear, default movement | clear, step 0.5 / slide 900 | Oct 20 (31 pulls), default |
|---|---|---|---|
| potency / min: baseline -> final (real player) | 8782 -> 8742 (7977) | 9288 -> 9145 (7977) | 8822 -> 9086 (8505) |
| GCDs / min | 23.6 -> 23.5 (22.6) | 23.9 -> 23.9 | 24.7 -> 24.7 (22.0) |
| cast cancels | 85 -> 83 (0) | 54 -> 50 | 163 -> 188 (123) |
| sheet slots (Sage column) | 74 -> 98 / 102 (73) | 70 -> 98 / 102 | 293 -> 359 / 389 (281) |
| shields at 20%+ hits | 10 -> 39 / 40 (35) | 12 -> 39 / 40 | 117 -> 122 / 136 (101) |
| any Sage mitigation at 20%+ hits | 34 -> 40 / 40 (40) | 34 -> 40 | 133 -> 134 / 136 (123) |
| heal segments >= real Sage | 18 -> 21 / 27 | 18 -> 21 / 27 | 128 -> 127 / 158 |
| heal, cumulative behind the real Sage | 0 -> 0 / 27 (ratio >= 1.11 -> 1.12) | 0 -> 0 / 27 (1.09 -> 1.15) | 8 -> 5 / 158 |
| heal, neighbouring-segment pairs >= real | 18 -> 19 / 26 | 15 -> 20 / 26 | 103 -> 104 / 127 |
| heal segments with counterfactual 5+ points below logged | 14 -> 12 | 14 -> 11 | 47 -> 48 |
| lowest counterfactual HP | -29% -> -43% | -29% -> -22% | -33% -> -42% |
| rules' lowest MP | 6000 -> 5450 (real 3235 from the log's UpdateHp lines; brief: 3450) | 4950 -> 4950 | 4350 -> 4750 (user 900, possibly just after a raise) |
| Eukrasian Prognosis II | 27 -> 53 (77) | 27 -> 54 | |

Oct 20 per-raidwide coverage against the repo (cmp_cov.py): one drop, Double-trouble Trap #2 Kerachole 4 -> 2 of 15
(not a sheet assignment; see s11/s12). Every other raidwide's Kerachole / shield / any count is equal or higher.

Harness (from the Examples folder, `RuleHarness.dll <bin\Release> SAM_Simple.json SAM_Lv100_SlowMAL.json <the other 17>`):
Unit 77 passed, 0 failed; E2E all passed (165 checks); all 19 Example files compile. Output: results/harness.txt.

Diagnostics added (no effect on results): `bighits <log> <pull>` (hits on the Sage before each big hit, the casts
into it, the real Sage's shields and cooldowns around it); the trace line now shows Addersgall@timer, Addersting,
party average and lowest HP; TRACE_ALL=1 prints every press every 100 ms; PANH_DEBUG=1 prints the Panhaima model's
inputs per slot. Tools: ev.sh (the three runs + one-line summaries), cmp_rw.py (per-hit coverage differences between
two runs), cmp_cov.py (per-raidwide coverage drops), probe/ (log probes), lum/ (game-data check of Eukrasia).
