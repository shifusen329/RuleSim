#!/usr/bin/env python3
"""Your presses through Dancing Mad's Phase 1 to Phase 2 gap, from AnoMech's press log (2026-11-14).

  dmu_p2_presses.py <ACT log> <dalamud.log> [<dalamud.old.log> ...] [--since 20261114] [--from 14] [--to 31]
                    [--offset 1.4] [--me 10041112,1001A4A8]

For each pull that reached Phase 1's third Mystery Magic: every line AnoMech's LocalPlayerInputHooks wrote from --from to
--to seconds after that cast's start (runs of custom-button presses collapsed), next to your own actions and the statuses
you put on the tanks in the ACT log, all as seconds after the cast. Answers "did the button get pressed?" when a rule
seems not to fire.

Reading AnoMech's lines (it hooks UseAction inside Wrath's hook):
  - "Action pressed: 1000000" / "1000001": the Single Target / AoE DPS custom action handed to the game.
  - A press that a rule turns into Wait (Cease) never reaches the game: nothing is logged. A gap in the lines while you
    held the button down is Wait.
  - A custom-action press with nothing hostile targeted that turns into an attack (Dosis, Toxikon, Eukrasian Dosis) is
    handed to the game as that attack, so it is logged under the attack's name, not 1000000.
  - The game firing a queued action logs it by name, right after the press that queued it.
  - Any other action logged by name with no custom-action press in the 1.6 s before it (marked MANUAL) was pressed on
    its own key or macro, not by the rules (2026-11-14: a manual Eukrasia at +23.4 to +23.8 s, then no presses until
    Kefka was back, put Taurochole and Haima after his return).
Dalamud's clock reads about 1.4 s later than ACT's for the same action (--offset).
"""
import datetime as dt
import os
import re
import sys

CUSTOM = {1000000: 'ST', 1000001: 'AoE', 1000002: 'ST heal', 1000003: 'AoE heal'}
ATTACKS = {'Dosis', 'Dosis II', 'Dosis III', 'Eukrasian Dosis', 'Eukrasian Dosis II', 'Eukrasian Dosis III', 'Toxikon',
           'Toxikon II', 'Phlegma', 'Phlegma II', 'Phlegma III', 'Dyskrasia', 'Dyskrasia II', 'Eukrasian Dyskrasia', 'Pneuma',
           'Psyche'}
MINE = {'Taurochole', 'Haima', 'Eukrasian Diagnosis', 'Druochole', 'Kerachole', 'Eukrasia', 'Eukrasian Dyskrasia',
        'Eukrasian Dosis III', 'Rhizomata', 'Egeiro', 'Swiftcast', 'Kardia', 'Ixochole', 'Physis II', 'Zoe', 'Holos',
        'Panhaima', 'Krasis', 'Pepsis', 'Soteria', 'Lucid Dreaming'}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def pulls(log, since, me):
    zone, T, out, cur = None, None, [], None
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
                cur = {'T': T, 'mm': [], 'act': []}
            elif l[3] == '0' and T is not None:
                if len(cur['mm']) >= 3:
                    out.append(cur)
                T = None
            continue
        if T is None:
            continue
        if p.startswith(b'20|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[5] == 'Mystery Magic' and l[2][:1] == '4':
                t = ts(l[1])
                if not cur['mm'] or (t - cur['mm'][-1]).total_seconds() > 2:
                    cur['mm'].append(t)
        elif p.startswith(b'21|') or p.startswith(b'22|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[2] in me and l[5] in MINE:
                s = f'{l[5]} -> {l[7]}'
                if not cur['act'] or cur['act'][-1][1] != s:
                    cur['act'].append((ts(l[1]), s))
        elif p.startswith(b'34|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[3] == 'Kefka' and len(cur['mm']) >= 3:
                cur['act'].append((ts(l[1]), f"Kefka {'targetable' if l[6].strip() in ('01', '1') else 'untargetable'}"))
    return out


def main():
    args = sys.argv[1:]
    since, lo, hi, off, me, paths = None, 14.0, 31.0, 1.4, {'10041112', '1001A4A8'}, []
    i = 0
    while i < len(args):
        a = args[i]
        if a in ('--since', '--from', '--to', '--offset', '--me'):
            v = args[i + 1]
            i += 2
            if a == '--since':
                since = v
            elif a == '--from':
                lo = float(v)
            elif a == '--to':
                hi = float(v)
            elif a == '--offset':
                off = float(v)
            else:
                me = set(v.split(','))
        else:
            paths.append(a)
            i += 1
    act, dlogs = paths[0], paths[1:]
    presses = []
    for f in dlogs:
        if not os.path.exists(f):
            continue
        for line in open(f, encoding='utf-8', errors='replace'):
            if '[LocalPlayerInputHooks]' not in line:
                continue
            m = re.search(r'Action pressed: (\d+) \(Action\) -- (.*?) \(job', line)
            if not m:
                continue
            presses.append((dt.datetime.fromisoformat(line[:23]) - dt.timedelta(seconds=off), int(m.group(1)), m.group(2)))
    presses.sort()
    for c in pulls(act, since, me):
        mm3 = c['mm'][2]
        print(f"== pull {c['T']:%Y-%m-%d %H:%M:%S}  third Mystery Magic {(mm3 - c['T']).total_seconds():.1f} s")
        rows = []
        for t, s in c['act']:
            r = (t - mm3).total_seconds()
            if lo <= r <= hi:
                rows.append((r, 1, f'ACT      {s}'))
        run = None
        for k, (t, a, n) in enumerate(presses):
            r = (t.replace(tzinfo=None) - mm3).total_seconds()
            if not lo <= r <= hi:
                continue
            if a in CUSTOM:
                if run and run[2] == a and r - run[1] < 1.0:
                    run[1], run[3] = r, run[3] + 1
                else:
                    if run:
                        rows.append((run[0], 0, f'press    {CUSTOM[run[2]]} x{run[3]}' + (f' (to +{run[1]:.1f})' if run[3] > 1 else '')))
                    run = [r, r, a, 1]
                continue
            before = [x for x in presses[max(0, k - 15):k] if 0 <= (t - x[0]).total_seconds() <= 1.6]
            how = 'queued/rule' if any(x[1] in CUSTOM for x in before) else 'MANUAL' if n not in ATTACKS else 'custom, no target'
            rows.append((r, 0, f'named    {n} ({how})'))
        if run:
            rows.append((run[0], 0, f'press    {CUSTOM[run[2]]} x{run[3]}' + (f' (to +{run[1]:.1f})' if run[3] > 1 else '')))
        for r, _, s in sorted(rows):
            print(f'   +{r:5.1f}  {s}')


if __name__ == '__main__':
    main()
