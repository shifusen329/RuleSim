# Dancer Custom Rules (2026-10-23)

Repo files: `WrathCombo/CustomRules/Examples/DNC_ST.json`, `DNC_AoE.json`, `DMU_Mit_DNC.json`; presets `DNC_ST_CustomRules` 4200 /
`DNC_AoE_CustomRules` 4230; `partner` target kind + `partnerNonOptimal` value (RuleSet.cs, RuleBuiltins.cs, DNC_Helper.cs
`CurrentPartnerNonOptimal` made internal). Harness DNC block in `scratchpad/harness/Program.cs` (Unit 77, E2E all passed).

## Data (raid-replay scan)
- `scratchpad/dnc/DncScan` (C#, RaidReplay.Core): `dotnet DncScan/out/DncScan.dll [--file=sub] [--player=HEX] [--out=DIR]` ->
  `scan/{pulls,actions,statuses,casts,enemies,phases,deaths,party_gcd}.csv`. 1,337 DMU pulls with a Dancer (Sep 20 - Oct 22),
  35 Dancers; 1,111 ended in P1, 218 in P2, 8 in P3. `clear/` = the AnoMech full clear with the MCH (1005A0DF) as D3.
- `report.py` (ranking, procs dropped, burst contents), `gaps.py`, `p1end.py`, `opener.py`, `trans.py`: `report.md`.
- Game data: `dnc_gamedata.txt` (Lumina). Lv100 potencies: Cascade 220, Fountain 280 combo/120, RC 280, FF 340, Saber 540,
  DotD 1000, Starfall 600 (crit DH), Tillana 600, LD 540, FM 850, SF2 850, TF4 1300, FD1 180, FD2 100, FD3 220, FD4 460.
  Shield Samba 15%, 90 s (trait), 15 s. Improvised Finish 5% barrier at 0 stacks, 30 s, 8 y. Finishes/Tillana/FM: 15 y self.
- Timing (logs): SS/TS GCD 1.5 s, steps 1.0, finishes 1.5. P1 Kefka untargetable 197.3-198.7 s, P2 Kefka 10.3 s later.
- Esprit (user's own gauge, Sep 29 / Oct 20, ACT 31: d0 byte2 feathers, byte3 Esprit, d1 steps, d2 step index): Cascade/Fountain
  +5, RC/FF +10 and a feather ~45%, Tillana +50; party weaponskill/spell under the Dancer's Esprit ~0.17 per cast (guide ~0.2).

## Replay (`scratchpad/dncsim`, copy of rulesim + DncSim.cs/DncCommands.cs)
`dotnet out/DncSim.dll run all dnc [--seeds=5] [--set=a=b] [--rules=DIR] [--rate=0.18] [--detail] [--fires] [--me=HEX]`;
`timeline <log> <pull> dnc --me=HEX [--from --to] [--trace]`; `sweep.sh name "set" ...`; `bydetail.py` (by phase reached).
Score: personal potency x (SF, TF, Devilment 1.155, others' raid buffs); contributed = party damage in the sim's Tech window x5%,
partner damage in Devilment x15.5% / Standard Finish x5% (real buffs taken out). Personal -> damage with the Dancer's own ratio.

Final (5 seeds, 897 Dancer-pulls >= 60 s): rules 14,485 pot/min vs real 13,317 (+8.8%), total +6.9%. Best real Dancer
Kyomi Alayna 14,187 -> rules 14,657 on her pulls (+3.3%); every Dancer +3.3% to +21.6%. GCD/min sim 27.68 vs real 26.05 (real
idle: top 3-6%, rest 8-12%). Procs expired /10 min: sim 0.1, real 7.6. Shield Samba covers 20.2% of raidwide damage (real 19.6%).
Plan layer costs 0.05% potency.

Sweeps (pot/min, all pulls): optEspritHold 80 -> 50: +0.55% at party rates 0.12/0.18/0.25 (burst overcaps when pooled);
feathers keep 3 always 14,310 / keep 0 14,494 / window 30 s 14,477 (P3 pulls favour keeping: 14,311 vs 14,198, 8 pulls only);
optStdHoldBeforeTech 0/3/6 equal, 10 worse; optFlourishHoldBeforeTech no effect; optTechFinishDelay 1 s = same total (more
party contribution, less personal), 2 s worse; Last Dance as filler after procs > before procs > not as filler.

Plan: Samba s1 moved to Revolting Ruin III #1 +12.4..14 s (23-24.5 s) so s2 (DTT 1:58.5) is back before the 2-minute Tech
dance (~1:55; nothing but steps while dancing): before that change the replay's s2 slid to 2:00.5 and Samba coverage was 12.5%.
Not modelled: LoS/position for Samba/Improvisation (8 y), barrier eaten by earlier damage, P4/P5 (no Dancer data; windows from
the clear only; s8 keyed on Ultima Upsurge #2 +29.5..36 s, falls back to Ultima Repeater #1's cast).
