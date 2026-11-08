#!/usr/bin/env python3
"""What was on each tank when a Dancing Mad tankbuster landed, from ACT logs (2026-11-08).

For the Nth cast of an enemy ability in a pull: the statuses on the cast's target (taken as the main tank) and on the other tank it
hit at the moment the hit lands, with how long before or after the cast's start each was applied and which job applied it. Only
casts that hit two tanks count. Statuses outside MIT are listed at the end so a missed mitigation shows up.

  dmu_buster_mits.py <ACT log or folder> [...] --cast "Ultimate Embrace" --n 2 [--you-job WHM] [--since 20260915]

--you-job keeps pulls where the log's own player was on that job (the pulls the job's Custom Rules could have played).
Reads each log through grep (Git Bash on Windows), several at once. Pulls logged twice count once.
"""
import argparse, collections, concurrent.futures as cf, datetime as dt, os, re, statistics, subprocess, sys

JOB = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB', 0x18: 'WHM', 0x1C: 'SCH', 0x21: 'AST', 0x28: 'SGE', 0x14: 'MNK', 0x16: 'DRG',
       0x1E: 'NIN', 0x22: 'SAM', 0x27: 'RPR', 0x29: 'VPR', 0x17: 'BRD', 0x1F: 'MCH', 0x26: 'DNC', 0x19: 'BLM', 0x1B: 'SMN', 0x23: 'RDM',
       0x2A: 'PCT', 0x24: 'BLU'}
TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}
MIT = re.compile(r"^(Rampart|Guardian|Guardian's Will|Sentinel|Bulwark|Holy Sheltron|Sheltron|Knight's Resolve|Knight's Benediction|Intervention|"
                 r"Divine Veil|Passage of Arms|Cover|Hallowed Ground|Damnation|Vengeance|Thrill of Battle|Bloodwhetting|Stem the Flow|Stem the Tide|"
                 r"Raw Intuition|Nascent Flash|Nascent Glint|Holmgang|Shake It Off|Shake It Off \(Over Time\)|Shadowed Vigil|Vigilant|Shadow Wall|"
                 r"Dark Mind|The Blackest Night|Oblation|Dark Missionary|Living Dead|Walking Dead|Undead Rebirth|Great Nebula|Nebula|Camouflage|"
                 r"Heart of Corundum|Clarity of Corundum|Catharsis of Corundum|Heart of Stone|Heart of Light|Aurora|Superbolide|Aquaveil|Divine Benison|"
                 r"Temperance|Divine Caress|Regen|Medica II|Medica III|Asylum|Liturgy of the Bell|Exaltation|The Bole|Bole|The Spire|The Ewer|The Arrow|"
                 r"Intersection|Collective Unconscious|Sun Sign|Suntouched|Neutral Sect|Aspected Benefic|Aspected Helios|Helios Conjunction|Horoscope|"
                 r"Macrocosmos|Galvanize|Catalyze|Protraction|Excogitation|Sacred Soil|Fey Illumination|Seraphic Illumination|Desperate Measures|"
                 r"Consolation|Haima|Haimatinon|Panhaima|Panhaimatinon|Taurochole|Kerachole|Holosakos|Holos|Eukrasian Diagnosis|Differential Diagnosis|"
                 r"Eukrasian Prognosis|Kardion|Shield Samba|Tactician|Troubadour|Magick Barrier|Radiant Aegis|Tempera Coat|Tempera Grassa|Improvised Finish)$")


def when(s):
    return dt.datetime.fromisoformat(s[:23])


