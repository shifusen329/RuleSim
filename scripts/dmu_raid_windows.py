#!/usr/bin/env python3
"""The party's 2-minute raid-buff windows in Dancing Mad (Ultimate) pulls, and where an action (No Mercy by default) landed
against them (2026-11-14).

  dmu_raid_windows.py <ACT log or folder> [...] [--since 20260920] [--action "No Mercy"] [--me 10041112,1001A4A8]
                      [--list] [--csv out.csv]

A window starts with the first raid buff a player puts out (Divination, Battle Litany, Brotherhood, Arcane Circle, Searing
Light, Starry Muse, Embolden, Technical Finish, Battle Voice, Radiant Finale; Chain Stratagem, Dokumori or Mug on an
enemy) more than 30 s after the last window started. Times are seconds into the pull (the game's combat start), with the
phase markers a pull reached: P2 = the first Forsaken, P3 = the first Definition of Insanity, P4 = the first Kefka Says,
P5 = the first Ultima Repeater... (as cast). For every player who used --action: each use, the window it fell in or the
nearest one, and the offset (use - window start). The totals cluster the windows across pulls by phase and time.
"""
import collections
import csv
import datetime as dt
import os
import re
import sys

BUFFS = {'Divination', 'Battle Litany', 'Brotherhood', 'Arcane Circle', 'Searing Light', 'Starry Muse', 'Embolden',
         'Technical Finish', 'Battle Voice', 'Radiant Finale', 'Chain Stratagem', 'Dokumori', 'Mug'}
