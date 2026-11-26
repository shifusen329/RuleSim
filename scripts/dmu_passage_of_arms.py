#!/usr/bin/env python3
"""Passage of Arms at Dancing Mad's Light of Judgment casts: when Paladins press it and whom it covers (2026-11-26).

  dmu_passage_of_arms.py <ACT log or folder> [...] [--since 20261020] [--loj 2] [--slot OT] [--list]

User, 2026-11-26: "Time passage of arms before Light of Judgement 2 as OT" (DMU_Mit_PLD.json). For every Passage of Arms
(7385) a Paladin pressed while a Light of Judgment's party hit was coming (from the cast's start to its hit), per pull:
the Paladin's slot (MT = Revolting Ruin III #1's target, OT = #2's), the press as seconds after the cast started, when
its channel ended (the Paladin's next action or move), and at the hit: how many party members had Arms Up (1176, 5 s from
each application; it stays after the channel ends) out of how many the hit landed on. Works on ACT logs and on FFLogs fights
rebuilt by fflogs_pull.sh. A summary per Light of Judgment and slot: presses, the press-time spread and the median share
covered, by press-time bucket.
"""
import argparse
import collections
import datetime as dt
import os
import statistics


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def logs(paths):
    for p in paths:
        if os.path.isdir(p):
            yield from sorted(os.path.join(p, f) for f in os.listdir(p) if f.endswith('.log'))
        else:
            yield p


def scan(path, since):
    zone, T = None, None
    for raw in open(path, 'rb'):
        h = raw[:4]
        if h.startswith(b'01|'):
            zone = raw.split(b'|')[2].decode()
            continue
        if zone != '553':
            continue
        if h == b'260|':
            l = raw.decode().split('|')
            if since and l[1][:10].replace('-', '') < since:
                continue
            if l[3] == '1' and T is None:
                T = ts(l[1])
                loj, rr3, presses, arms, hits, chan = [], [], [], collections.defaultdict(list), collections.defaultdict(set), {}
                names = {}
            elif l[3] == '0' and T is not None:
                yield T, os.path.basename(path), loj, rr3, presses, arms, hits, chan, names
                T = None
            continue
        if T is None:
            continue
        if h.startswith(b'20|'):
            l = raw.decode('utf-8', 'replace').split('|')
            r = (ts(l[1]) - T).total_seconds()
            if l[2][:1] == '4' and l[5] == 'Light of Judgment' and (not loj or r - loj[-1] > 2):
                loj.append(r)
            if l[2][:1] == '4' and l[5] == 'Revolting Ruin III' and (not rr3 or r - rr3[-1][0] > 2):
                rr3.append((r, l[6]))
        elif h.startswith(b'21|') or h.startswith(b'22|'):
            l = raw.decode('utf-8', 'replace').split('|')
            r = (ts(l[1]) - T).total_seconds()
            if l[2][:1] == '1':
                names[l[2]] = l[3]
                if l[5] == 'Passage of Arms':
                    presses.append((r, l[2]))
            if l[2][:1] == '4' and l[5] == 'Light of Judgment' and l[6][:1] == '1':
                hits[len(loj)].add((round(r, 1), l[6]))
        elif h.startswith(b'26|') or h.startswith(b'30|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) < 9:
                continue
            r = (ts(l[1]) - T).total_seconds()
            if l[3] == 'Arms Up':
                arms[l[7]].append((r, h.startswith(b'26|'), float(l[4]) if h.startswith(b'26|') else 0))
            elif l[3] == 'Passage of Arms' and h.startswith(b'30|'):
                chan.setdefault(l[7], []).append(r)
    if T is not None:
        yield T, os.path.basename(path), loj, rr3, presses, arms, hits, chan, names


def up_at(events, t):
    """Arms Up on a player at time t: the last gain before t, not removed before t and not expired."""
    on, until = False, -1
    for r, gain, dur in sorted(events):
        if r > t:
            break
        if gain:
            on, until = True, r + dur
        else:
            on = False
    return on and until >= t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--since')
    ap.add_argument('--loj', type=int)
    ap.add_argument('--slot')
    ap.add_argument('--list', action='store_true')
    a = ap.parse_args()
    rows, seen = [], set()
    for path in logs(a.paths):
        for T, f, loj, rr3, presses, arms, hits, chan, names in scan(path, a.since):
            key = (T.strftime('%Y%m%d%H%M%S'), len(presses))
            if key in seen:
                continue
            seen.add(key)
            for p, who in presses:
                n = next((k + 1 for k, c in enumerate(loj) if c - 1 <= p <= c + 5.2), 0)
                if not n or (a.loj and n != a.loj):
                    continue
                slot = 'MT' if rr3 and rr3[0][1] == who else 'OT' if len(rr3) >= 2 and rr3[1][1] == who else '?'
                if a.slot and slot != a.slot:
                    continue
                hit = sorted(hits.get(n, ()))
                if not hit:
                    continue
                t_hit = hit[0][0]
                hitp = {x[1] for x in hit}
                cov = sum(1 for x in hitp if up_at(arms.get(x, []), t_hit))
                end = next((e for e in chan.get(who, []) if e >= p), None)
                rows.append({'f': f, 'T': T, 'who': names.get(who, who), 'slot': slot, 'loj': n, 'p': round(p - loj[n - 1], 1),
                             'hit': round(t_hit - loj[n - 1], 1), 'cov': cov, 'of': len(hitp), 'chan': round(end - p, 1) if end else None})
    if a.list:
        for r in rows:
            print(f"{r['T']:%m-%d %H:%M:%S} {r['who']:<22} {r['slot']} LoJ#{r['loj']} pressed +{r['p']:.1f} (hit +{r['hit']:.1f}); channel "
                  f"{('%.1f s' % r['chan']) if r['chan'] is not None else '?'}; Arms Up at the hit {r['cov']}/{r['of']}")
    for (n, slot), g in sorted(collections.groupby(sorted(rows, key=lambda r: (r['loj'], r['slot'])), key=lambda r: (r['loj'], r['slot'])) if False else
                               ((k, [r for r in rows if (r['loj'], r['slot']) == k]) for k in sorted({(r['loj'], r['slot']) for r in rows}))):
        ps = [r['p'] for r in g]
        print(f"Light of Judgment #{n}, {slot}: {len(g)} presses by {len({r['who'] for r in g})} Paladins, +{min(ps):.1f} to +{max(ps):.1f} "
              f"(median {statistics.median(ps):.1f}); Arms Up on the hit's players, median {statistics.median([r['cov'] / r['of'] for r in g]):.0%}")
        for lo, hi in ((-1, 2.0), (2.0, 3.0), (3.0, 4.0), (4.0, 4.4), (4.4, 6)):
            b = [r for r in g if lo <= r['p'] < hi]
            if b:
                full = sum(1 for r in b if r['cov'] >= r['of'] - 1)
                print(f"   pressed +{max(lo, 0):.1f} to +{hi:.1f}: {len(b):3}  covered all but one or more at the hit: {full:3} ({full / len(b):.0%}); "
                      f"median {statistics.median([r['cov'] / r['of'] for r in b]):.0%}")


if __name__ == '__main__':
    main()
