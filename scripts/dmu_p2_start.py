#!/usr/bin/env python3
"""Dancing Mad's Phase 1 to Phase 2 transition, timed from fixed casts (2026-11-14).

  dmu_p2_start.py <ACT log or folder> [...] [--since 20261020] [--actions [--me 10041112,1001A4A8]]

For pulls that reached Phase 2: Revolting Ruin III #1 and #2's cast targets (the tanks the sheet calls MT and OT), whether
they were different players, Phase 1's third Mystery Magic (its cast start), the last hit on Phase 1's Kefka, when Phase 2's
Kefka became targetable (ACT 34 line), the first Ultimate Embrace's cast start and hit, and each as seconds after that
Mystery Magic, so a rule keyed on it can time the transition. --actions also lists, per pull, your actions (with their
targets) and the statuses you put on tanks from 14 s to 40 s after that Mystery Magic.
"""
import collections
import datetime as dt
import os
import re
import sys


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def main():
    args = sys.argv[1:]
    since, paths, acts, me = None, [], False, {'10041112', '1001A4A8'}
    i = 0
    while i < len(args):
        if args[i] == '--actions':
            acts = True
            i += 1
        elif args[i] == '--me':
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
    rows = []
    for log in logs:
        zone, T, cur = None, None, None
        names = {}
        for raw in open(log, 'rb'):
            p = raw[:4]
            if p.startswith(b'01|'):
                zone = raw.split(b'|')[2].decode()
                continue
            if p.startswith(b'03|'):
                l = raw.decode('utf-8', 'replace').split('|')
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
                    cur = {'rr3': [], 'mm': [], 'ue': None, 'uehit': None, 'p1last': None, 'targetable': None, 'p1kefka': None, 'T': T, 'mine': []}
                elif l[3] == '0' and T is not None:
                    if cur['ue'] is not None and len(cur['mm']) >= 3 and len(cur['rr3']) >= 2:
                        rows.append(cur)
                    T = None
                continue
            if T is None:
                continue
            r = None
            if p.startswith(b'20|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[2][:1] != '4':
                    continue
                r = (ts(l[1]) - T).total_seconds()
                if l[5] == 'Revolting Ruin III' and (not cur['rr3'] or r - cur['rr3'][-1][0] > 2):
                    cur['rr3'].append((r, names.get(l[6], l[7])))
                elif l[5] == 'Mystery Magic' and (not cur['mm'] or r - cur['mm'][-1] > 2):
                    cur['mm'].append(r)
                elif l[5] == 'Ultimate Embrace' and cur['ue'] is None and r > 190:
                    cur['ue'] = r
            elif acts and (p.startswith(b'26|') or p.startswith(b'30|')):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[5] in me and l[7] != l[5] and l[3] in ('Taurochole', 'Haima', 'Eukrasian Diagnosis', 'Kardion', 'Haimatinon'):
                    cur['mine'].append(((ts(l[1]) - T).total_seconds(), f"{'+' if p.startswith(b'26|') else '-'}{l[3]} on {l[8]}"))
                continue
            elif p.startswith(b'21|') or p.startswith(b'22|'):
                l = raw.decode('utf-8', 'replace').split('|')
                r = (ts(l[1]) - T).total_seconds()
                if acts and l[2] in me and (not cur['mine'] or cur['mine'][-1][1] != f'press {l[5]} -> {l[7]}' or r - cur['mine'][-1][0] > 0.3):
                    cur['mine'].append((r, f'press {l[5]} -> {l[7]}'))
                if l[7] == 'Kefka' and l[6][:1] == '4' and l[2][:1] == '1' and r < 205:
                    if cur['p1kefka'] is None:
                        cur['p1kefka'] = l[6]
                    if l[6] == cur['p1kefka']:
                        cur['p1last'] = r
                elif l[5] == 'Ultimate Embrace' and cur['ue'] is not None and cur['uehit'] is None:
                    cur['uehit'] = r
            elif p.startswith(b'34|'):
                l = raw.decode('utf-8', 'replace').split('|')
                r = (ts(l[1]) - T).total_seconds()
                if l[3] == 'Kefka' and l[6].strip() in ('01', '1') and 200 < r < 230 and cur['targetable'] is None:
                    cur['targetable'] = r
    if acts:
        for c in rows:
            mm3 = c['mm'][2]
            print(f"== {c['T']:%Y-%m-%d %H:%M:%S}  Revolting Ruin III #1 -> {c['rr3'][0][1]}, #2 -> {c['rr3'][1][1]}; third Mystery Magic "
                  f"{mm3:.1f} s; Kefka targetable +{(c['targetable'] or 0) - mm3:.1f}; Ultimate Embrace cast +{c['ue'] - mm3:.1f}, hit "
                  f"+{(c['uehit'] or 0) - mm3:.1f}")
            for r, s in c['mine']:
                if 14 <= r - mm3 <= 40 and 'attack' not in s:
                    print(f'   +{r - mm3:5.1f}  {s}')
    print(f'pulls that reached Phase 2 with both Revolting Ruin IIIs: {len(rows)}')
    same = sum(1 for c in rows if c['rr3'][0][1] == c['rr3'][1][1])
    print(f'   Revolting Ruin III #1 and #2 cast at the same player: {same} of {len(rows)}')
    stats = collections.defaultdict(list)
    for c in rows:
        mm3 = c['mm'][2]
        stats['third Mystery Magic (s into the pull)'].append(mm3)
        if c['p1last'] is not None:
            stats['last hit on Phase 1 Kefka, after it'].append(c['p1last'] - mm3)
        if c['targetable'] is not None:
            stats["Phase 2 Kefka targetable, after it"].append(c['targetable'] - mm3)
        stats['first Ultimate Embrace cast, after it'].append(c['ue'] - mm3)
        if c['uehit'] is not None:
            stats['its hit, after it'].append(c['uehit'] - mm3)
    for k, v in stats.items():
        v.sort()
        print(f'   {k:40} median {v[len(v) // 2]:6.1f}  min {v[0]:6.1f}  p10 {v[len(v) // 10]:6.1f}  p90 {v[9 * len(v) // 10]:6.1f}  max {v[-1]:6.1f}  (n {len(v)})')


if __name__ == '__main__':
    main()
