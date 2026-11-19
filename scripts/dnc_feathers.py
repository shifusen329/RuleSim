#!/usr/bin/env python3
"""The user's Dancer feathers against DNC_ST.json's keep rule (2026-11-18, user: "DNC_ST.json does not appear to hold the
feathers specified in the options").

  dnc_feathers.py <ACT log> [...] [--me=HEX,HEX] [--keep=3] [--window=30] [--list]

Reads the raw ACT lines. The user's gauge is line 31: data0 = job (0x26) | 0 | feathers << 16 | Esprit << 24. Fan Dance /
Fan Dance II come from lines 21/22, Technical Step presses from 21 (15998), and Technical Finish (status 1822) on the user from
26/30. Combat starts with line 260's in-game combat flag. Dancing Mad only (zone 0x553).

Technical Step's recast is 120 s from its press. As in the file, in Dancing Mad the second one waits until 113.5 s of combat
(optTech2At): techIn = max(recast left, 113.5 - combat time) from 60 s to 113.5 s.

Per Fan Dance, outside Technical Finish: the feathers just before it (the last gauge reading before the press) and techIn:
  - "window": techIn < --window. The rule spends only above --keep.
  - "outside": techIn >= --window. The rule spends all of them, by design.
Also the feathers held at each Technical Finish, and the feathers lost to the cap (a gain shown while already at 4).
"""
import collections
import re
import sys
from datetime import datetime

args = [a for a in sys.argv[1:] if not a.startswith('--')]
opt = dict(a[2:].split('=', 1) for a in sys.argv[1:] if a.startswith('--') and '=' in a)
me = {x.upper() for x in opt.get('me', '1001A4A8,10041112').split(',')}
keep = int(opt.get('keep', '3'))
window = float(opt.get('window', '30'))
listing = '--list' in sys.argv
FAN = {0x3E87: 'Fan Dance', 0x3E88: 'Fan Dance II'}
TECH_STEP = 0x3E7E
TECH_FINISH_ACTIONS = {16004, 16193, 16194, 16195, 16196}
TECH_FINISH_STATUS = 0x71E


def ts(s):
    return datetime.fromisoformat(s[:26] + s[27:]).timestamp() if len(s) > 27 else datetime.fromisoformat(s).timestamp()


uses = []          # (log, pull clock, feathers before, techIn, techUp, name, combatTime)
at_finish = collections.Counter()
gains = 0; combat_s = 0.0
for path in args:
    zone = None; start = None; in_combat = False; last_tech = -1e9; tech_up_until = 0.0; feathers = None; gauge_t = 0.0; job = 0
    pull = 0
    for line in open(path, encoding='utf-8', errors='replace'):
        f = line.split('|')
        if len(f) < 3:
            continue
        typ = f[0]
        if typ == '01':
            zone = f[2].upper(); continue
        if zone != '553':
            continue
        if typ == '260' and len(f) > 3:
            g = f[3] == '1'
            t = ts(f[1])
            if g and not in_combat:
                start = t; pull += 1
            if not g and in_combat and start is not None and job == 0x26:
                combat_s += t - start
            in_combat = g
            continue
        if typ == '31' and f[2].upper() in me:
            d0 = int(f[3], 16)
            job = d0 & 0xFF
            if job != 0x26:
                feathers = None
            if d0 & 0xFF == 0x26:
                nf = (d0 >> 16) & 0xFF
                if in_combat and feathers is not None and nf > feathers:
                    gains += nf - feathers
                feathers = nf; gauge_t = ts(f[1])
            continue
        if typ in ('21', '22') and f[2].upper() in me:
            aid = int(f[4], 16); t = ts(f[1])
            if aid == TECH_STEP:
                last_tech = t
            if aid in TECH_FINISH_ACTIONS and in_combat and feathers is not None:
                at_finish[feathers] += 1
            if aid in FAN and in_combat and start is not None and feathers is not None:
                ct = t - start
                cd = max(0.0, 120 - (t - last_tech))
                tech_in = max(cd, 113.5 - ct) if 60 < ct < 113.5 else cd
                uses.append((path.split('\\')[-1].split('/')[-1], pull, ct, feathers, tech_in, t < tech_up_until, FAN[aid]))
            continue
        if typ == '26' and len(f) > 8 and f[7].upper() in me and int(f[2], 16) == TECH_FINISH_STATUS:
            tech_up_until = ts(f[1]) + float(f[4]); continue
        if typ == '30' and len(f) > 8 and f[7].upper() in me and int(f[2], 16) == TECH_FINISH_STATUS:
            tech_up_until = ts(f[1]); continue

out = [u for u in uses if not u[5]]
win = [u for u in out if u[4] < window]
outside = [u for u in out if u[4] >= window]
print(f"{len(uses)} Fan Dances in Dancing Mad combat ({len(uses) - len(out)} under Technical Finish)")
print(f"  window (Technical Step within {window:g} s, the rule keeps {keep}): {len(win)}; feathers before them: "
      + ', '.join(f"{k}: {v}" for k, v in sorted(collections.Counter(u[3] for u in win).items())))
print(f"  outside the window (the rule spends all): {len(outside)}; feathers before them: "
      + ', '.join(f"{k}: {v}" for k, v in sorted(collections.Counter(u[3] for u in outside).items())))
bad = [u for u in win if u[3] <= keep]
print(f"  spent inside the window with {keep} or fewer: {len(bad)}")
print(f"feather gains: {gains} in {combat_s / 60:.0f} combat minutes, {gains / max(combat_s / 60, 1e-9):.2f} a minute "
      f"(3 feathers take about {3 / max(gains / max(combat_s, 1e-9), 1e-9):.0f} s)")
print(f"feathers at Technical Finish: " + ', '.join(f"{k}: {v}" for k, v in sorted(at_finish.items())))
if listing:
    for u in bad[:40]:
        print(f"    {u[0]} pull {u[1]} {u[2]:6.1f}s {u[6]:12} feathers {u[3]} techIn {u[4]:5.1f}")
