#!/usr/bin/env python3
"""Every Phase 3 Earthquake (Chaos's C572, Dancing Mad) in ACT logs: how the Accretions were healed off and what the earth
crystal's pulses and the bosses' auto-attacks did, to set the healer plans' thresholds (2026-11-11).

  dmu_earthquake.py <ACT log or folder> [...] [--me 10041112,1001A4A8] [--since 20261024] [--list]

Per Earthquake: the two Accretion holders' seats (First / Second in Line), when each came off (seconds after the cast),
whose heal did it and the gap; every pulse (what set it off, the largest hit as % of max HP, inside the last pulse's Earth
Resistance Down II or not); tank auto-attacks (Exdeath C250 / Chaos C252) as % of max HP; and whether the party reached
Black Hole's cast alive. The totals give the pulse and auto percentiles. --list prints each Earthquake.
"""
import datetime as dt
import mmap
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dmu_black_hole import JOBS, SUPPORT, LINES, effects, ts, back, seek_time  # noqa: E402

TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}

CAST = b'|C572|Earthquake|'


def scan(path, me, since):
    out = []
    with open(path, 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        p = 0
        while True:
            p = mm.find(CAST, p + 1)
            if p < 0:
                break
            s = mm.rfind(b'\n', 0, p) + 1
            l = mm[s:mm.find(b'\n', p)].decode('utf-8', 'replace').split('|')
            if l[0] != '20':
                continue
            T = ts(l[1])
            if since and T.strftime('%Y%m%d') < since:
                continue
            z, zp = back(mm, b'\n01|', s, 0, lambda x: True)
            if not z or z[2] != '553':
                continue
            jobs = {}
            q = zp
            while True:
                q = mm.find(b'\n03|', q + 1, s)
                if q < 0:
                    break
                c = mm[q + 1:mm.find(b'\n', q + 1)].decode('utf-8', 'replace').split('|')
                if len(c) > 4 and c[2][:1] == '1':
                    jobs[c[2]] = JOBS.get(int(c[4], 16), c[4])
            rows = []
            o = s
            while o < len(mm):
                e = mm.find(b'\n', o)
                e = len(mm) if e < 0 else e
                raw = mm[o:e]
                o = e + 1
                if raw[:3] not in (b'20|', b'21|', b'22|', b'25|', b'26|', b'30|', b'260', b'38|'):
                    continue
                x = raw.decode('utf-8', 'replace').rstrip('\r').split('|')
                try:
                    r = (ts(x[1]) - T).total_seconds()
                except (IndexError, ValueError):
                    continue
                if r > 60:
                    break
                if x[0] == '260' and x[2] == '0' and r > 0:
                    rows.append((r, ['END']))
                    break
                rows.append((r, x))
            out.append((path, T, jobs, rows))
        mm.close()
    return out


def analyse(T, jobs, rows, me):
    line, acc, names, maxhp, hp = {}, {}, {}, {}, {}
    lastheal = {}
    pulses = []      # (r, trigger, max %, in vuln)
    autos = []       # % of max HP
    offs = []        # (r, id, healer job)
    trig = []        # (r, text)
    vuln_until = -99
    bh = None
    end = None
    dead_before_bh = 0
    group = None
    acc_now = set()
    for r, l in rows:
        ty = l[0]
        if ty == 'END':
            end = r
            break
        if ty == '38' and l[2][:1] == '1':
            try:
                hp[l[2]] = int(l[5])
                maxhp[l[2]] = int(l[6])
            except ValueError:
                pass
        elif ty == '20' and l[5] == 'Black Hole' and bh is None:
            bh = r
        elif ty == '26' and l[7][:1] == '1':
            names[l[7]] = l[8]
            if l[3] in LINES:
                line[l[7]] = LINES[l[3]]
            elif l[3] == 'Accretion':
                acc[l[7]] = r
                acc_now.add(l[7])
            elif l[3] == 'Earth Resistance Down II':
                vuln_until = max(vuln_until, r + float(l[4]))
        elif ty == '30' and l[7][:1] == '1' and l[3] in ('Accretion', 'Primordial Crust'):
            h = lastheal.get(l[7])
            if l[3] == 'Accretion':
                tanks = [100 * hp[i] / maxhp[i] for i in hp if jobs.get(i) in TANKS and maxhp.get(i)]
                low = sum(1 for i in hp if i not in acc_now and i != l[7] and maxhp.get(i) and hp[i] > 0 and 100 * hp[i] / maxhp[i] < 45)
                offs.append((r, l[7], jobs.get(h[1], '?') if h and r - h[0] < 1.5 else '?', min(tanks, default=100), low))
                acc_now.discard(l[7])
            trig.append((r, f"{'Accretion' if l[3] == 'Accretion' else 'Crust'} {l[8]}"))
        elif ty in ('21', '22') and l[6][:1] == '1':
            d, hl = effects(l)
            if hl:
                lastheal[l[6]] = (r, l[2])
            try:
                mx = int(l[25]) if l[25] else maxhp.get(l[6], 0)
            except ValueError:
                mx = maxhp.get(l[6], 0)
            if l[2][:1] == '4' and l[4] in ('C250', 'C252') and d and mx:
                autos.append(100 * d / mx)
            if l[5] == 'Earthquake' and l[4] == 'BAFA' and d and mx:
                if group is None or r - group[0] > 0.3:
                    t = [x for x in trig if 0.5 < r - x[0] < 1.6]
                    group = [r, t[-1][1] if t else '?', 0.0, r < vuln_until - 0.05]
                    pulses.append(group)
                group[2] = max(group[2], 100 * d / mx)
        elif ty == '25' and l[2][:1] == '1' and (bh is None or r < bh):
            dead_before_bh += 1
    seat = lambda i: f"{'ACC' if i in acc else 'SUP' if jobs.get(i) in SUPPORT else 'DPS'}{line.get(i, '?')}"
    chain = lambda r0: any(x[1].startswith('Crust') and 0 < x[0] - r0 < 3.2 for x in trig)
    return {'chain': chain, 'T': T, 'acc': acc, 'offs': offs, 'pulses': pulses, 'autos': autos, 'bh': bh, 'end': end,
            'dead': dead_before_bh, 'seat': seat, 'names': names, 'jobs': jobs}


def pct(xs, q):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(q * len(xs)))] if xs else 0


