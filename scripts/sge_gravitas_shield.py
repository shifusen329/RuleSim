#!/usr/bin/env python3
"""Did the user's Sage shield the first Gravitas? (2026-11-19: pull #4 that day died to it by 68 HP, food gone, no shield.)

  sge_gravitas_shield.py <ACT log> [...] [--me=HEX,HEX]

Per Dancing Mad pull with the user on Sage that reached the second Graven Image: the user's Eukrasia / Eukrasian Prognosis II /
Eukrasian Diagnosis presses (and the target) from the cast's start to Gravitas, Gravitas's time from the cast, and the user's
HP and max HP at Gravitas. The plan (DMU_Mit_SGE.json "gravitas1") shields from +3 to +8.5 s; Gravitas lands at about +10.3.
Raw ACT lines: 260 (combat), 20 (Kefka's Graven Image), 21/22 (the user's actions; the Gravitas hits on the user).
"""
import sys
from datetime import datetime

args = [a for a in sys.argv[1:] if not a.startswith('--')]
opt = dict(a[2:].split('=', 1) for a in sys.argv[1:] if a.startswith('--') and '=' in a)
me = {x.upper() for x in opt.get('me', '1001A4A8,10041112').split(',')}
WATCH = {'Eukrasia', 'Eukrasian Prognosis II', 'Eukrasian Diagnosis', 'Zoe', 'Kerachole', 'Holos', 'Panhaima', 'Macrocosmos'}


def ts(s):
    return datetime.fromisoformat(s[:26] + s[27:]).timestamp()


for path in args:
    zone = None; in_combat = False; start = None; gi = []; job = None; presses = []; grav = None; pull = 0
    rows = []

    def flush():
        if start is not None and len(gi) >= 2 and job == '28':
            g2 = gi[1]
            ps = [(t - g2, n, tg) for t, n, tg in presses if g2 <= t < g2 + 11]
            gv = f"{grav[0] - g2:+.1f} HP {grav[1]}/{grav[2]}" if grav else '-'
            rows.append((pull, start, ps, gv))

    for line in open(path, encoding='utf-8', errors='replace'):
        f = line.split('|')
        if len(f) < 3:
            continue
        if f[0] == '01':
            zone = f[2].upper(); continue
        if zone != '553':
            continue
        if f[0] == '03' and f[2].upper() in me and len(f) > 4:
            job = f[4].upper(); continue
        if f[0] == '260' and len(f) > 3:
            g = f[3] == '1'
            if g and not in_combat:
                flush()
                start = ts(f[1]); gi = []; presses = []; grav = None; pull += 1
            in_combat = g
            continue
        if not in_combat:
            continue
        if f[0] == '20' and f[5] == 'Graven Image' and not f[2].startswith('10'):
            t = ts(f[1])
            if not gi or t - gi[-1] > 5:
                gi.append(t)
            continue
        if f[0] in ('21', '22'):
            if f[2].upper() in me and f[5] in WATCH:
                presses.append((ts(f[1]), f[5], f[7]))
            elif f[5] == 'Gravitas' and f[6].upper() in me and grav is None and len(gi) == 2:
                grav = (ts(f[1]), f[24], f[25])
    flush()
    name = path.replace('\\', '/').split('/')[-1]
    for pull, st, ps, gv in rows:
        when = datetime.fromtimestamp(st).strftime('%H:%M')
        eprog = any(n == 'Eukrasian Prognosis II' and 3 <= t < 10.3 for t, n, _ in ps)
        print(f"{name} pull {pull:3} {when}  shield {'YES' if eprog else 'no '}  Gravitas {gv:24}  " +
              ', '.join(f"{t:+.1f} {n}{' -> ' + tg if n == 'Eukrasian Diagnosis' else ''}" for t, n, tg in ps))