MARKS = [('P2', 'Forsaken'), ('P3', 'Definition of Insanity'), ('P4', 'Kefka Says'), ('P5', 'Ultima Repeater')]
JOBS = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB'}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def scan(path, action, since):
    pulls = []
    zone = None
    jobs = {}
    cur = None
    act = f'|{action}|'.encode()
    with open(path, 'rb') as fh:
        for raw in fh:
            p = raw[:4]
            if p.startswith(b'01|'):
                zone = raw.split(b'|')[2].decode()
                continue
            if p.startswith(b'03|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if len(l) > 4 and l[2][:1] == '1':
                    jobs[l[2]] = int(l[4], 16)
                continue
            if zone != '553':
                continue
            if p == b'260|':
                l = raw.decode().split('|')
                if since and l[1][:10].replace('-', '') < since:
                    continue
                if l[3] == '1' and cur is None:
                    cur = {'T': ts(l[1]), 'windows': [], 'uses': collections.defaultdict(list), 'marks': {}, 'names': {},
                           'jobs': dict(jobs), 'path': path}
                elif l[3] == '0' and cur is not None:
                    cur['dur'] = (ts(l[1]) - cur['T']).total_seconds()
                    pulls.append(cur)
                    cur = None
                continue
            if cur is None:
                continue
            if p.startswith(b'26|'):
                l = raw.decode('utf-8', 'replace').split('|')
                if l[3] in BUFFS and l[5][:1] == '1':
                    r = (ts(l[1]) - cur['T']).total_seconds()
                    if not cur['windows'] or r - cur['windows'][-1][0] > 30:
                        cur['windows'].append([r, set()])
                    cur['windows'][-1][1].add(l[3])
            elif p.startswith(b'20|'):
                l = raw.decode('utf-8', 'replace').split('|')
                for m, name in MARKS:
                    if l[5] == name and m not in cur['marks'] and l[2][:1] == '4':
                        cur['marks'][m] = (ts(l[1]) - cur['T']).total_seconds()
            elif (p.startswith(b'21|') or p.startswith(b'22|')) and act in raw:
                l = raw.decode('utf-8', 'replace').split('|')
                if l[5] == action and l[2][:1] == '1':
                    r = (ts(l[1]) - cur['T']).total_seconds()
                    u = cur['uses'][l[2]]
                    if not u or r - u[-1] > 5:
                        u.append(r)
                    cur['names'][l[2]] = l[3]
    return pulls


def phase(p, r):
    ph = 'P1'
    for m, _ in MARKS:
        if m in p['marks'] and r >= p['marks'][m] - 15:
            ph = m
    return ph


def main():
    args = sys.argv[1:]
    action, since, me, show, out = 'No Mercy', None, {'10041112', '1001A4A8'}, False, None
    paths = []
    i = 0
    while i < len(args):
        a = args[i]
        if a in ('--action', '--since', '--me', '--csv'):
            v = args[i + 1]
            i += 2
            if a == '--action':
                action = v
            elif a == '--since':
                since = v
            elif a == '--me':
                me = set(v.split(','))
            else:
                out = v
        elif a == '--list':
            show = True
            i += 1
        else:
            paths.append(a)
            i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    pulls = []
    for p in logs:
        pulls += scan(p, action, since)
    rows = []
    clusters = collections.defaultdict(list)
    for k, p in enumerate(pulls):
        wins = [w[0] for w in p['windows']]
        for w in wins:
            ph = phase(p, w)
            base = p['marks'].get(ph, 0) if ph != 'P1' else 0
            clusters[(ph, round((w - base) / 10) * 10)].append(w - base)
        if show:
            marks = ' '.join(f"{m} {t:.0f}" for m, t in sorted(p['marks'].items(), key=lambda x: x[1]))
            print(f"#{k} {p['T']:%m-%d %H:%M} {p['dur']:5.0f}s  marks: {marks or '-'}  windows: " + ', '.join(f'{w:.1f}' for w in wins))
        for pid, uses in p['uses'].items():
            for u in uses:
                if not wins:
                    continue
                w = min(wins, key=lambda x: abs(u - x))
                inside = any(0 <= u - x <= 20 for x in wins)
                rows.append({'pull': k, 'when': f"{p['T']:%Y-%m-%d %H:%M}", 'player': p['names'][pid], 'me': pid in me,
                             'job': JOBS.get(p['jobs'].get(pid, 0), '?'), 'use': round(u, 1), 'phase': phase(p, u),
                             'window': round(w, 1), 'offset': round(u - w, 1), 'inside': inside})
                if show:
                    print(f"     {action} {'YOU ' if pid in me else ''}{p['names'][pid]}: {u:.1f} ({phase(p, u)}), nearest window "
                          f"{w:.1f}, offset {u - w:+.1f}{'' if inside else '  OUTSIDE'}")
    print(f"pulls {len(pulls)}; raid windows by phase (seconds after the phase's first marker cast; P1 from the pull):")
    for (ph, b), xs in sorted(clusters.items(), key=lambda x: (x[0][0], x[0][1])):
        if len(xs) >= 3:
            xs.sort()
            print(f"   {ph} ~{b:4d}s: {len(xs):4d} windows, median {xs[len(xs) // 2]:.1f}, p10 {xs[len(xs) // 10]:.1f}, p90 {xs[9 * len(xs) // 10]:.1f}")
    for who in ('you', 'others'):
        sel = [r for r in rows if r['me'] == (who == 'you')]
        if not sel:
            continue
        by = collections.defaultdict(list)
        for r in sel:
            by[r['phase']].append(r)
        print(f"{action} ({who}): {len(sel)} uses, {sum(r['inside'] for r in sel)} inside a window (0-20 s after its start)")
        for ph in sorted(by):
            offs = sorted(r['offset'] for r in by[ph])
            print(f"   {ph}: {len(offs)} uses, inside {sum(r['inside'] for r in by[ph])}, offset to the nearest window median "
                  f"{offs[len(offs) // 2]:+.1f} (p10 {offs[len(offs) // 10]:+.1f}, p90 {offs[9 * len(offs) // 10]:+.1f})")
    if out:
        with open(out, 'w', newline='', encoding='utf-8') as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()) if rows else ['pull'])
            w.writeheader()
            w.writerows(rows)


if __name__ == '__main__':
    main()
