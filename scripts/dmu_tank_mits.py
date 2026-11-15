#!/usr/bin/env python3
"""Each tank's tankbuster mitigation and invulnerabilities in Dancing Mad, by tank slot (2026-11-15).

  dmu_tank_mits.py <ACT log or folder> [...] [--since 20261001] [--job GNB,PLD] [--bust "RR3#1,HD#2"] [--clears]

For every pull and every tank in it: the slot (MT = whom Revolting Ruin III #1 was cast at, OT = #2's), then per tankbuster
the hits it took (seconds after the buster's anchor cast starts, % of max HP), the tank's own mitigation and
invulnerability presses from 15 s before to 20 s after the anchor (same clock, in press order: the weave order), and the
mitigation statuses up at each hit. Works on real ACT logs and on FFLogs fights rebuilt by fflogs_pull.sh. --clears keeps
pulls that reached Phase 5's second Maddening Orchestra. A summary at the end: per job and slot, for each buster, how
often each action was pressed for it and its median press time.

Busters (anchor cast #n; the hits counted): RR3#1/RR3#2 (Revolting Ruin III), HD#1/HD#2 (Light of Judgment #1/#2;
Hyperdrive), UE#1/UE#2 (Ultimate Embrace), WINGS#2 (Wings of Destruction #2), TH#1..TH#7 (Exdeath's Thunder III casts),
REP#1/REP#2 (Ultima Repeater; Fell Forces), ORCH#1/ORCH#2 (Maddening Orchestra; Chaotic Holy/Flare, Flare Diffusion,
Fell Forces).
"""
import argparse
import collections
import datetime as dt
import os
import re
import statistics

JOB = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB'}
BUSTERS = [('RR3#1', 'Revolting Ruin III', 1, {'Revolting Ruin III'}),
           ('HD#1', 'Light of Judgment', 1, {'Hyperdrive'}),
           ('RR3#2', 'Revolting Ruin III', 2, {'Revolting Ruin III'}),
           ('HD#2', 'Light of Judgment', 2, {'Hyperdrive'}),
           ('UE#1', 'Ultimate Embrace', 1, {'Ultimate Embrace'}),
           ('WINGS#2', 'Wings of Destruction', 2, {'Wings of Destruction'}),
           ('UE#2', 'Ultimate Embrace', 2, {'Ultimate Embrace'})] + \
          [(f'TH#{i}', 'Thunder III', i, {'Thunder III'}) for i in range(1, 8)] + \
          [('REP#1', 'Ultima Repeater', 1, {'Fell Forces'}), ('REP#2', 'Ultima Repeater', 2, {'Fell Forces'}),
           ('ORCH#1', 'Maddening Orchestra', 1, {'Chaotic Holy', 'Chaotic Flare', 'Flare Diffusion', 'Fell Forces'}),
           ('ORCH#2', 'Maddening Orchestra', 2, {'Chaotic Holy', 'Chaotic Flare', 'Flare Diffusion', 'Fell Forces'})]
PRESS = {'Rampart', 'Camouflage', 'Nebula', 'Great Nebula', 'Heart of Stone', 'Heart of Corundum', 'Aurora', 'Superbolide',
         'Sentinel', 'Guardian', 'Bulwark', 'Sheltron', 'Holy Sheltron', 'Intervention', 'Hallowed Ground', 'Cover',
         'Vengeance', 'Damnation', 'Thrill of Battle', 'Raw Intuition', 'Bloodwhetting', 'Nascent Flash', 'Holmgang',
         'Equilibrium', 'Shadow Wall', 'Shadowed Vigil', 'Dark Mind', 'The Blackest Night', 'Oblation', 'Living Dead',
         'Arm\'s Length', 'Reprisal', 'Provoke', 'Shirk'}
