#!/usr/bin/env python3
"""Who holds Dancing Mad's Phase 3 Accretions, which one comes off first and whose heal does it (2026-11-17).

  dmu_accretion_order.py <ACT log or folder> [...] [--since 20261020] [--me 10041112,1001A4A8] [--list]

Per Earthquake (dmu_earthquake.py's scan): each Accretion holder's role (healer, DPS, tank) and line (First / Second in
Line), the order they came off (or ran out), the job of the last heal on the holder within 1.5 s of the removal (the holder's
own heal marked "self"; "?" = no direct heal within 1.5 s, i.e. a heal-over-time tick, which ACT logs
save without its source), and whether the party reached Black Hole's cast with nobody dead. The summary answers:
  - Is it always one healer and one DPS? Is the healer always First in Line (so line order = role order)?
  - Which came off first, by role and by line, in pulls that reached Black Hole clean and in the rest?
  - Whose heals removed them: healers, the holder's own, other DPS, tanks?
  - Which healer does it (2026-11-17): the regen healer (WHM, AST) or the shield healer (SGE, SCH), by the holder's role and
    by the party's healer pair. A removal with no direct heal within 1.5 s goes to the heals over time on the holder then
    (statuses from 30 s before the cast; ACT logs save HoT ticks without their source).
Folders are read for Network_*.log; converted FFLogs logs (Network_39999_*) work too.
"""
import collections
import datetime as dt
import mmap
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dmu_black_hole import effects, LINES, seek_time, ts  # noqa: E402
from dmu_earthquake import scan  # noqa: E402

HEALERS = {'WHM', 'SCH', 'AST', 'SGE'}
TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}
PURE = {'WHM', 'AST'}
# Heals cast by pets and placed actors (fairy, seraph, Earthly Star, Liturgy of the Bell): their owner's job.
PET = {'Fey Blessing': 'SCH', 'Consolation': 'SCH', 'Embrace': 'SCH', 'Seraphic Veil': 'SCH', 'Fey Union': 'SCH',
       'Stellar Explosion': 'AST', 'Stellar Burst': 'AST', 'Liturgy of the Bell': 'WHM'}
# Statuses that heal over time (their ticks carry no source in ACT logs).
HOTS = {'Regen', 'Medica II', 'Medica III', 'Asylum', 'Divine Aura', 'Aspected Benefic', 'Aspected Helios', 'Helios Conjunction',
        'Wheel of Fortune', 'Kerakeia', 'Physis', 'Physis II', 'Autophysis', 'Whispering Dawn', "Angel's Whisper", 'Sacred Soil',
        'Crest of Time Returned', 'Rekindle', 'Undying Flame'}


