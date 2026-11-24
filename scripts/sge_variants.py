#!/usr/bin/env python3
"""Compare Sage replay runs of rule variants (2026-11-18): the summaries of `RuleSim.dll run <log> ... --party`, one folder per variant.

  sge_variants.py base=<dir> A=<dir> [...] [--hits]

Each folder holds one output file per log (same logs in each). Per variant: active minutes, potency per minute (weighted by active
minutes), GCDs per minute, Eukrasian Diagnosis and Eukrasian Prognosis II presses per 10 minutes (rule fires), tanks and other players
the rules would lose who lived (the TankMetrics counterfactual), and player-seconds under 30% / 15%. --hits lists the hits behind the
non-tank losses per variant.
"""
import collections
import glob
import os
import re
import sys

args = sys.argv[1:]
hits = '--hits' in args
variants = [a.split('=', 1) for a in args if '=' in a]


def read(d):
    t = collections.Counter(); fires = collections.Counter(); lose = collections.Counter()
    for f in sorted(glob.glob(os.path.join(d, '*.txt'))):
        s = open(f, encoding='utf-8', errors='replace').read()
        m = re.search(r'===== (\d+) pulls, ([\d.]+) active minutes', s)
        if not m:
            continue
        mins = float(m.group(2)); t['pulls'] += int(m.group(1)); t['min'] += mins
        if (p := re.search(r'potency per minute: rules (\d+)', s)): t['pot'] += int(p.group(1)) * mins
        if (g := re.search(r'GCDs per minute: rules ([\d.]+)', s)): t['gcd'] += float(g.group(1)) * mins
        if (k := re.search(r'tanks the rules would lose that lived (\d+)', s)): t['tankLose'] += int(k.group(1))
        if (q := re.search(r'players the rules would lose that lived (\d+); player-seconds under 30%: logged (\d+) rules (\d+); under 15%: logged (\d+) rules (\d+)', s)):
            t['partyLose'] += int(q.group(1)); t['u30'] += int(q.group(3)); t['u15'] += int(q.group(5))
        # 25% (2026-11-24): time and dips under 25%, non-tanks and tanks.
        if (q := re.search(r'party \(non-tanks\) under 25%: player-seconds logged (\d+) rules (\d+); dips logged (\d+) rules (\d+)', s)):
            t['p25'] += int(q.group(2)); t['pDips'] += int(q.group(4)); t['p25log'] += int(q.group(1)); t['pDipsLog'] += int(q.group(3))
        if (q := re.search(r'tanks under 25%: tank-seconds logged (\d+) rules (\d+); dips logged (\d+) rules (\d+)', s)):
            t['t25'] += int(q.group(2)); t['tDips'] += int(q.group(4)); t['t25log'] += int(q.group(1)); t['tDipsLog'] += int(q.group(3))
        for x in re.finditer(r'     party lose x(\d+) (.*?):', s):
            lose[x.group(2) or '(unnamed)'] += int(x.group(1))
        if (r := re.search(r'rule fires: (.*)', s)):
            for part in r.group(1).split('; '):
                mm = re.match(r'(.*) (\d+)$', part.strip())
                if mm:
                    fires[mm.group(1)] += int(mm.group(2))
    return t, fires, lose


rows = []
for name, d in variants:
    t, fires, lose = read(d)
    ediag = sum(n for k, n in fires.items() if k.endswith(('Eukrasian Diagnosis on a tank', 'Eukrasian Diagnosis (spare Eukrasia)', 'Eukrasian Diagnosis on a tank in danger',
                                                           'Eukrasian Diagnosis on a tank (no target)', 'Eukrasian Diagnosis on the boss\'s target (Addersting below the tank shield count)')) or 'Eukrasian Diagnosis' in k)
    eprog = sum(n for k, n in fires.items() if 'Eukrasian Prognosis' in k)
    dosis = sum(n for k, n in fires.items() if k.endswith(': Eukrasian Dosis'))
    rows.append((name, t, ediag, eprog, dosis, lose))
base = rows[0][1]
print(f"{'variant':14} {'pulls':>5} {'min':>6} {'pot/min':>8} {'vs base':>8} {'GCD/min':>8} {'EDiag/10m':>9} {'EProg/10m':>9} {'EDosis/10m':>10} {'tanks lost':>10} {'others lost':>11} {'s<30%':>7} {'s<15%':>7} {'party s<25%':>11} {'dips':>5} {'tank s<25%':>10} {'dips':>5}")
for name, t, ediag, eprog, dosis, lose in rows:
    m = max(t['min'], 1e-9); pot = t['pot'] / m
    print(f"{name:14} {t['pulls']:5} {t['min']:6.0f} {pot:8.0f} {100 * (pot / (base['pot'] / base['min']) - 1):+7.2f}% {t['gcd'] / m:8.2f} {10 * ediag / m:9.1f} {10 * eprog / m:9.1f} {10 * dosis / m:10.1f} "
          f"{t['tankLose']:10} {t['partyLose']:11} {t['u30']:7} {t['u15']:7} {t['p25']:11} {t['pDips']:5} {t['t25']:10} {t['tDips']:5}")
if rows and rows[0][1]['p25log']:
    b = rows[0][1]
    print(f"{'(logged)':14} {'':5} {'':6} {'':8} {'':8} {'':8} {'':9} {'':9} {'':10} {'':10} {'':11} {'':7} {'':7} {b['p25log']:11} {b['pDipsLog']:5} {b['t25log']:10} {b['tDipsLog']:5}")
if hits:
    for name, t, ediag, eprog, dosis, lose in rows:
        print(f"\n{name}: non-tank losses by hit: " + ', '.join(f"{k} {n}" for k, n in lose.most_common()))
