#!/usr/bin/env python3
"""Addersgall cap burns next to a real Addersgall use, in the Sage replay (2026-11-18).

  GALL_LOG=1 dotnet RuleSim.dll run <log> <pulls> --me=<id> > out.txt   (once per log; several files may be given)
  sge_gall_burns.py out.txt [...] [--window 2] [--list 60]

A "burn" is a press by one of SGE_ST_DPS.json's cap rules (rule name with "capping" or "before it caps"); every other Addersgall
spend (Druochole, Taurochole, Ixochole, Kerachole by any other rule, the Dancing Mad plan's included) is a real use. Counts the
burns with a real use within --window seconds before or after (and which side), per rule, and lists them.
"""
import collections
import re
import sys

args = sys.argv[1:]
win = 2.0
show = 60
if '--list' in args:
    i = args.index('--list'); show = int(args[i + 1]); del args[i:i + 2]
if '--window' in args:
    i = args.index('--window'); win = float(args[i + 1]); del args[i:i + 2]
spends = collections.defaultdict(list)    # (file, pull) -> [(t s, action, rule)]
for path in args:
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.match(r'\s+gall (\d+) (-?\d+) (\S+) \| (.*)', line)
        if m:
            spends[(path, int(m.group(1)))].append((int(m.group(2)) / 1000, m.group(3), m.group(4).strip()))


def burn(rule):
    return 'capping' in rule or 'before it caps' in rule


total = collections.Counter(); near = collections.Counter(); side = collections.Counter(); real_n = 0
examples = []
for key, l in spends.items():
    l.sort()
    real = [x for x in l if not burn(x[2])]
    real_n += len(real)
    for t, act, rule in l:
        if not burn(rule):
            continue
        name = rule.split(': ', 1)[-1]
        total[name] += 1
        close = [r for r in real if abs(r[0] - t) <= win]
        if close:
            near[name] += 1
            r = min(close, key=lambda r: abs(r[0] - t))
            side['real use after the burn' if r[0] > t else 'real use before the burn'] += 1
            examples.append((key[1], t, act, name, r[0] - t, r[1], r[2].split(': ', 1)[-1]))
pulls = len(spends)
print(f"{pulls} pulls; {sum(total.values())} cap burns, {real_n} real Addersgall uses")
print(f"burns with a real use within {win:g} s: {sum(near.values())} of {sum(total.values())}  ({dict(side)})")
for name in total:
    print(f"  {name}: {near[name]} of {total[name]}")
by_real = collections.Counter((e[5], e[6]) for e in examples)
print("by the real use that followed:")
for (a, r), n in by_real.most_common():
    print(f"  {n:4} {a} ({r})")
gaps = collections.Counter(f"{int(abs(e[4]))}-{int(abs(e[4])) + 1} s" for e in examples)
print("gap:", dict(sorted(gaps.items())))
for p, t, act, name, dt, ract, rrule in sorted(examples)[:show]:
    m, s = divmod(t, 60)
    print(f"  pull {p:3} {int(m)}:{s:04.1f} {act:10} ({name}) -> real {ract} {dt:+.1f} s ({rrule})")