def hot_window(path, T):
    """Heal-over-time statuses gained and lost from 30 s before the cast to 30 s after: (r, gained, name, source, target)."""
    out = []
    with open(path, 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        o = seek_time(mm, 0, len(mm), T - dt.timedelta(seconds=32))
        while o < len(mm):
            e = mm.find(b'\n', o)
            e = len(mm) if e < 0 else e
            raw = mm[o:e]
            o = e + 1
            if raw[:3] not in (b'26|', b'30|'):
                continue
            x = raw.decode('utf-8', 'replace').split('|')
            try:
                r = (ts(x[1]) - T).total_seconds()
            except (IndexError, ValueError):
                continue
            if r > 30:
                break
            if r >= -30 and x[3] in HOTS and x[7][:1] == '1':
                out.append((r, x[0] == '26', x[3], x[5], x[7]))
        mm.close()
    return out


def role(job):
    return 'healer' if job in HEALERS else 'tank' if job in TANKS else 'DPS' if job and job != '?' else '?'


def cls(job):
    return 'regen healer' if job in PURE else 'shield healer' if job in HEALERS else role(job)


def one(path, T, jobs, rows):
    line, acc_on, off, names = {}, {}, {}, {}
    hw = hot_window(path, T)
    last = {}      # holder id -> (r, source id)
    bh, dead, end = None, 0, None
    for r, l in rows:
        ty = l[0]
        if ty == 'END':
            end = r
            break
        if ty == '20' and len(l) > 5 and l[5] == 'Black Hole' and bh is None:
            bh = r
        elif ty == '26' and l[7][:1] == '1':
            names[l[7]] = l[8]
            if l[3] in LINES:
                line[l[7]] = LINES[l[3]]
            elif l[3] == 'Accretion' and l[7] not in acc_on:
                acc_on[l[7]] = r
        elif ty == '30' and l[7][:1] == '1' and l[3] == 'Accretion' and l[7] in acc_on and l[7] not in off:
            h = last.get(l[7])
            src = h[1] if h and r - h[0] < 1.5 else None
            hots = {}
            for hr, gained, nm, hsrc, ht in hw:
                if ht == l[7] and hr <= r:
                    if gained:
                        hots[nm] = hsrc
                    else:
                        hots.pop(nm, None)
            off[l[7]] = (r, src, r - acc_on[l[7]] >= 13.8, h[2] if src else '', hots)
        elif ty in ('21', '22') and l[6][:1] == '1':
            _, healed = effects(l)
            if healed:
                last[l[6]] = (r, l[2], l[5])
        elif ty == '25' and l[2][:1] == '1' and (bh is None or r < bh):
            dead += 1
    holders = []
    for i, t in acc_on.items():
        r, src, expired, act, hots = off.get(i, (None, None, False, '', {}))
        by = 'expired' if expired else '?' if src is None else ('self' if src == i else role(jobs.get(src)) + ' ' + jobs.get(src, '?')) + f' ({act})'
        # Which kind of player did it: the direct heal's caster, else the heals over time's casters ("mixed" if both kinds).
        if expired or r is None:
            kind = 'ran out' if expired else None
        elif src is not None:
            kind = cls(jobs.get(src) or PET.get(act))
        elif hots:
            ks = {cls(jobs.get(v)) for v in hots.values()}
            kind = (ks.pop() if len(ks) == 1 else 'mixed') + ' (HoT)'
        else:
            kind = 'unknown'
        if src is None and hots and not expired:
            by += ' HoT: ' + ', '.join(f'{k} ({jobs.get(v, "?")})' for k, v in sorted(hots.items()))
        holders.append({'id': i, 'name': names.get(i, i), 'job': jobs.get(i, '?'), 'role': role(jobs.get(i)),
                        'line': line.get(i, 0), 'on': t, 'off': r, 'by': by, 'expired': expired, 'kind': kind})
    holders.sort(key=lambda h: (h['off'] is None, h['off'] or 0))
    hl = sorted(j for j in jobs.values() if j in HEALERS)
    npure = sum(1 for j in hl if j in PURE)
    pair = 'regen+shield' if len(hl) == 2 and npure == 1 else 'two regen' if npure == 2 and len(hl) == 2 else \
        'two shield' if len(hl) == 2 else '+'.join(hl) or 'none'
    return {'T': T, 'holders': holders, 'clean': bh is not None and dead == 0, 'bh': bh, 'dead': dead, 'healers': hl, 'pair': pair}


def main():
    args = sys.argv[1:]
    me = {'10041112', '1001A4A8'}
    since, show, paths = None, False, []
    i = 0
    while i < len(args):
        if args[i] == '--me':
            me = set(args[i + 1].split(','))
            i += 2
        elif args[i] == '--since':
            since = args[i + 1]
            i += 2
        elif args[i] == '--list':
            show = True
            i += 1
        else:
            paths.append(args[i])
            i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    seen, res = set(), []
    for p in logs:
        for path, T, jobs, rows in scan(p, me, since):
            if T in seen:
                continue
            seen.add(T)
            a = one(path, T, jobs, rows)
            a['src'] = os.path.basename(path)
            a['you'] = next((jobs[x] for x in me if x in jobs), None)
            res.append(a)

    pairs = collections.Counter()
    healer_line = collections.Counter()
    first = {True: collections.Counter(), False: collections.Counter()}
    by = {True: collections.Counter(), False: collections.Counter()}
    n = {True: 0, False: 0}
    kinds = collections.defaultdict(collections.Counter)     # (pair, whose Accretion) -> remover kind
    acts = collections.defaultdict(collections.Counter)      # remover kind -> action
    pairs_seen = collections.Counter()
    for a in res:
        pairs_seen[a['pair'] + ' (' + '+'.join(a['healers']) + ')'] += 1
        for h in a['holders']:
            if h['kind'] is None:
                continue
            whose = (cls(h['job']) if h['role'] == 'healer' else h['role']) + "'s"
            kinds[(a['pair'], whose)][h['kind']] += 1
            kinds[(a['pair'], 'all')][h['kind']] += 1
            if '(' in h['by'] and 'HoT' not in h['by']:
                acts[h['kind']][h['by'].rsplit('(', 1)[1].rstrip(')') + (' on self' if h['by'].startswith('self') else '')] += 1
    for a in res:
        hs = a['holders']
        pairs['+'.join(sorted(h['role'] for h in hs))] += 1
        for h in hs:
            if h['role'] == 'healer':
                healer_line[h['line']] += 1
        done = [h for h in hs if h['off'] is not None]
        c = a['clean']
        if len(hs) == 2:
            n[c] += 1
        if len(done) >= 1 and len(hs) == 2:
            f = done[0]
            first[c][f"{f['role']} first ({'First' if f['line'] == 1 else 'Second' if f['line'] == 2 else '?'} in Line)"
                     + (' [ran out]' if f['expired'] else '')] += 1
        for h in done:
            by[c][f"{h['role']}'s Accretion: {h['by'].split(' ')[0]}"] += 1
        if show:
            outcome = 'clean to Black Hole' if c else 'Black Hole not reached' if a['bh'] is None else f"Black Hole reached, {a['dead']} dead"
            print(f"{a['T']:%Y-%m-%d %H:%M:%S} {a['src']:28} you {a['you'] or '-':4} healers {'+'.join(a['healers']):8} {outcome}")
            for h in hs:
                o = f"off +{h['off']:.1f} by {h['by']}" if h['off'] is not None else 'never off (pull ended)'
                print(f"    {h['role']:6} {h['job']:3} line {h['line']}  {h['name']:22} on +{h['on']:.1f}, {o}")
    print(f"Earthquakes: {len(res)} ({n[True]} clean to Black Hole with two Accretions, {n[False]} not)")
    print(f"holder pairs: {dict(pairs)}")
    print(f"healer holder's line: {dict(sorted(healer_line.items()))}  (1 = First in Line)")
    for c in (True, False):
        print(f"{'clean' if c else 'not clean'}: first off: {dict(first[c])}")
        print(f"{'clean' if c else 'not clean'}: removed by: {dict(by[c])}")
    print(f"healer pairs: {dict(pairs_seen)}")
    for key in sorted(kinds):
        print(f"{key[0]:13} {key[1]:22} removed by: {dict(kinds[key].most_common())}")
    for k in sorted(acts):
        print(f"actions, {k}: {dict(acts[k].most_common())}")


if __name__ == '__main__':
    main()
