#!/usr/bin/env python3
"""Every Ultimate Embrace in Dancing Mad in ACT logs: who it was cast on, who it hit, and who had an invulnerability (2026-11-08).

Ultimate Embrace normally hits both tanks 5.0 s after its cast starts. For each cast this reports the cast's target (taken as
the main tank), that player's job, each player the hit landed on with the damage and any invulnerability up at the hit, whether a
player it hit died within 12 s, whether the other tank was alive at the cast, how long the pull went on, and the job of the log's
own player. "Solo" means the hit landed on one player only.

  dmu_ue_hits.py <ACT log or folder> [...] [--since 20260915] [--mt-job WAR] [--invuln Holmgang] [--solo] [--csv out.csv]

--mt-job keeps casts aimed at that job, --invuln those where the main tank had that status at the hit, --solo those that hit
one player. Filters combine. Reads each log through grep (Git Bash on Windows), several at once. Pulls logged twice count once.
"""
import argparse, collections, concurrent.futures as cf, csv, datetime as dt, os, re, statistics, subprocess, sys

JOB = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB', 0x18: 'WHM', 0x1C: 'SCH', 0x21: 'AST', 0x28: 'SGE', 0x14: 'MNK', 0x16: 'DRG',
       0x1E: 'NIN', 0x22: 'SAM', 0x27: 'RPR', 0x29: 'VPR', 0x17: 'BRD', 0x1F: 'MCH', 0x26: 'DNC', 0x19: 'BLM', 0x1B: 'SMN', 0x23: 'RDM',
       0x2A: 'PCT', 0x24: 'BLU'}
TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}
INVULN = 'Holmgang|Living Dead|Walking Dead|Undead Rebirth|Hallowed Ground|Superbolide'
PAT = (r'^(01\||02\||260\||03\|[^|]*\|10|25\|[^|]*\|10|2[0-2]\|[^|]*\|[^|]*\|[^|]*\|[^|]*\|Ultimate Embrace\||'
       r'(26|30)\|[^|]*\|[^|]*\|(' + INVULN + r'|Transcendent)\|)')


def when(s):
    return dt.datetime.fromisoformat(s[:23])


def damage(p):
    """The damage in a 21/22 line's first damage effect (low byte of the flags 3, 5 or 6), large hits included."""
    for i in range(8, 22, 2):
        try:
            flags, val = int(p[i], 16), int(p[i + 1], 16)
        except (ValueError, IndexError):
            return 0
        if flags & 0xFF in (3, 5, 6):
            return (val >> 16) + ((val & 0xFF) << 16 if val & 0x4000 else 0)
    return 0


