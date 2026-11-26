#!/usr/bin/env python3
"""Why a Gunbreaker's No Mercy drifts off the party's raid buffs in Dancing Mad (2026-11-14).

  dmu_gnb_nomercy.py <ACT log or folder> [...] [--since 20261023] [--me 10041112,1001A4A8] [--detail] [--until 198]
                     [--job PLD]

For each Dancing Mad pull where you played Gunbreaker: each No Mercy, when it was back (the last one + 60 s; the first
from the pull), how late it went, and every ability (oGCD) pressed in between: the slots that went first. The totals count
which abilities took those slots, and how late each No Mercy was against the party's raid buffs (the first raid buff
within 20 s before or after it; Divination, Battle Litany, Brotherhood, Arcane Circle, Searing Light, Starry Muse,
Embolden, Technical Finish, Battle Voice, Radiant Finale).

--detail (2026-11-15) prints each No Mercy pressed before --until seconds (default all; 198 = Phase 1): its time, when it
was back, how late, the drift from the opener's 60 s grid (opener + 60 s, + 120 s ...), the first raid buff near it,
and everything you pressed from 3 s before it was back to it (GCDs in lower case), with Bloodfest's own readiness.

--job PLD (2026-11-26, user: "Fix the fight or flight timing so it aligns with the party's 2minute raid buff windows"): the
same for a Paladin's Fight or Flight (also 60 s).
"""
import collections
import datetime as dt
import os
import re
import sys

BUFFS = {'Divination', 'Battle Litany', 'Brotherhood', 'Arcane Circle', 'Searing Light', 'Starry Muse', 'Embolden',
         'Technical Finish', 'Battle Voice', 'Radiant Finale'}
GCDS = {'Keen Edge', 'Brutal Shell', 'Solid Barrel', 'Burst Strike', 'Gnashing Fang', 'Savage Claw', 'Wicked Talon',
        'Double Down', 'Sonic Break', 'Reign of Beasts', 'Noble Blood', 'Lion Heart', 'Lightning Shot', 'Demon Slice',
        'Demon Slaughter', 'Fated Circle', 'attack',
        'Fast Blade', 'Riot Blade', 'Royal Authority', 'Atonement', 'Supplication', 'Sepulchre', 'Holy Spirit', 'Confiteor',
        'Blade of Faith', 'Blade of Truth', 'Blade of Valor', 'Goring Blade', 'Shield Lob', 'Total Eclipse', 'Prominence',
        'Holy Circle'}
JOBS = {'GNB': (0x25, 'No Mercy', 'Bloodfest'), 'PLD': (0x13, 'Fight or Flight', 'Imperator')}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def main():
    args = sys.argv[1:]
    me, since, paths, detail, until, jobname = {'10041112', '1001A4A8'}, None, [], False, 1e9, 'GNB'
    i = 0
    while i < len(args):
        if args[i] == '--me':
            me = set(args[i + 1].split(','))
            i += 2
        elif args[i] == '--since':
            since = args[i + 1]
            i += 2
        elif args[i] == '--job':
            jobname = args[i + 1].upper()
            i += 2
        elif args[i] == '--detail':
            detail = True
            i += 1
        elif args[i] == '--until':
            until = float(args[i + 1])
            i += 2
        else:
            paths.append(args[i])
            i += 1
    JOB, BURST, PAIR = JOBS[jobname]
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    took = collections.Counter()
    late_ready, late_buff = collections.defaultdict(list), collections.defaultdict(list)
    for log in logs:
        zone = None
        job = None
        T = None
        ev = []
        for raw in open(log, 'rb'):
            p = raw[:4]
            if p.startswith(b'01|'):
                zone = raw.split(b'|')[2].decode()
                continue
            if p.startswith(b'03|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[2] in me:
                    job = int(l[4], 16)
                continue
            if zone != '553' or job != JOB:
                continue
            if p == b'260|':
                l = raw.decode().split('|')
                if since and l[1][:10].replace('-', '') < since:
                    continue
                if l[3] == '1' and T is None:
                    T, ev = ts(l[1]), []
                elif l[3] == '0' and T is not None:
                    nms = [r for r, k, n in ev if k == 'me' and n == BURST]
                    buffs = [r for r, k, n in ev if k == 'buff']
                    if detail and nms:
                        bfs = [r for r, k, n in ev if k == 'me' and n == PAIR]
                        print(f'== pull {T:%Y-%m-%d %H:%M:%S}')
                        prev = None
                        for n_i, r in enumerate(nms):
                            if r > until:
                                break
                            ready = prev + 60 if prev is not None else 0
                            near = [b for b in buffs if -20 <= r - b <= 20]
                            bf = [b for b in bfs if b <= r and r - b < 15]
                            bfr = [b + 60 for b in bfs if b < ready - 30]
                            print(f'   {BURST} {n_i + 1} at {r:6.1f} s: back at {ready:6.1f}, {r - ready:+.1f} late; drift from the grid '
                                  f'{r - nms[0] - 60 * n_i:+.1f}; raid buff {("%+.1f" % (r - min(near))) if near else "none near"}; '
                                  f'{PAIR} {("%+.1f before" % (r - bf[-1])) if bf else "-"}'
                                  f'{(", back at %.1f" % bfr[-1]) if bfr and n_i else ""}')
                            seq = [(x, nme) for x, k, nme in ev if k == 'me' and ready - 3 <= x <= r + 0.1]
                            print('      ' + ' '.join(f'{x:.1f} {nme.lower() if nme in GCDS else nme}' for x, nme in seq))
                            prev = r
                    prev = None
                    for n_i, r in enumerate(nms):
                        ready = prev + 60 if prev is not None else 0
                        slot = 'opener' if n_i == 0 else f'#{n_i + 1}'
                        late_ready[slot].append(r - ready)
                        near = [b for b in buffs if -20 <= r - b <= 20]
                        if near:
                            late_buff[slot].append(r - min(near))
                        if prev is not None:
                            for x, k, nme in ev:
                                if k == 'me' and ready <= x < r and nme not in GCDS and nme != BURST:
                                    took[nme] += 1
                        prev = r
                    T = None
                continue
            if T is None:
                continue
            if p.startswith(b'26|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[3] in BUFFS and l[7] in me:
                    ev.append(((ts(l[1]) - T).total_seconds(), 'buff', l[3]))
            elif p.startswith(b'21|') or p.startswith(b'22|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[2] in me:
                    r = (ts(l[1]) - T).total_seconds()
                    if not ev or ev[-1][1:] != ('me', l[5]) or r - ev[-1][0] > 0.3:
                        ev.append((r, 'me', l[5]))
    for name, d in (('after it was back', late_ready), ('after the first raid buff near it', late_buff)):
        print(f'{BURST}, seconds {name}:')
        for slot in sorted(d, key=lambda s: (s != 'opener', len(s), s)):
            v = sorted(d[slot])
            if len(v) >= 3:
                print(f'   {slot:7} n={len(v):3}  median {v[len(v) // 2]:+.1f}  p10 {v[len(v) // 10]:+.1f}  p90 {v[9 * len(v) // 10]:+.1f}')
    print(f'abilities pressed while {BURST} was back and waiting (all slots):')
    for k, v in took.most_common(15):
        print(f'   {v:4}  {k}')


if __name__ == '__main__':
    main()
