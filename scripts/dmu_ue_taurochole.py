#!/usr/bin/env python3
"""Your Sage's Taurochole against Dancing Mad's first Ultimate Embrace (2026-11-14).

  dmu_ue_taurochole.py <ACT log or folder> [...] [--since 20261110] [--me 10041112,1001A4A8] [--cast "Ultimate Embrace"] [--n 1]

For each pull where you played Sage and the Nth cast happened: when it started, when its hit landed on each tank, and your
Taurochole around it: when you pressed it, on whom, when its status landed on them, and whether that was before that tank's
hit (on time), after it (late: no 10% for that hit) or not at all within the cast's +-25 s. A Taurochole on a player the cast
didn't hit counts as missed.
"""
import datetime as dt
import os
import re
import sys

TANKS = {0x13, 0x15, 0x20, 0x25}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def amount(v):
    v = int(v, 16)
    return (v >> 16) + ((v & 0xFF) << 16 if v & 0x4000 else 0)


def main():
    args = sys.argv[1:]
    me, since, cast, nth, paths = {'10041112', '1001A4A8'}, None, 'Ultimate Embrace', 1, []
    i = 0
    while i < len(args):
        a = args[i]
        if a in ('--me', '--since', '--cast', '--n'):
            v = args[i + 1]
            i += 2
            if a == '--me':
                me = set(v.split(','))
            elif a == '--since':
                since = v
            elif a == '--cast':
                cast = v
            else:
                nth = int(v)
        else:
            paths.append(a)
            i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    rows = []
    for log in logs:
        zone, T, pull = None, None, None
        jobs, names = {}, {}
        for raw in open(log, 'rb'):
            p = raw[:4]
            if p.startswith(b'01|'):
                zone = raw.split(b'|')[2].decode()
                continue
            if p.startswith(b'03|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if len(l) > 4 and l[2][:1] == '1':
                    jobs[l[2]] = int(l[4], 16)
                    names[l[2]] = l[3]
                continue
            if zone != '553':
                continue
            if p == b'260|':
                l = raw.decode().split('|')
                if since and l[1][:10].replace('-', '') < since:
                    continue
                if l[3] == '1' and T is None:
                    T = ts(l[1])
                    mine = next((i for i in me if jobs.get(i) == 0x28), None)
                    pull = {'T': T, 'me': mine, 'casts': [], 'hits': {}, 'press': [], 'gain': [], 'lost': []} if mine else None
                elif l[3] == '0' and T is not None:
                    if pull and len(pull['casts']) >= nth:
                        rows.append((pull, dict(jobs), dict(names)))
                    T, pull = None, None
                continue
            if pull is None:
                continue
            if p.startswith(b'20|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[5] == cast and l[2][:1] == '4':
                    r = (ts(l[1]) - T).total_seconds()
                    if not pull['casts'] or r - pull['casts'][-1] > 2:
                        pull['casts'].append(r)
            elif p.startswith(b'21|') or p.startswith(b'22|'):
                l = raw.decode('utf-8', 'replace').split('|')
                r = (ts(l[1]) - T).total_seconds()
                if l[5] == cast and l[2][:1] == '4' and l[6][:1] == '1' and len(pull['casts']) == nth:
                    d = sum(amount(l[k + 1]) for k in range(8, 24, 2) if l[k] and int(l[k], 16) & 0xFF in (3, 5, 6))
                    pull['hits'].setdefault(l[6], (r, d, l[24], l[25]))
                elif l[2] == pull['me'] and l[5] == 'Taurochole':
                    pull['press'].append((r, l[6]))
            elif p.startswith(b'26|') or p.startswith(b'30|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[3] == 'Taurochole' and l[5] == pull['me']:
                    pull['gain' if p.startswith(b'26|') else 'lost'].append(((ts(l[1]) - T).total_seconds(), l[7]))
    late = ontime = missed = 0
    for pull, jobs, names in rows:
        c = pull['casts'][nth - 1]
        hits = pull['hits']
        tanks = {t: h for t, h in hits.items() if jobs.get(t) in TANKS}
        print(f"{pull['T']:%Y-%m-%d %H:%M:%S}  {cast} #{nth} cast at {c:.1f} s; hits: " +
              ', '.join(f"{names.get(t, t)} {h[0] - c:+.1f} s ({h[1]:,} on {int(h[2]):,}/{int(h[3]):,})" for t, h in tanks.items()))
        near = [(r, tgt) for r, tgt in pull['gain'] if c - 25 <= r <= c + 25]
        presses = [(r, tgt) for r, tgt in pull['press'] if c - 25 <= r <= c + 25]
        if not near:
            missed += 1
            print(f"    no Taurochole from you within 25 s of the cast{'; pressed at ' + ', '.join(f'{r - c:+.1f}' for r, _ in presses) if presses else ''}")
            continue
        for r, tgt in near:
            press = min(presses, key=lambda x: abs(x[0] - r), default=None)
            h = hits.get(tgt)
            who = names.get(tgt, tgt)
            ptxt = f"pressed {press[0] - c:+.1f} s" if press else 'press not seen'
            if h is None:
                missed += 1
                verdict = 'MISSED (that player was not hit)'
            elif r <= h[0] and not any(r < x <= h[0] for x, w in pull['lost'] if w == tgt):
                ontime += 1
                verdict = f'on time, {h[0] - r:.1f} s before the hit'
            elif r <= h[0]:
                missed += 1
                gone = min(x for x, w in pull['lost'] if w == tgt and r < x <= h[0])
                verdict = f'EXPIRED {h[0] - gone:.1f} s before the hit (landed {h[0] - r:.1f} s early)'
            else:
                late += 1
                verdict = f'LATE, {r - h[0]:.1f} s after the hit'
            print(f"    Taurochole on {who}: {ptxt}, landed {r - c:+.1f} s (hit {h[0] - c:+.1f} s): {verdict}" if h else
                  f"    Taurochole on {who}: {ptxt}, landed {r - c:+.1f} s: {verdict}")
    print(f'pulls {len(rows)}: Taurochole on time {ontime}, late {late}, none, expired or on someone not hit {missed}')


if __name__ == '__main__':
    main()
