#!/usr/bin/env python3
"""How Dancing Mad's Phase 1 ends: Kefka's HP at the end of the phase, whether the pull went on to Phase 2, and what was
cast last in pulls that ended there (2026-11-14). Answers whether damage in Phase 1's last seconds matters (an HP check at
the transition) or Phase 1 ends on a timer whatever HP is left.

  dmu_p1_end.py <ACT log or folder> [...] [--since 20260920]

Per pull: Kefka's HP % (the last HP seen on Phase 1's Kefka from any hit) at 185, 190 and 195 s and at his last hit, the time
of that last hit, whether Phase 2 came (Ultimate Embrace cast after 200 s), the pull's end, and the last enemy cast.
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
    since, paths = None, []
    i = 0
    while i < len(args):
        if args[i] == '--since':
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
        for raw in open(log, 'rb'):
            p = raw[:4]
            if p.startswith(b'01|'):
                zone = raw.split(b'|')[2].decode()
                continue
            if zone != '553':
                continue
            if p == b'260|':
                l = raw.decode().split('|')
                if since and l[1][:10].replace('-', '') < since:
                    continue
                if l[3] == '1' and T is None:
                    T = ts(l[1])
                    cur = {'T': T, 'hp': [], 'p2': False, 'last': None, 'kefka': None}
                elif l[3] == '0' and T is not None:
                    cur['end'] = (ts(l[1]) - T).total_seconds()
                    rows.append(cur)
                    T = None
                continue
            if T is None:
                continue
            if p.startswith(b'20|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[2][:1] == '4':
                    r = (ts(l[1]) - T).total_seconds()
                    cur['last'] = (r, l[3], l[5])
                    if l[5] == 'Ultimate Embrace' and r > 200:
                        cur['p2'] = True
            elif p.startswith(b'21|') or p.startswith(b'22|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[7] == 'Kefka' and l[6][:1] == '4' and l[24] and l[25]:
                    r = (ts(l[1]) - T).total_seconds()
                    if r > 230:
                        continue
                    if cur['kefka'] is None:
                        cur['kefka'] = l[6]
                    if l[6] == cur['kefka']:
                        try:
                            cur['hp'].append((r, 100 * int(l[24]) / int(l[25])))
                        except (ValueError, ZeroDivisionError):
                            pass
    def at(hp, t):
        v = [h for r, h in hp if r <= t]
        return v[-1] if v else None
    groups = collections.defaultdict(list)
    for c in rows:
        if not c['hp'] or c['hp'][-1][0] < 180:
            continue
        last_t, last_hp = c['hp'][-1]
        if c['p2']:
            g = 'reached Phase 2'
        elif 190 <= c['end'] <= 215:
            g = 'ended 190-215 s in Phase 1'
        else:
            continue
        groups[g].append((at(c['hp'], 185), at(c['hp'], 190), at(c['hp'], 195), last_hp, last_t, c['end'], c['last']))
    for g, v in groups.items():
        print(f'== {g}: {len(v)} pulls')
        for k, name in ((0, '185 s'), (1, '190 s'), (2, '195 s'), (3, 'last hit')):
            xs = sorted(x[k] for x in v if x[k] is not None)
            if xs:
                print(f'   Kefka HP at {name:8}: median {xs[len(xs) // 2]:5.1f}%  p10 {xs[len(xs) // 10]:5.1f}  p90 {xs[9 * len(xs) // 10]:5.1f}  '
                      f'min {xs[0]:5.1f}  max {xs[-1]:5.1f}')
        lt = sorted(x[4] for x in v)
        print(f'   last hit on Phase 1 Kefka at: median {lt[len(lt) // 2]:.1f} s (p10 {lt[len(lt) // 10]:.1f}, p90 {lt[9 * len(lt) // 10]:.1f})')
        if g.startswith('ended'):
            ends = collections.Counter(f'{x[6][2]} ({x[6][1]})' for x in v if x[6])
            print('   last enemy cast before the end:', ', '.join(f'{k} {n}' for k, n in ends.most_common(6)))
            zero = sum(1 for x in v if x[3] is not None and x[3] < 0.5)
            print(f'   Kefka at 0% at his last hit: {zero} of {len(v)}')
            loj = sorted(x[3] for x in v if x[6] and x[6][2] == 'Light of Judgment' and x[6][0] >= 195 and x[3] is not None)
            if loj:
                print(f'   ended by a Light of Judgment cast at 195 s or later (the enrage): {len(loj)} pulls, Kefka at '
                      f'min {loj[0]:.1f}%  p10 {loj[len(loj) // 10]:.1f}  median {loj[len(loj) // 2]:.1f}')
                print('   lowest ten:', ' '.join(f'{h:.1f}' for h in loj[:10]))
        else:
            hi = sorted((x[3] for x in v if x[3] is not None), reverse=True)
            print('   highest ten at the last hit:', ' '.join(f'{h:.1f}' for h in hi[:10]))


if __name__ == '__main__':
    main()
