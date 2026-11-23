#!/usr/bin/env python3
"""The user's Gunbreaker cartridges, pull by pull (2026-11-23, user: "why are GNB cartridges being held and going unused?").

  gnb_carts.py <ACT log> [...] [--me=HEX,HEX] [--since=2026-11-23T11:00] [--list]

Reads the raw ACT lines. The gauge is line 31: data0 = job (0x25) | cartridges << 16 (byte 2; up to 6 under Bloodfest).
Each change of the count is matched to the user's action within 0.6 s: spenders Burst Strike / Gnashing Fang / Fated Circle
(-1), Double Down (-2); makers Solid Barrel / Demon Slaughter (+1), Bloodfest (+3). Per pull (line 260's in-game combat
flag, Dancing Mad or not):
  - made, spent, wasted to the cap (a maker that raised nothing), vanished (a drop with no spender: Bloodfest's extra ones
    running out), and held at the end (the count when combat ended: a wipe or the kill);
  - seconds spent at 1+, 2+ and 3+ cartridges outside No Mercy, and the longest stretch at 2+ without a spend;
  - with --list, every waste and vanish with the time and what was pressed around it.
"""
import collections
import sys
from datetime import datetime

args = [a for a in sys.argv[1:] if not a.startswith('--')]
opt = dict(a[2:].split('=', 1) for a in sys.argv[1:] if a.startswith('--') and '=' in a)
me = {x.upper() for x in opt.get('me', '1001A4A8,10041112').split(',')}
since = datetime.fromisoformat(opt['since']).timestamp() if 'since' in opt else 0
listing = '--list' in sys.argv
SPEND = {'Burst Strike': 1, 'Gnashing Fang': 1, 'Fated Circle': 1, 'Double Down': 2}
MAKE = {'Solid Barrel': 1, 'Demon Slaughter': 1, 'Bloodfest': 3}


def ts(s):
    return datetime.fromisoformat(s[:26] + s[27:]).timestamp()


class Pull:
    def __init__(self, t0, zone):
        self.t0 = t0; self.zone = zone; self.end = None
        self.gauge = []          # (t, carts)
        self.acts = []           # (t, name)
        self.nm = []             # No Mercy buff windows (start, end)


pulls = []
for path in args:
    zone = None; cur = None; in_combat = False; job = 0; nm_on = None
    for line in open(path, encoding='utf-8', errors='replace'):
        f = line.split('|')
        if len(f) < 4:
            continue
        typ = f[0]
        if typ == '01':
            zone = f[3]; continue
        if typ == '260':
            t = ts(f[1]); g = f[3] == '1'
            if g and not in_combat and t >= since:
                cur = Pull(t, zone); pulls.append(cur)
            elif not g and in_combat and cur is not None and cur.end is None:
                cur.end = t
            in_combat = g
            continue
        if cur is None or cur.end is not None:
            continue
        if typ == '31' and f[2].upper() in me:
            d0 = int(f[3], 16)
            job = d0 & 0xFF
            if job == 0x25:
                cur.gauge.append((ts(f[1]), (d0 >> 16) & 0xFF))
        elif typ in ('21', '22') and f[2].upper() in me and (f[5] in SPEND or f[5] in MAKE or f[5] in ('No Mercy', 'Solid Barrel')):
            t = ts(f[1])
            if not cur.acts or cur.acts[-1] != (round(t, 1), f[5]):
                cur.acts.append((round(t, 1), f[5]))
        elif typ == '26' and len(f) > 8 and f[3] == 'No Mercy' and f[7].upper() in me:
            nm_on = ts(f[1]); cur.nm.append([nm_on, nm_on + float(f[4])])
        elif typ == '30' and len(f) > 8 and f[3] == 'No Mercy' and f[7].upper() in me and cur.nm:
            cur.nm[-1][1] = ts(f[1])

