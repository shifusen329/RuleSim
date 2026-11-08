#!/usr/bin/env python3
"""The first Revolting Ruin III in Dancing Mad as a Sage plays it, from ACT logs (2026-11-08).

For each pull where the log's own player is a Sage: when the buster's two hits land, how much each does, the tank's HP around them,
and when Taurochole went out, on whom, how much it healed and how much of that was overheal. Also where the Sage's weave slots fell
between the two hits (a slot: the time from the end of a cast or an instant GCD's animation until the next GCD can start).

  dmu_rr3_sge.py <ACT log or folder> [...] [--since 20261025] [--detail N]

Damage and heals are read from the first damage or heal effect of the 21/22 line. HP is the target's HP on that line, which the log
takes before the effect is applied. Reads each log through grep (Git Bash on Windows). Pulls logged twice count once.
"""
import argparse, collections, concurrent.futures as cf, datetime as dt, os, re, statistics as st, subprocess, sys

SGE = 0x28
GCDS = {'Dosis III', 'Eukrasian Dosis III', 'Dyskrasia II', 'Toxikon II', 'Phlegma III', 'Eukrasia', 'Pneuma', 'Eukrasian Diagnosis',
        'Eukrasian Prognosis II', 'Eukrasian Prognosis', 'Egeiro', 'Diagnosis', 'Prognosis', 'Eukrasian Dyskrasia'}
CAST_S = {'Dosis III': 1.5, 'Dyskrasia II': 0.0}


def when(s):
    return dt.datetime.fromisoformat(s[:23])


def amount(p, kinds):
    for i in range(8, 22, 2):
        try:
            flags, val = int(p[i], 16), int(p[i + 1], 16)
        except (ValueError, IndexError):
            return 0
        if flags & 0xFF in kinds:
            return (val >> 16) + ((val & 0xFF) << 16 if val & 0x4000 else 0)
    return 0


def scan(path):
    pat = r'^(01\||02\||260\||03\|[^|]*\|10|20\||2[12]\||3[79]\|[^|]*\|10)'
    out, jobs = [], {}
    zone, start, cur, me, n = None, None, None, None, 0
    proc = subprocess.Popen(['grep', '-a', '-E', pat, path], stdout=subprocess.PIPE)

    def close():
        nonlocal cur
        if cur:
            out.append(cur)
        cur = None

    for raw in proc.stdout:
        p = raw.decode('utf-8', 'replace').rstrip('\r\n').split('|')
        c = p[0]
        if c == '01':
            close(); zone, start = p[2], None; jobs.clear(); continue
        if c == '02':
            me = p[2]; continue
        if c == '03':
            jobs[p[2]] = int(p[4], 16); continue
        t = when(p[1])
        if c == '260':
            if p[3] == '1' and start is None:
                close(); start, n = t, 0
            elif p[3] == '0':
                close(); start = None
            continue
        if zone != '553' or start is None:
            continue
        if c in ('37', '39'):
            # The real HP updates (the ability lines' own HP can be seconds stale). 37: HP p[5], max p[6]; 39: HP p[4], max p[5].
            i = 4 if c == '39' else 5
            if cur and p[2] == cur['tank'] and len(p) > i + 1 and p[i].isdigit() and p[i + 1].isdigit() and int(p[i + 1]) > 0:
                cur['hp'].append(((t - cur['cast']).total_seconds(), int(p[i]), int(p[i + 1])))
            continue
        if len(p) < 8:
            continue
        name = p[5]
        if c == '20':
            if name == 'Revolting Ruin III' and p[2].startswith('4'):
                n += 1
                if n == 1 and jobs.get(me) == SGE:
                    close()
                    cur = dict(log=os.path.basename(path), start=start, cast=t, tank=p[6], tname=p[7], hits=[], tauro=[], gcds=[], me=me, hp=[])
            elif cur and p[2] == me and name in GCDS:
                cur['gcds'].append(((t - cur['cast']).total_seconds(), name, float(p[8] or 0) if len(p) > 8 else 0.0))
            continue
        if not cur or len(p) < 26:
            continue
        d = (t - cur['cast']).total_seconds()
        if d > 14:
            close(); continue
        if name.startswith('Revolting Ruin') and p[2].startswith('4') and p[6] == cur['tank']:
            hp, mx = int(p[24] or 0) if p[24].isdigit() else 0, int(p[25]) if p[25].isdigit() else 0
            cur['hits'].append((d, name, amount(p, (3, 5, 6)), hp, mx))
        elif p[2] == me and name == 'Taurochole':
            hp, mx = int(p[24]) if p[24].isdigit() else 0, int(p[25]) if p[25].isdigit() else 0
            cur['tauro'].append((d, p[7], p[6] == cur['tank'], amount(p, (4,)), hp, mx))
        elif p[2] == me and name in GCDS and not any(abs(g[0] - d) < 2.0 and g[1] == name for g in cur['gcds']):
            cur['gcds'].append((d, name, 0.0))
    close()
    proc.wait()
    return out


