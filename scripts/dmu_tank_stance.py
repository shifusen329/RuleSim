#!/usr/bin/env python3
"""Tank stances, Provoke/Shirk and Kefka's target in Dancing Mad's Phase 1 to the first Ultimate Embrace (2026-11-24).

  dmu_tank_stance.py <ACT log or folder> [...] [--since 20261124] [--to 60] [--job GNB]

User, 2026-11-24: "update DMU_Mit_GNB.json so that Royal Guard is disabled when OT mits is enabled, and activate it before
the first Revolting Ruin in P1." Per pull: each tank's stance (Royal Guard, Defiance, Grit, Iron Will) at the pull and
every change after it, Provoke/Shirk presses (target), the targets of Kefka's auto-attacks as they change, Revolting Ruin
III casts (start, target) and the players their hits landed on, and Ultimate Embrace #1 (start, target, players hit, the
invulnerabilities up). Times are seconds after the 260 line that starts combat. --to cuts the event list at that many
seconds (Ultimate Embrace is always listed); --job keeps pulls with a tank on that job.
"""
import argparse
import datetime as dt
import os
import re

JOB = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB'}
STANCE = {'Royal Guard', 'Defiance', 'Grit', 'Iron Will'}
PRESS = {'Provoke', 'Shirk', 'Royal Guard', 'Release Royal Guard', 'Defiance', 'Release Defiance', 'Grit', 'Release Grit',
         'Iron Will', 'Release Iron Will', 'Holmgang', 'Superbolide', 'Hallowed Ground', 'Living Dead'}
INVULN = {'Holmgang', 'Superbolide', 'Hallowed Ground', 'Living Dead', 'Walking Dead'}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def logs(paths):
    for p in paths:
        if os.path.isdir(p):
            yield from sorted(os.path.join(p, f) for f in os.listdir(p) if f.startswith('Network_') and f.endswith('.log'))
        else:
            yield p


def pulls(path, since):
    zone, T, cur, jobs, names = None, None, None, {}, {}
    stance, invuln = {}, {}  # player -> stance name (across the whole log), player -> set of invulns up
    for raw in open(path, 'rb'):
        h = raw[:4]
        if h.startswith(b'01|'):
            zone = raw.split(b'|')[2].decode()
            continue
        if h.startswith(b'03|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) > 4 and l[2][:1] == '1':
                try:
                    jobs[l[2]] = int(l[4], 16)
                except ValueError:
                    pass
                names[l[2]] = l[3]
            continue
        if h.startswith(b'26|') or h.startswith(b'30|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) > 8 and l[7][:1] == '1' and (l[3] in STANCE or l[3] in INVULN):
                gain = h.startswith(b'26|')
                if l[3] in STANCE:
                    if gain:
                        stance[l[7]] = l[3]
                    elif stance.get(l[7]) == l[3]:
                        stance.pop(l[7])
                else:
                    s = invuln.setdefault(l[7], set())
                    (s.add if gain else s.discard)(l[3])
                if T is not None:
                    cur['ev'].append(((ts(l[1]) - T).total_seconds(), l[7], ('+' if gain else '-') + l[3]))
            continue
        if zone != '553':
            continue
        if h == b'260|':
            l = raw.decode().split('|')
            if since and l[1][:10].replace('-', '') < since:
                continue
            if l[3] == '1' and T is None:
                T = ts(l[1])
                cur = {'T': T, 'file': os.path.basename(path), 'stance0': dict(stance), 'ev': [], 'autos': [], 'rr3': [], 'ue': [], 'party': set(),
                       'jobs': jobs, 'names': names}
            elif l[3] == '0' and T is not None:
                cur['jobs'], cur['names'] = dict(jobs), dict(names)
                yield cur
                T = None
            continue
        if T is None:
            continue
        if h.startswith(b'20|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[2][:1] == '4' and l[5] in ('Revolting Ruin III', 'Ultimate Embrace'):
                r = (ts(l[1]) - T).total_seconds()
                lst = cur['rr3'] if l[5] == 'Revolting Ruin III' else cur['ue']
                if not lst or r - lst[-1]['t'] > 2:
                    lst.append({'t': r, 'tgt': l[6], 'hit': []})
        elif h.startswith(b'21|') or h.startswith(b'22|'):
            l = raw.decode('utf-8', 'replace').split('|')
            r = (ts(l[1]) - T).total_seconds()
            if l[2][:1] == '4' and l[6][:1] == '1':
                # Kefka's auto-attack is action C252 ("unknown_c252" in the log).
                if (l[4] == 'C252' or l[5].lower() == 'attack') and l[3].startswith('Kefka'):
                    if not cur['autos'] or cur['autos'][-1][1] != l[6]:
                        cur['autos'].append((r, l[6]))
                for key, name in (('rr3', 'Revolting Ruin III'), ('ue', 'Ultimate Embrace')):
                    if l[5] == name and cur[key]:
                        c = cur[key][-1]
                        if l[6] not in [x[1] for x in c['hit']]:
                            c['hit'].append((round(r - c['t'], 1), l[6], sorted(invuln.get(l[6], ()))))
            if l[2][:1] == '1':
                cur['party'].add(l[2])
            if l[2][:1] == '1' and l[5] in PRESS:
                if not cur['ev'] or cur['ev'][-1][1:] != (l[2], l[5]) or r - cur['ev'][-1][0] > 1:
                    cur['ev'].append((r, l[2], l[5] + (f" -> {l[7]}" if l[5] in ('Provoke', 'Shirk') else '')))
    if T is not None:
        cur['jobs'], cur['names'] = dict(jobs), dict(names)
        yield cur


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--since')
    ap.add_argument('--to', type=float, default=40)
    ap.add_argument('--job')
    a = ap.parse_args()
    for path in logs(a.paths):
        for c in pulls(path, a.since):
            tanks = {i: JOB[j] for i, j in c['jobs'].items() if j in JOB and i in c['party']}
            if a.job and a.job not in tanks.values():
                continue
            if not c['rr3'] and not c['autos']:
                continue
            nm = lambda i: f"{c['names'].get(i, i)} {tanks.get(i, '')}".strip()
            print(f"== {c['T']:%Y-%m-%d %H:%M:%S} {c['file']}")
            print('   stances at the pull: ' + '; '.join(f"{nm(i)} {c['stance0'].get(i, 'none')}" for i in tanks))
            ev = [(r, f"{nm(w)}: {what}") for r, w, what in c['ev'] if r <= a.to and w in tanks]
            ev += [(r, f"Kefka autos -> {nm(w)}") for r, w in c['autos'] if r <= a.to]
            ev += [(x['t'], f"Revolting Ruin III #{k + 1} cast at {nm(x['tgt'])}; hits " +
                    ', '.join(f"+{d} {nm(w)}" for d, w, _ in x['hit'])) for k, x in enumerate(c['rr3']) if x['t'] <= a.to]
            ev += [(x['t'], f"Ultimate Embrace #{k + 1} cast at {nm(x['tgt'])}; hits " +
                    ', '.join(f"+{d} {nm(w)}" + (f" [{'/'.join(inv)}]" if inv else '') for d, w, inv in x['hit'])) for k, x in enumerate(c['ue'][:1])]
            for r, s in sorted(ev):
                print(f"   {r:7.1f}  {s}")


if __name__ == '__main__':
    main()
