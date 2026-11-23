# Gunbreaker replay results

## optSpendSpare: Burst Strike / Gnashing Fang on cartridges the next No Mercy won't need (2026-11-23)

User: "Use burst strike > Continuation to increase DPS if not holding for a No Mercy Window", then "Ensure that it's used when
the Gnashing Fang combo is either on cooldown or being held for No Mercy", then "run it through the sim and compare to the
previous ruleset".

The model is `GnbSim.cs`, with level-100 game data from GameDump:
- **Cartridges:** Solid Barrel +1 (wasted at the cap); Bloodfest +3 with the cap at 6 for 30 s (60 s recast; the extra ones
  vanish when it ends); Burst Strike and Gnashing Fang -1; Double Down -2.
- **Combos:** the Gnashing Fang and Reign combos as the gauge's combo step; the Keen Edge combo isn't broken by cartridge
  weaponskills; a weaponskill ends any Continuation proc.
- **Score:** potency x No Mercy (+20%, DoTs snapshot it) x the logged party's raid buffs.
- **Not modelled:** mitigation, auto-attacks.

The model runs about 13% over the logged players. It assumes perfect execution and no mitigation weaves. The A/B is paired.

`run all gnb --ab=optSpendSpare=0`: 1,635 Gunbreaker pulls (every Gunbreaker in the user's Dancing Mad logs, 5,874 active
minutes). The option off reproduces the previous GNB_ST.json exactly: the HEAD file and `--set=optSpendSpare=0` gave identical
potency on all 144 finished pulls since Nov 15.

| pulls | new (on) | previous (off) | new vs previous |
|---|---|---|---|
| all | 19,148 | 19,117 | **+0.16%** (better in 710, worse in 254) |
| Phase 1 only | 19,148 | 19,118 | +0.15% |
| 3:20-9:00 | 19,288 | 19,248 | +0.21% |
| 9:00+ | 18,593 | 18,594 | -0.01% |
| the user (191) | 18,958 | 18,936 | +0.12% |

Cartridges per 10 minutes:

| | new | previous |
|---|---|---|
| spent in No Mercy | 31.3 | 31.4 |
| spent outside No Mercy | 29.2 | 28.1 |
| wasted to the cap | 0 | 0 |
| vanished with Bloodfest | 0.0 | 0.3 |
| held at a death or the pull's end | 5.9 | 7.2 |
| at 2+ outside No Mercy (s a minute) | 18.9 | 28.0 |
| Burst Strike | 17.9 | 16.8 |
| Gnashing Fang | 20.3 | 20.3 |

- **The windows are unchanged:** 31.3 against 31.4 cartridges spent in No Mercy. The gain is cartridges spent instead of held
  (to a death or a wipe, or stranded by Bloodfest).
- **The spare Gnashing Fang rule almost never fires:** both charges are nearly always needed for the next window.