def q(xs, f=lambda x: f'{x:.1f}'):
    xs = sorted(xs)
    return f'{f(xs[len(xs) // 4])} / {f(st.median(xs))} / {f(xs[min(len(xs) - 1, 3 * len(xs) // 4)])}' if xs else '-'


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('paths', nargs='+'); ap.add_argument('--since', default='20261025'); ap.add_argument('--detail', type=int, default=0)
    a = ap.parse_args()
    logs = []
    for p in a.paths:
        logs += [os.path.join(p, f) for f in os.listdir(p) if f.startswith('Network_')] if os.path.isdir(p) else [p]
    logs = sorted(p for p in logs if (re.search(r'_(\d{8})\.log$', p) or [0, '99999999'])[1] >= a.since)
    with cf.ThreadPoolExecutor(6) as ex:
        found = [c for r in ex.map(scan, logs) for c in r]
    seen, pulls = set(), []
    for c in sorted(found, key=lambda x: x['cast']):
        if c['cast'] not in seen and len(c['hits']) >= 2:
            seen.add(c['cast']); pulls.append(c)
    print(f"{len(pulls)} Sage pulls with both hits of the first Revolting Ruin III, {pulls[0]['cast']:%m-%d} to {pulls[-1]['cast']:%m-%d}" if pulls else 'none')
    if not pulls:
        return
    print('hit names:', dict(collections.Counter(h[1] for c in pulls for h in c['hits'][:2])))
    print('cast starts in combat at (q1/median/q3 s):', q([(c['cast'] - c['start']).total_seconds() for c in pulls]))
    h1 = [c['hits'][0] for c in pulls]; h2 = [c['hits'][1] for c in pulls]
    print('hit 1 lands, s after the cast starts:', q([h[0] for h in h1]), '| hit 2:', q([h[0] for h in h2]), '| gap:', q([b[0] - a_[0] for a_, b in zip(h1, h2)]))
    pct = lambda v, mx: 100.0 * v / mx if mx else 0
    print('damage, % of max HP: hit 1', q([pct(h[2], h[4]) for h in h1], lambda x: f'{x:.0f}'), '| hit 2', q([pct(h[2], h[4]) for h in h2], lambda x: f'{x:.0f}'))

    def at(c, s):
        """The tank's HP fraction at the last real HP update at or before s seconds after the cast starts."""
        xs = [x for x in c['hp'] if x[0] <= s]
        return xs[-1][1] / xs[-1][2] if xs else None

    def low(c, a_, b):
        xs = [x[1] / x[2] for x in c['hp'] if a_ < x[0] <= b]
        return min(xs) if xs else None
    pc = lambda xs: q([100 * x for x in xs if x is not None], lambda x: f'{x:.0f}')
    print('tank HP % (real updates): before hit 1', pc([at(c, 5.0) for c in pulls]), '| 1.4 s after hit 1', pc([at(c, 6.5) for c in pulls]),
          '| just before hit 2', pc([at(c, 8.0) for c in pulls]), '| lowest in the 3 s after hit 2', pc([low(c, 8.2, 11.2) for c in pulls]))
    tp = [c for c in pulls if c['tauro']]
    moved = []
    for c in tp:
        t, d1, now, lo = c['tauro'][0], c['hits'][0][2], at(c, 8.0), low(c, 8.2, 11.2)
        if t[0] >= c['hits'][0][0] or now is None or lo is None or not t[5]:
            continue
        lost = (d1 / 9) / t[5]                                   # hit 1 without Taurochole's 10%
        # The heal counts only up to what is still missing just before hit 2 (other heals fill the rest), plus what hit 1 now adds.
        gain = min(t[3] / t[5], max(0.0, 1 - now) + lost) - lost
        moved.append((gain, lo, lost))
    if moved:
        print(f"\nIf Taurochole landed after hit 1 instead ({len(moved)} pulls, estimate): heal that would count, net of hit 1 losing the 10%, % of max HP:",
              pc([m[0] for m in moved]), f"| better in {sum(m[0] > 0 for m in moved)}, worse in {sum(m[0] < 0 for m in moved)}, same in {sum(m[0] == 0 for m in moved)}")
        print('  lowest HP after hit 2, now:', pc([m[1] for m in moved]), '| estimated:', pc([min(1.0, m[1] + m[0]) for m in moved]), '| tanks under 20% now:', sum(m[1] < .2 for m in moved),
              '| estimated:', sum(min(1.0, m[1] + m[0]) < .2 for m in moved))
    print(f"\nTaurochole in that window: {len(tp)} of {len(pulls)} pulls; on the buster's tank {sum(c['tauro'][0][2] for c in tp)}")
    if tp:
        t0 = [c['tauro'][0] for c in tp]
        print('  lands, s after the cast starts:', q([t[0] for t in t0]), '| before hit 1 in', sum(t[0] < c['hits'][0][0] for t, c in zip(t0, tp)), '| between the hits in',
              sum(c['hits'][0][0] <= t[0] < c['hits'][1][0] for t, c in zip(t0, tp)))
        eff = [(t[3], min(t[3], max(0, t[5] - t[4])), t[5]) for t in t0 if t[5]]
        print('  heal (raw) as % of max HP:', q([pct(r, m) for r, _, m in eff], lambda x: f'{x:.0f}'), '| what the tank could take (effective, by its HP when it landed):',
              q([pct(e, m) for _, e, m in eff], lambda x: f'{x:.0f}'), f"| overheal {100 * (1 - sum(e for _, e, _ in eff) / max(1, sum(r for r, _, _ in eff))):.0f}%")
    # The rule's press on the logged GCDs: the first moment from 5.5 s with a weave slot (not casting, 0.6 s or more to the next GCD),
    # or, not casting, when waiting out the next cast would land after 7.5 s (a small clip of the next GCD).
    ws, clips = collections.Counter(), []
    for c in pulls:
        cast = sorted((g[0], g[0] + max(g[2], 0.6)) for g in c['gcds'])
        res = None
        t = 5.5
        while t < 7.5 and res is None:
            if not any(s_ <= t < e_ for s_, e_ in cast):
                nxt = min([s_ for s_, _ in cast if s_ > t] or [t + 2.5])
                rem = nxt - t
                if rem >= 0.6:
                    res = ('a clean weave slot', 0.0)
                elif t + rem + 1.5 > 7.5:
                    res = ('a small clip of the next GCD', 0.6 - rem)
            t += 0.05
        res = res or ('no press possible in the window', 0.0)
        ws[res[0]] += 1
        if res[1]:
            clips.append(res[1])
    print('\nThe between-the-hits press, on the logged GCDs:', dict(ws), '| clip when there is one, s:', q(clips))
    if a.detail:
        print()
        for c in pulls[-a.detail:]:
            print(f"{c['cast']:%m-%d %H:%M:%S} on {c['tname']}: hits " + ', '.join(f"+{h[0]:.1f}s {h[2]:,}" for h in c['hits'][:2]) + ' | Taurochole '
                  + (', '.join(f"+{t[0]:.1f}s on {t[1]} heal {t[3]:,} at {pct(t[4], t[5]):.0f}%" for t in c['tauro']) or 'none') + ' | GCDs '
                  + ', '.join(f"{g[0]:+.1f} {g[1].split()[0]}" for g in sorted(c['gcds'])[:7]))


if __name__ == '__main__':
    sys.exit(main())