def main():
    args = sys.argv[1:]
    me = {'10041112', '1001A4A8'}
    since = None
    show = False
    paths = []
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
    res = []
    for p in logs:
        for path, T, jobs, rows in scan(p, me, since):
            res.append(analyse(T, jobs, rows, me))
    first_pulse, second_pulse, crust_pulse, autos = [], [], [], []
    order = {'ACC1 first': 0, 'ACC2 first': 0}
    gaps = []
    at_off = []    # (lowest tank %, others under 45%, a Crust broke within 3.2 s)
    for k, a in enumerate(res):
        acc_pulses = [p for p in a['pulses'] if p[1].startswith('Accretion') and not p[3]]
        if len(acc_pulses) >= 1:
            first_pulse.append(acc_pulses[0][2])
        if len(acc_pulses) >= 2:
            second_pulse.append(acc_pulses[1][2])
        crust_pulse += [p[2] for p in a['pulses'] if p[1].startswith('Crust') and not p[3]]
        autos += a['autos']
        offs = a['offs']
        at_off += [(o[3], o[4], a['chain'](o[0])) for o in offs]
        if len(offs) >= 2:
            order['ACC1 first' if a['seat'](offs[0][1]) == 'ACC1' else 'ACC2 first'] += 1
            gaps.append(offs[1][0] - offs[0][0])
        if show:
            reached = a['bh'] is not None and a['dead'] == 0
            you = next((a['jobs'][i] for i in me if i in a['jobs']), '?')
            print(f"#{k} {a['T']:%Y-%m-%d %H:%M}  you {you}  " + ', '.join(
                f"{a['seat'](i)} {a['names'].get(i, i)} ({a['jobs'].get(i, '?')}) on at +{t:.1f}" for i, t in a['acc'].items()))
            for r, i, j, tm, low in offs:
                print(f"     +{r:5.1f}  Accretion off: {a['seat'](i)} {a['names'].get(i, i)} (last heal by a {j}); lowest tank {tm:.0f}%, "
                      f"{low} others under 45%{'; a Crust broke within 3.2 s' if a['chain'](r) else ''}")
            for r, t, m, v in a['pulses']:
                print(f"     +{r:5.1f}  pulse from {t:32} max {m:5.1f}% of max HP{'   INSIDE the last pulse vulnerability' if v else ''}")
            print(f"     autos max {max(a['autos'], default=0):.0f}%, Black Hole {'reached with nobody dead' if reached else 'not reached' if a['bh'] is None else 'reached, ' + str(a['dead']) + ' dead'}")
    print(f"Earthquakes: {len(res)}; Accretions healed off in order: {order}; gap between them median {pct(gaps, 0.5):.1f} s")
    print(f"pulse max % of max HP (not inside a vulnerability): first Accretion's p50 {pct(first_pulse, .5):.0f} p90 {pct(first_pulse, .9):.0f} "
          f"max {max(first_pulse, default=0):.0f} (n {len(first_pulse)}); second Accretion's p50 {pct(second_pulse, .5):.0f} "
          f"p90 {pct(second_pulse, .9):.0f} max {max(second_pulse, default=0):.0f} (n {len(second_pulse)}); after a Crust p50 "
          f"{pct(crust_pulse, .5):.0f} max {max(crust_pulse, default=0):.0f} (n {len(crust_pulse)})")
    for lo, hi in ((0, 30), (30, 45), (45, 60), (60, 101)):
        g = [x for x in at_off if lo <= x[0] < hi]
        print(f"Accretions off with the lowest tank at {lo}-{hi}%: {len(g)}, a Crust broke within 3.2 s after {sum(1 for x in g if x[2])}")
    for n in (0, 1, 2):
        g = [x for x in at_off if (x[1] == n if n < 2 else x[1] >= 2)]
        print(f"Accretions off with {n}{'+' if n == 2 else ''} others under 45%: {len(g)}, a Crust broke within 3.2 s after {sum(1 for x in g if x[2])}")
    print(f"tank autos % of max HP: p50 {pct(autos, .5):.0f} p90 {pct(autos, .9):.0f} p99 {pct(autos, .99):.0f} max {max(autos, default=0):.0f} (n {len(autos)})")


if __name__ == '__main__':
    main()
