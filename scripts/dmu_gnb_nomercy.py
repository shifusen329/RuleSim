#!/usr/bin/env python3
"""Why a Gunbreaker's No Mercy drifts off the party's raid buffs in Dancing Mad (2026-11-14).

  dmu_gnb_nomercy.py <ACT log or folder> [...] [--since 20261023] [--me 10041112,1001A4A8]

For each Dancing Mad pull where you played Gunbreaker: each No Mercy, when it was back (the last one + 60 s; the first
from the pull), how late it went, and every ability (oGCD) pressed in between: the slots that went first. The totals count
which abilities took those slots, and how late each No Mercy was against the party's raid buffs (the first raid buff
within 20 s before or after it; Divination, Battle Litany, Brotherhood, Arcane Circle, Searing Light, Starry Muse,
Embolden, Technical Finish, Battle Voice, Radiant Finale).
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
        'Demon Slaughter', 'Fated Circle', 'attack'}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def main():
    args = sys.argv[1:]
    me, since, paths = {'10041112', '1001A4A8'}, None, []
    i = 0
    while i < len(args):
        if args[i] == '--me':
            me = set(args[i + 1].split(','))
            i += 2
        elif args[i] == '--since':
            since = args[i + 1]
            i += 2
        else:
            paths.append(args[i])
            i += 1
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
            if zone != '553' or job != 0x25:
                continue
            if p == b'260|':
                l = raw.decode().split('|')
                if since and l[1][:10].replace('-', '') < since:
                    continue
                if l[3] == '1' and T is None:
                    T, ev = ts(l[1]), []
                elif l[3] == '0' and T is not None:
                    nms = [r for r, k, n in ev if k == 'me' and n == 'No Mercy']
                    buffs = [r for r, k, n in ev if k == 'buff']
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
                                if k == 'me' and ready <= x < r and nme not in GCDS and nme != 'No Mercy':
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
        print(f'No Mercy, seconds {name}:')
        for slot in sorted(d, key=lambda s: (s != 'opener', len(s), s)):
            v = sorted(d[slot])
            if len(v) >= 3:
                print(f'   {slot:7} n={len(v):3}  median {v[len(v) // 2]:+.1f}  p10 {v[len(v) // 10]:+.1f}  p90 {v[9 * len(v) // 10]:+.1f}')
    print('abilities pressed while No Mercy was back and waiting (all slots):')
    for k, v in took.most_common(15):
        print(f'   {v:4}  {k}')


if __name__ == '__main__':
    main()