def scan(path):
    out, jobs, names, dead, in_pull = [], {}, {}, set(), []
    zone, start, cast, me = None, None, None, None
    inv, count = collections.defaultdict(dict), 0
    proc = subprocess.Popen(['grep', '-a', '-E', PAT, path], stdout=subprocess.PIPE)

    def close():
        nonlocal cast
        if cast:
            out.append(cast)
        cast = None

    for raw in proc.stdout:
        p = raw.decode('utf-8', 'replace').rstrip('\r\n').split('|')
        c = p[0]
        if c == '01':
            close(); zone = p[2]; start = None; jobs.clear(); names.clear(); continue
        if c == '02':
            me = p[2]; continue
        if c == '03':
            jobs[p[2]] = JOB.get(int(p[4], 16), p[4]); names[p[2]] = p[3]; continue
        t = when(p[1])
        if c == '260':
            if p[3] == '1' and start is None:
                close(); start = t; count = 0; inv.clear(); dead.clear(); in_pull = []
            elif p[3] == '0':
                close(); start = None
                for x in in_pull:
                    x['end'] = t
            continue
        if zone != '553' or start is None:
            continue
        if c in ('26', '30'):
            if p[3] == 'Transcendent':
                if c == '26':
                    dead.discard(p[7])   # raised
            elif c == '26':
                inv[p[7]][p[3]] = t + dt.timedelta(seconds=float(p[4] or 0))
            else:
                inv[p[7]].pop(p[3], None)
        elif c == '25':
            dead.add(p[2])
            if cast and 0 <= (t - cast['cast']).total_seconds() <= 17 and p[2] in cast['hits']:
                cast['deaths'].add(p[2])
        elif c == '20':
            close(); count += 1
            cast = dict(log=os.path.basename(path), start=start, cast=t, n=count, mt=p[6], hits={}, deaths=set(), inv={}, jobs=dict(jobs), names=dict(names),
                        end=None, me_job=jobs.get(me, '?'), others=[(i, i not in dead) for i, j in jobs.items() if j in TANKS and i != p[6]])
            in_pull.append(cast)
        elif c in ('21', '22') and cast and p[6].startswith('10') and 0 <= (t - cast['cast']).total_seconds() <= 8 and p[6] not in cast['hits']:
            cast['hits'][p[6]] = damage(p)
            cast['inv'][p[6]] = sorted(n for n, e in inv[p[6]].items() if e >= t)
    close()
    proc.wait()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+'); ap.add_argument('--since', default='20260915'); ap.add_argument('--mt-job'); ap.add_argument('--invuln')
    ap.add_argument('--solo', action='store_true'); ap.add_argument('--csv'); ap.add_argument('--workers', type=int, default=6)
    a = ap.parse_args()
    logs = []
    for p in a.paths:
        logs += [os.path.join(p, f) for f in os.listdir(p) if f.startswith('Network_')] if os.path.isdir(p) else [p]
    logs = [p for p in logs if (re.search(r'_(\d{8})\.log$', p) or [0, '99999999'])[1] >= a.since]
    with cf.ThreadPoolExecutor(a.workers) as ex:
        found = [c for r in ex.map(scan, sorted(logs)) for c in r]
    seen, casts = set(), []
    for c in sorted(found, key=lambda x: x['cast']):
        if c['cast'] not in seen:
            seen.add(c['cast']); casts.append(c)
    hit = [c for c in casts if c['hits']]
    print(f'{len(logs)} logs; {len(casts)} Ultimate Embrace casts in Dancing Mad, {len(hit)} that hit someone')
    print('players hit per cast:', dict(sorted(collections.Counter(len(c['hits']) for c in hit).items())))
    print('main tank jobs:', dict(collections.Counter(c['jobs'].get(c['mt'], '?') for c in hit).most_common()))
    sel = hit
    if a.mt_job:
        sel = [c for c in sel if c['jobs'].get(c['mt']) == a.mt_job.upper()]
    if a.invuln:
        sel = [c for c in sel if a.invuln in c['inv'].get(c['mt'], [])]
    if a.solo:
        sel = [c for c in sel if len(c['hits']) == 1]
    print(f'\n{len(sel)} selected (--mt-job {a.mt_job} --invuln {a.invuln} --solo {a.solo})')

    def tag(c, i):
        return f"{c['names'].get(i, i)} {c['jobs'].get(i, '?')}"

    def who(c, i):
        died = ' DIED' if i in c['deaths'] else ''
        inv = f" [{', '.join(c['inv'].get(i, []))}]" if c['inv'].get(i) else ''
        return f"{tag(c, i)} {c['hits'][i]:,}{inv}{died}"

    def other(c):
        return ', '.join(f"{tag(c, i)} {'alive' if ok else 'DEAD'}" for i, ok in c['others']) or 'none'

    def after(c):
        return (c['end'] - c['cast']).total_seconds() - 5 if c['end'] else -1

    for c in sel:
        print(f"{c['cast']:%Y-%m-%d %H:%M:%S}  #{c['n']} at {(c['cast'] - c['start']).total_seconds():.0f}s  on {tag(c, c['mt'])}  hit: "
              + '; '.join(who(c, i) for i in c['hits']) + f"  | other tank: {other(c)} | pull went on {after(c):.0f}s | you {c['me_job']}  ({c['log']})")
    if sel:
        print('\nby main tank (casts; other tank alive; the main tank died within 12 s; median seconds the pull went on after the hit; your jobs):')
        for name, n in collections.Counter(c['names'].get(c['mt'], c['mt']) for c in sel).most_common():
            xs = [c for c in sel if c['names'].get(c['mt'], c['mt']) == name]
            print(f"  {name:24} {n:3}; other tank alive {sum(any(ok for _, ok in c['others']) for c in xs):3}; died {sum(c['mt'] in c['deaths'] for c in xs)}; "
                  f"went on {statistics.median(after(c) for c in xs):.0f}s; {dict(collections.Counter(c['me_job'] for c in xs))}; "
                  f"{min(c['cast'] for c in xs):%m-%d} to {max(c['cast'] for c in xs):%m-%d}")
    if a.csv:
        with open(a.csv, 'w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['cast', 'pull_start', 'n', 'combat_s', 'mt', 'mt_job', 'hit_count', 'hits', 'other_tank', 'pull_went_on_s', 'your_job', 'log'])
            for c in sel:
                w.writerow([c['cast'].isoformat(), c['start'].isoformat(), c['n'], (c['cast'] - c['start']).total_seconds(), c['names'].get(c['mt'], c['mt']),
                            c['jobs'].get(c['mt'], '?'), len(c['hits']), ' / '.join(who(c, i) for i in c['hits']), other(c), round(after(c)), c['me_job'], c['log']])


if __name__ == '__main__':
    sys.exit(main())
