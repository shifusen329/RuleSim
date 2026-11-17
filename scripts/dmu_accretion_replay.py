#!/usr/bin/env python3
"""Replay a healer plan through Dancing Mad's Earthquake Accretions, two rule folders side by side (2026-11-17).

  dmu_accretion_replay.py <job: sge|whm|ast> <me HEX[,HEX]> <rulesA> <rulesB> <LOG|EQ time> [<LOG|EQ time> ...]

Each argument after the folders is "<ACT log path>|<Earthquake cast time as the log prints it, YYYY-MM-DD HH:MM:SS>" (as
dmu_accretion_order.py --list prints them). The pull holding that time is found with RuleSim's pulls listing, and the
timeline from 1 s before the cast to 25 s after is replayed with each folder; the rules that fire on an Accretion holder
(rule names "on the healer's / DPS's Accretion", the old "on First / Second in Line's Accretion", Microcosmos "for the last Accretion") are printed per folder, then the totals.
Run from anywhere; RuleSim must be built (src/RuleSim/bin/Release/net10.0).
"""
import collections
import datetime as dt
import os
import re
import subprocess
import sys

BIN = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'RuleSim', 'bin', 'Release', 'net10.0')


def sim(*args):
    return subprocess.run(['dotnet', 'RuleSim.dll', *args], cwd=BIN, capture_output=True, text=True).stdout


def pulls(log, cache={}):
    if log not in cache:
        rows = []
        for line in sim('pulls', log, '1-5000').splitlines():
            m = re.match(r'#(\d+)\s+(\d\d)-(\d\d) (\d\d:\d\d:\d\d)\s+\S+\s+([\d.]+)s', line)
            if m:
                rows.append((int(m.group(1)), m.group(2) + '-' + m.group(3) + ' ' + m.group(4), float(m.group(5))))
        cache[log] = rows
    return cache[log]


def main():
    job, mes, ra, rb, specs = sys.argv[1], sys.argv[2].split(','), sys.argv[3], sys.argv[4], sys.argv[5:]
    totals = {ra: collections.Counter(), rb: collections.Counter()}
    for spec in specs:
        log, t = spec.split('|')
        T = dt.datetime.strptime(t, '%Y-%m-%d %H:%M:%S')
        hit = None
        for n, start, dur in pulls(log):
            s = dt.datetime.strptime(f'{T.year}-{start}', '%Y-%m-%d %H:%M:%S')
            if s <= T <= s + dt.timedelta(seconds=dur):
                hit = (n, (T - s).total_seconds())
        if not hit:
            print(f'{t}: no pull found in {os.path.basename(log)}')
            continue
        n, off = hit
        print(f'=== {t} ({os.path.basename(log)} pull #{n}, Earthquake at {off:.0f} s)')
        for rules in (ra, rb):
            out = ''
            for me in mes:
                args = ['timeline', log, str(n), f'--me={me}', f'--from={off - 1:.0f}', f'--to={off + 25:.0f}', f'--rules={rules}']
                if job != 'sge':
                    args.append(f'--job={job}')
                out = sim(*args)
                if 'RULES' in out:
                    break
            fired = [l.strip() for l in out.splitlines() if 'RULES' in l and re.search(r"on (the healer's|the DPS's|First in Line's|Second in Line's) Accretion|for the last Accretion", l)]
            print(f'  {os.path.basename(rules.rstrip("/")) or rules}: ' + ('none' if not fired else ''))
            for l in fired:
                print('    ' + l[:160])
                totals[rules][re.sub(r'.*\((?:DMU mitigation plan \([^)]*\)|plan): ', '', l).rstrip(')')] += 1
    for rules in (ra, rb):
        print(f'{rules}: {sum(totals[rules].values())} presses on a holder: {dict(totals[rules])}')


if __name__ == '__main__':
    main()