def scan(path, cast_name, nth):
    pat = (r'^(01\||02\||260\||03\|[^|]*\|10|2[0-2]\|[^|]*\|[^|]*\|[^|]*\|[^|]*\|' + re.escape(cast_name) + r'\||'
           r'(26|30)\|[^|]*\|[^|]*\|[^|]*\|[^|]*\|[^|]*\|[^|]*\|10)')
    out, jobs, names, active = [], {}, {}, collections.defaultdict(dict)
    zone, start, cur, me, count = None, None, None, None, 0
    proc = subprocess.Popen(['grep', '-a', '-E', pat, path], stdout=subprocess.PIPE)
    for raw in proc.stdout:
        p = raw.decode('utf-8', 'replace').rstrip('\r\n').split('|')
        c = p[0]
        if c == '01':
            zone, start, cur = p[2], None, None; jobs.clear(); names.clear(); active.clear(); continue
        if c == '02':
            me = p[2]; continue
        if c == '03':
            jobs[p[2]] = JOB.get(int(p[4], 16), p[4]); names[p[2]] = p[3]; continue
        t = when(p[1])
        if c == '260':
            if p[3] == '1' and start is None:
                start, cur, count = t, None, 0; active.clear()
            elif p[3] == '0':
                start, cur = None, None
            continue
        if zone != '553' or start is None:
            continue
        if c == '26':
            if jobs.get(p[7]) in TANKS:
                active[p[7]][p[3]] = (t, t + dt.timedelta(seconds=float(p[4] or 0)), jobs.get(p[5], '?'))
        elif c == '30':
            active[p[7]].pop(p[3], None)
        elif c == '20':
            count += 1
            cur = None
            if count == nth:
                cur = dict(log=os.path.basename(path), start=start, cast=t, mt=p[6], jobs=dict(jobs), names=dict(names), you=jobs.get(me, '?'), hits=set(), snap=None)
                out.append(cur)
        elif c in ('21', '22') and cur and p[6].startswith('10') and 0 <= (t - cur['cast']).total_seconds() <= 8:
            cur['hits'].add(p[6])
            if cur['snap'] is None:
                cur['snap'] = {tg: {n: ((v[0] - cur['cast']).total_seconds(), v[2]) for n, v in d.items() if v[1] >= t} for tg, d in active.items()}
    proc.wait()
    return out


def table(title, casts, tank_of):
    agg, other = collections.defaultdict(list), collections.Counter()
    for c in casts:
        for n, v in c['snap'].get(tank_of(c), {}).items():
            if MIT.match(n):
                agg[n].append(v)
            else:
                other[n] += 1
    print(f'\n{title}')
    for n, v in sorted(agg.items(), key=lambda kv: -len(kv[1])):
        print(f"  {n:26} {len(v):4}/{len(casts)}  applied {statistics.median(x[0] for x in v):+6.1f} s  from {dict(collections.Counter(x[1] for x in v).most_common(4))}")
    return other


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+'); ap.add_argument('--cast', required=True); ap.add_argument('--n', type=int, default=1)
    ap.add_argument('--you-job'); ap.add_argument('--since', default='20260915'); ap.add_argument('--workers', type=int, default=6)
    a = ap.parse_args()
    logs = []
    for p in a.paths:
        logs += [os.path.join(p, f) for f in os.listdir(p) if f.startswith('Network_')] if os.path.isdir(p) else [p]
    logs = sorted(p for p in logs if (re.search(r'_(\d{8})\.log$', p) or [0, '99999999'])[1] >= a.since)
    with cf.ThreadPoolExecutor(a.workers) as ex:
        found = [c for r in ex.map(lambda p: scan(p, a.cast, a.n), logs) for c in r]
    seen, casts = set(), []
    for c in sorted(found, key=lambda x: x['cast']):
        if c['cast'] not in seen and c['snap'] is not None:
            seen.add(c['cast']); casts.append(c)
    both = [c for c in casts if len(c['hits']) == 2 and all(c['jobs'].get(i) in TANKS for i in c['hits']) and c['mt'] in c['hits']]
    print(f"{len(casts)} casts of {a.cast} #{a.n} that hit someone; {len(both)} hit two tanks; your job in those: {dict(collections.Counter(c['you'] for c in both).most_common())}")
    if a.you_job:
        both = [c for c in both if c['you'] == a.you_job.upper()]
        print(f"{len(both)} with you on {a.you_job.upper()}" + (f", {both[0]['cast']:%Y-%m-%d} to {both[-1]['cast']:%Y-%m-%d}; main tanks {dict(collections.Counter(c['jobs'][c['mt']] for c in both))}" if both else ''))
    if not both:
        return
    print(f"cast starts {statistics.median((c['cast'] - c['start']).total_seconds() for c in both):.0f} s into combat (times below are from the cast's start; negative = before it)")
    o1 = table('Main tank (the cast\'s target)', both, lambda c: c['mt'])
    o2 = table('Other tank', both, lambda c: next(i for i in c['hits'] if i != c['mt']))
    print('\nnot counted as mitigation (most common):', dict((o1 + o2).most_common(14)))


if __name__ == '__main__':
    sys.exit(main())
