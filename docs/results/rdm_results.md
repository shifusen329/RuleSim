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
10-17 s), too short for the Acceleration buff to run out before a 5 s Veraero III precast (that needs 26 s), and while Grand Impact
Ready is up nothing can be hard-cast. So the option presses Acceleration with 19 s or less left, opens with its instant Veraero III at
the pull, then Grand Impact, then Manafication's combo (one GCD later than with the precast).

`run all rdm --seeds=5 --ab=optPrepullAcceleration=1`, same pulls and seeds:

| pulls | n | potency/min | Acceleration / pull | Grand Impact / pull |
|---|---|---|---|---|
| all | 894 | 16,242 -> 16,229 (-0.08%) | 4.89 -> 5.46 | 4.80 -> 5.41 |
| under 3:20 (Phase 1) | 445 | -0.14% | 3.32 -> 3.99 | 3.25 -> 3.97 |
| 3:20 - 9:00 | 428 | -0.18% | 6.21 -> 6.68 | 6.11 -> 6.59 |
| 9:00 or more | 21 | +1.24% | 11.11 -> 11.86 | 10.90 -> 11.51 |
| the user's | 476 | +0.28% | 4.79 -> 5.33 | 4.67 -> 5.26 |

First 30 s: 12,317 -> 12,263 potency a pull (-0.4%): the precast Veraero III and its Dualcast Verthunder III go, Grand Impact comes in.
414 pulls better, 464 worse. Left off by default.
