#!/usr/bin/env python3
"""Who holds Dancing Mad's Phase 3 Accretions, which one comes off first and whose heal does it (2026-11-17).

  dmu_accretion_order.py <ACT log or folder> [...] [--since 20261020] [--me 10041112,1001A4A8] [--list]

Per Earthquake (dmu_earthquake.py's scan): each Accretion holder's role (healer, DPS, tank) and line (First / Second in
Line), the order they came off (or ran out), the job of the last heal on the holder within 1.5 s of the removal (the holder's
own heal marked "self"; "?" = no direct heal within 1.5 s, i.e. a heal-over-time tick, which ACT logs
save without its source), and whether the party reached Black Hole's cast with nobody dead. The summary answers:
  - Is it always one healer and one DPS? Is the healer always First in Line (so line order = role order)?
  - Which came off first, by role and by line, in pulls that reached Black Hole clean and in the rest?
  - Whose heals removed them: healers, the holder's own, other DPS, tanks?
Folders are read for Network_*.log; converted FFLogs logs (Network_39999_*) work too.
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dmu_black_hole import effects, LINES  # noqa: E402
from dmu_earthquake import scan  # noqa: E402

HEALERS = {'WHM', 'SCH', 'AST', 'SGE'}
TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}


def role(job):
    return 'healer' if job in HEALERS else 'tank' if job in TANKS else 'DPS' if job and job != '?' else '?'


def one(T, jobs, rows):
    line, acc_on, off, names = {}, {}, {}, {}
    last = {}      # holder id -> (r, source id)
    bh, dead, end = None, 0, None
    for r, l in rows:
        ty = l[0]
        if ty == 'END':
            end = r
            break
        if ty == '20' and len(l) > 5 and l[5] == 'Black Hole' and bh is None:
            bh = r
        elif ty == '26' and l[7][:1] == '1':
            names[l[7]] = l[8]
            if l[3] in LINES:
                line[l[7]] = LINES[l[3]]
            elif l[3] == 'Accretion' and l[7] not in acc_on:
                acc_on[l[7]] = r
        elif ty == '30' and l[7][:1] == '1' and l[3] == 'Accretion' and l[7] in acc_on and l[7] not in off:
            h = last.get(l[7])
            src = h[1] if h and r - h[0] < 1.5 else None
            off[l[7]] = (r, src, r - acc_on[l[7]] >= 13.8, h[2] if src else '')
        elif ty in ('21', '22') and l[6][:1] == '1':
            _, healed = effects(l)
            if healed:
                last[l[6]] = (r, l[2], l[5])
        elif ty == '25' and l[2][:1] == '1' and (bh is None or r < bh):
            dead += 1
    holders = []
    for i, t in acc_on.items():
        r, src, expired, act = off.get(i, (None, None, False, ''))
        by = 'expired' if expired else '?' if src is None else ('self' if src == i else role(jobs.get(src)) + ' ' + jobs.get(src, '?')) + f' ({act})'
        holders.append({'id': i, 'name': names.get(i, i), 'job': jobs.get(i, '?'), 'role': role(jobs.get(i)),
                        'line': line.get(i, 0), 'on': t, 'off': r, 'by': by, 'expired': expired})
    holders.sort(key=lambda h: (h['off'] is None, h['off'] or 0))
    return {'T': T, 'holders': holders, 'clean': bh is not None and dead == 0, 'bh': bh, 'dead': dead}


def main():
    args = sys.argv[1:]
    me = {'10041112', '1001A4A8'}
    since, show, paths = None, False, []
    i = 0
    while i < len(args):
        if args[i] == '--me':
            me = set(args[i + 1].split(','))
            i += 2
        elif args[i] == '--since':
            since = args[i + 1]
            i += 2
        elif args[i] == '--list':
            show = True
            i += 1
        else:
            paths.append(args[i])
            i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    seen, res = set(), []
    for p in logs:
        for path, T, jobs, rows in scan(p, me, since):
            if T in seen:
                continue
            seen.add(T)
            a = one(T, jobs, rows)
            a['src'] = os.path.basename(path)
            a['you'] = next((jobs[x] for x in me if x in jobs), None)
            res.append(a)

    pairs = collections.Counter()
    healer_line = collections.Counter()
    first = {True: collections.Counter(), False: collections.Counter()}
    by = {True: collections.Counter(), False: collections.Counter()}
    n = {True: 0, False: 0}
    for a in res:
        hs = a['holders']
        pairs['+'.join(sorted(h['role'] for h in hs))] += 1
        for h in hs:
            if h['role'] == 'healer':
                healer_line[h['line']] += 1
        done = [h for h in hs if h['off'] is not None]
        c = a['clean']
        if len(hs) == 2:
            n[c] += 1
        if len(done) >= 1 and len(hs) == 2:
            f = done[0]
            first[c][f"{f['role']} first ({'First' if f['line'] == 1 else 'Second' if f['line'] == 2 else '?'} in Line)"
                     + (' [ran out]' if f['expired'] else '')] += 1
        for h in done:
            by[c][f"{h['role']}'s Accretion: {h['by'].split(' ')[0]}"] += 1
        if show:
            outcome = 'clean to Black Hole' if c else 'Black Hole not reached' if a['bh'] is None else f"Black Hole reached, {a['dead']} dead"
            print(f"{a['T']:%Y-%m-%d %H:%M:%S} {a['src']:28} you {a['you'] or '-':4} {outcome}")
            for h in hs:
                o = f"off +{h['off']:.1f} by {h['by']}" if h['off'] is not None else 'never off (pull ended)'
                print(f"    {h['role']:6} {h['job']:3} line {h['line']}  {h['name']:22} on +{h['on']:.1f}, {o}")
    print(f"Earthquakes: {len(res)} ({n[True]} clean to Black Hole with two Accretions, {n[False]} not)")
    print(f"holder pairs: {dict(pairs)}")
    print(f"healer holder's line: {dict(sorted(healer_line.items()))}  (1 = First in Line)")
    for c in (True, False):
        print(f"{'clean' if c else 'not clean'}: first off: {dict(first[c])}")
        print(f"{'clean' if c else 'not clean'}: removed by: {dict(by[c])}")


if __name__ == '__main__':
    main()