pulls = [p for p in pulls if len(p.gauge) > 3 and p.end]
tot = collections.Counter()
print(f"{len(pulls)} Gunbreaker pulls")
print(f"{'start':16} {'zone':24} {'len':>5}  made spent wasted vanished heldEnd   s@1+ s@2+ s@3+ (outside No Mercy)  longest 2+ hold")
for p in pulls:
    made = spent = wasted = vanished = 0
    events = []
    # Collapse repeated readings, then attribute each change.
    g = [p.gauge[0]]
    for t, c in p.gauge[1:]:
        if c != g[-1][1]:
            g.append((t, c))
    used = set()
    for (t0, c0), (t1, c1) in zip(g, g[1:]):
        d = c1 - c0
        near = [(abs(t - t1), i, n) for i, (t, n) in enumerate(p.acts) if abs(t - t1) <= 0.6 and i not in used]
        near.sort()
        if d > 0:
            made += d
            for _, i, n in near:
                if n in MAKE:
                    used.add(i); break
        else:
            hit = next(((i, n) for _, i, n in near if n in SPEND), None)
            if hit:
                used.add(hit[0]); spent += -d
            else:
                vanished += -d
                events.append((t1 - p.t0, f"vanished {-d} ({c0} -> {c1})"))
    # Makers that raised nothing: the count was at the cap.
    for i, (t, n) in enumerate(p.acts):
        if n in MAKE and i not in used:
            before = max((c for tt, c in g if tt <= t - 0.05), default=None)
            cap = 6 if any(nn == 'Bloodfest' and 0 <= t - tt <= 30 for tt, nn in p.acts) else 3
            last = next((c for tt, c in reversed(g) if tt <= t + 0.6), None)
            if last is not None and last >= cap:
                wasted += MAKE[n]
                events.append((t - p.t0, f"wasted {MAKE[n]}: {n} at {last}/{cap}"))
    held_end = g[-1][1]
    # Time at 1+/2+/3+ outside No Mercy, and the longest 2+ stretch without a spend.
    s = collections.Counter(); longest = 0; run_start = None; longest_at = (0, 0)
    for (t0, c0), (t1, _) in zip(g, g[1:] + [(p.end, g[-1][1])]):
        inside = any(a <= t0 < b for a, b in p.nm)
        for k in (1, 2, 3):
            if c0 >= k and not inside:
                s[k] += t1 - t0
        if c0 >= 2 and not inside:
            run_start = t0 if run_start is None else run_start
            if t1 - run_start > longest:
                longest = t1 - run_start; longest_at = (run_start - p.t0, t1 - p.t0)
        else:
            run_start = None
    for k, v in (('made', made), ('spent', spent), ('wasted', wasted), ('vanished', vanished), ('held', held_end)):
        tot[k] += v
    for k in (1, 2, 3):
        tot[f's{k}'] += s[k]
    tot['len'] += p.end - p.t0
    print(f"{datetime.fromtimestamp(p.t0):%m-%d %H:%M:%S} {(p.zone or '')[:24]:24} {p.end - p.t0:5.0f}  {made:4} {spent:5} {wasted:6} {vanished:8} {held_end:7}   "
          f"{s[1]:4.0f} {s[2]:4.0f} {s[3]:4.0f}                     {longest:5.1f} s ({int(longest_at[0] // 60)}:{longest_at[0] % 60:04.1f}-{int(longest_at[1] // 60)}:{longest_at[1] % 60:04.1f})")
    if listing:
        for t, e in sorted(events):
            print(f"      {int(t // 60)}:{t % 60:04.1f} {e}")
print(f"\ntotal over {tot['len'] / 60:.1f} min: made {tot['made']}, spent {tot['spent']}, wasted to the cap {tot['wasted']}, "
      f"vanished {tot['vanished']}, held at pull end {tot['held']}; outside No Mercy {100 * tot['s1'] / max(tot['len'], 1):.0f}% of the time at 1+, "
      f"{100 * tot['s2'] / max(tot['len'], 1):.0f}% at 2+, {100 * tot['s3'] / max(tot['len'], 1):.0f}% at 3+")