INVULN = {'Superbolide', 'Hallowed Ground', 'Holmgang', 'Living Dead', 'Walking Dead', 'Undead Rebirth'}
UP = re.compile(r"^(Rampart|Camouflage|Nebula|Great Nebula|Heart of Corundum|Clarity of Corundum|Heart of Stone|Aurora|"
                r"Superbolide|Guardian|Sentinel|Bulwark|Holy Sheltron|Knight's Resolve|Intervention|Knight's Benediction|"
                r"Hallowed Ground|Vengeance|Damnation|Thrill of Battle|Bloodwhetting|Stem the Flow|Stem the Tide|Holmgang|"
                r"Shadow Wall|Shadowed Vigil|Dark Mind|The Blackest Night|Oblation|Living Dead|Walking Dead|Reprisal)$")


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def amount(v):
    v = int(v, 16)
    return (v >> 16) + ((v & 0xFF) << 16 if v & 0x4000 else 0)


def pulls(path, since):
    zone, T, cur, jobs, names = None, None, None, {}, {}
    for raw in open(path, 'rb'):
        p = raw[:4]
        if p.startswith(b'01|'):
            zone = raw.split(b'|')[2].decode()
            continue
        if p.startswith(b'03|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) > 4 and l[2][:1] == '1':
                try:
                    jobs[l[2]] = int(l[4], 16)
                except ValueError:
                    pass
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
                cur = {'T': T, 'file': os.path.basename(path), 'casts': collections.defaultdict(list), 'cast_tgt': collections.defaultdict(list),
                       'hits': [], 'press': [], 'status': [], 'jobs': jobs, 'names': names}
            elif l[3] == '0' and T is not None:
                cur['jobs'], cur['names'] = dict(jobs), dict(names)
                yield cur
                T = None
            continue
        if T is None:
            continue
        if p.startswith(b'20|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[2][:1] == '4':
                r = (ts(l[1]) - T).total_seconds()
                c = cur['casts'][l[5]]
                if not c or r - c[-1] > 2:
                    c.append(r)
                    cur['cast_tgt'][l[5]].append(l[6])
        elif p.startswith(b'21|') or p.startswith(b'22|'):
            l = raw.decode('utf-8', 'replace').split('|')
            r = (ts(l[1]) - T).total_seconds()
            if l[2][:1] == '4' and l[6][:1] == '1':
                d = sum(amount(l[k + 1]) for k in range(8, 24, 2) if l[k] and int(l[k], 16) & 0xFF in (3, 5, 6))
                try:
                    mx = int(l[25])
                except (ValueError, IndexError):
                    mx = 0
                cur['hits'].append((r, l[5], l[6], 100 * d / mx if mx else 0))
            elif l[2][:1] == '1' and l[5] in PRESS:
                if not cur['press'] or cur['press'][-1][1:3] != (l[2], l[5]) or r - cur['press'][-1][0] > 1:
                    cur['press'].append((r, l[2], l[5], l[7]))
        elif p.startswith(b'26|') or p.startswith(b'30|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) > 8 and UP.match(l[3]) and l[7][:1] in '14':
                cur['status'].append(((ts(l[1]) - T).total_seconds(), p.startswith(b'26|'), l[3], l[7], l[5]))
    # FFLogs fights rebuilt by convert.py have no closing 260 line: the pull runs to the end of the file.
    if T is not None:
        cur['jobs'], cur['names'] = dict(jobs), dict(names)
        yield cur


def up_at(status, who, t):
    on = {}
    for r, gain, name, tgt, src in status:
        if r > t:
            break
        if tgt != who:
            continue
        if gain:
            on[name] = src
        else:
            on.pop(name, None)
    return on


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--since')
    ap.add_argument('--job')
    ap.add_argument('--bust')
    ap.add_argument('--clears', action='store_true')
    ap.add_argument('--quiet', action='store_true', help='summary only')
    a = ap.parse_args()
    want_jobs = set(a.job.split(',')) if a.job else None
    want_bust = set(a.bust.split(',')) if a.bust else None
    logs = []
    for p in a.paths:
        if os.path.isdir(p):
            for root, _, files in os.walk(p):
                logs += [os.path.join(root, f) for f in files if re.match(r'Network_\d+_\d+\.log$', f)]
        else:
            logs.append(p)
    summary = collections.defaultdict(lambda: collections.defaultdict(list))
    counts = collections.Counter()
    invulns = []
    for log in sorted(logs):
        for c in pulls(log, a.since):
            rr3 = c['cast_tgt'].get('Revolting Ruin III', [])
            if len(rr3) < 2:
                continue
            if a.clears and len(c['casts'].get('Maddening Orchestra', [])) < 2:
                continue
            slot = {rr3[0]: 'MT', rr3[1]: 'OT'}
            tanks = [i for i, j in c['jobs'].items() if j in JOB and i in slot]
            # Each tank's invulnerabilities against the buster whose anchor came 1 s before to 13 s after the press.
            row = {}
            for tk in tanks:
                got = []
                for r, who, act, tgt in c['press']:
                    if who != tk or act not in INVULN:
                        continue
                    for tag, anchor, n, _ in BUSTERS:
                        starts = c['casts'].get(anchor, [])
                        if len(starts) >= n and -1 <= r - starts[n - 1] <= 13:
                            got.append(f'{act[:2]} {tag} {r - starts[n - 1]:+.1f}')
                            break
                    else:
                        got.append(f'{act[:2]} at {r:.0f} s')
                row[slot[tk]] = (JOB[c['jobs'][tk]], got)
            if len(row) == 2:
                invulns.append((c['T'], row))
            for tk in tanks:
                job = JOB[c['jobs'][tk]]
                if want_jobs and job not in want_jobs:
                    continue
                key = (job, slot[tk])
                counts[key] += 1
                if not a.quiet:
                    print(f"== {c['file']} {c['T']:%Y-%m-%d %H:%M}  {job} {slot[tk]} {c['names'].get(tk, tk)}")
                for tag, anchor, n, hitnames in BUSTERS:
                    if want_bust and tag not in want_bust:
                        continue
                    starts = c['casts'].get(anchor, [])
                    if len(starts) < n:
                        continue
                    t0 = starts[n - 1]
                    nxt = starts[n] if len(starts) > n else t0 + 40
                    hits = [h for h in c['hits'] if h[2] == tk and h[1] in hitnames and t0 <= h[0] < min(nxt, t0 + 30)]
                    pr = [(r - t0, act) for r, who, act, tgt in c['press'] if who == tk and t0 - 15 <= r <= t0 + 20]
                    co = [(r - t0, act) for r, who, act, tgt in c['press'] if who != tk and tgt == c['names'].get(tk) and t0 - 15 <= r <= t0 + 20]
                    for r, act in pr:
                        summary[key][(tag, act)].append(r)
                    summary[key][(tag, '#hit')].append(1 if hits else 0)
                    if not a.quiet and (hits or pr):
                        ht = ', '.join(f'+{h[0] - t0:.1f} {h[3]:.0f}%' for h in hits[:6])
                        ps = ', '.join(f'{act} {r:+.1f}' for r, act in pr)
                        cs = ', '.join(f'{act} {r:+.1f} (co)' for r, act in co)
                        ups = ''
                        if hits:
                            on = up_at(c['status'], tk, hits[0][0] + 0.05)
                            ups = ' | up at 1st hit: ' + (', '.join(sorted(on)) or '-')
                        print(f'   {tag:7} hits [{ht}]  pressed: {ps or "-"}{"; " + cs if cs else ""}{ups}')
    print('\n==== invulnerabilities per pull (Su Superbolide, Ha Hallowed Ground, Ho Holmgang, Li Living Dead; buster, s after its anchor) ====')
    for T, row in invulns:
        print(f"{T:%Y-%m-%d %H:%M}  MT {row['MT'][0]}: {', '.join(row['MT'][1]) or '-':44} | OT {row['OT'][0]}: {', '.join(row['OT'][1]) or '-'}")
    print('\n==== summary: for each buster, pressed by n of the tank-pulls (median press time after the anchor cast) ====')
    for key in sorted(summary):
        n = counts[key]
        print(f'-- {key[0]} {key[1]} ({n} tank-pulls)')
        for tag, *_ in BUSTERS:
            took = summary[key].get((tag, '#hit'), [])
            acts = sorted({act for (t, act) in summary[key] if t == tag and act != '#hit'},
                          key=lambda x: statistics.median(summary[key][(tag, x)]))
            if not acts and not any(took):
                continue
            parts = [f'{act} {len(summary[key][(tag, act)])} ({statistics.median(summary[key][(tag, act)]):+.1f})' for act in acts]
            print(f'   {tag:7} took hits {sum(took)}/{len(took)}:  ' + ', '.join(parts))


if __name__ == '__main__':
    main()
