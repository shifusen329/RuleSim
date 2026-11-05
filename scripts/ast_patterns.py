#!/usr/bin/env python3
"""How Astrologians play a dungeon run, in the terms the AST rule files decide on (2026-11-04).

For FFLogs runs converted to ACT logs (fflogs_pull.sh) or your own (act_runs.py): every Astrologian action with its
situation, then per action the patterns the rules key on. The log's own player (its 02 line) is the Astrologian.

  ast_patterns.py <ACT log> [...] [--csv out.csv]

Pulls: party hits on enemies with gaps under 10 s are one pull; a pull is a boss pull when an enemy in it has at least
half the run's biggest max HP. Enemies alive: enemies the party hit in the last 6 s and not yet dead; near the target:
those within 8 y of your target (Gravity II's reach, as RuleSim's EnemiesFor). MP is not in converted FFLogs logs.
"""
import argparse, collections, csv, datetime as dt, math, os, statistics, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ast_cards import parse_time  # noqa: E402

TANK_JOBS = {0x01, 0x03, 0x13, 0x15, 0x20, 0x25}
JOBN = {0x13: 'PLD', 0x15: 'WAR', 0x20: 'DRK', 0x25: 'GNB', 0x18: 'WHM', 0x1C: 'SCH', 0x21: 'AST', 0x28: 'SGE', 0x14: 'MNK',
        0x16: 'DRG', 0x1E: 'NIN', 0x22: 'SAM', 0x27: 'RPR', 0x29: 'VPR', 0x17: 'BRD', 0x1F: 'MCH', 0x26: 'DNC', 0x19: 'BLM',
        0x1B: 'SMN', 0x23: 'RDM', 0x2A: 'PCT'}
GCDS = {'Fall Malefic', 'Malefic IV', 'Malefic III', 'Malefic II', 'Malefic', 'Gravity II', 'Gravity', 'Combust III',
        'Combust II', 'Combust', 'Aspected Benefic', 'Benefic II', 'Benefic', 'Helios Conjunction', 'Aspected Helios',
        'Helios', 'Ascend', 'Macrocosmos'}
SKIP = {'Attack', 'Sprint', 'attack'}


def num(s):
    try:
        return float(s)
    except ValueError:
        return None


def read(path):
    me = None; jobs = {}; hp = {}; mhp = {}
    emax, ename, epos, dead = {}, {}, {}, {}
    hits = collections.deque(); allhits = []
    mine = {}            # my status name -> expiry
    dots = {}            # enemy -> my Combust expiry
    uses, seen = [], set()
    mypos = collections.deque()   # (t, x, y)
    ehp = {}                      # enemy -> current HP %
    ehits = []                    # (t, ability id, party target): enemy abilities landing on the party
    t = None
    with open(path, 'rb') as f:
        for raw in f:
            k = raw[:4]
            if not k.startswith((b'02|', b'03|', b'21|', b'22|', b'25|', b'26|', b'30|', b'37|', b'38|', b'39|')):
                continue
            p = raw.decode('utf-8', 'replace').rstrip('\r\n').split('|')
            t = parse_time(p[1]); c = p[0]
            if c == '02':
                me = p[2]; continue
            if c == '03':
                if p[2].startswith('10'):
                    jobs[p[2]] = int(p[4], 16)
                continue
            if c == '25':
                dead.setdefault(p[2], t); continue
            if c in ('26', '30'):
                name, src, tgt = p[3], p[5], p[7]
                if tgt == me:
                    if c == '26':
                        mine[name] = t + dt.timedelta(seconds=num(p[4]) or 0)
                    else:
                        mine.pop(name, None)
                if src == me and name.startswith('Combust') and tgt.startswith('4'):
                    dots[tgt] = t + dt.timedelta(seconds=num(p[4]) or 0) if c == '26' else t
                continue
            if c in ('37', '38', '39'):
                i = 4 if c == '39' else 5
                if c == '39' and p[2] == me and len(p) > 11 and num(p[10]) is not None:
                    mypos.append((t, num(p[10]), num(p[11])))
                if len(p) > i + 1 and p[i].isdigit() and p[i + 1].isdigit() and int(p[i + 1]) > 0:
                    if p[2].startswith('4'):
                        ehp[p[2]] = 100.0 * int(p[i]) / int(p[i + 1])
                    if p[2].startswith('10'):
                        hp[p[2]], mhp[p[2]] = int(p[i]), int(p[i + 1])
                    elif p[2].startswith('4'):
                        emax[p[2]] = max(emax.get(p[2], 0), int(p[i + 1]))
                continue
            # 21 / 22
            src, tgt = p[2], p[6]
            if src == me and len(p) > 41 and num(p[40]) is not None:
                mypos.append((t, num(p[40]), num(p[41])))
            while mypos and (t - mypos[0][0]).total_seconds() > 1.5:
                mypos.popleft()
            # Enemies only: converted FFLogs pets are 40Fxxxxx (Earthly Star's explosion would count as a raidwide).
            if src.startswith('4') and not src.startswith('40F') and tgt.startswith('10') and p[5].lower() not in ('attack', 'auto-attack', ''):
                ehits.append((t, p[4], tgt))
            if len(p) > 31 and tgt.startswith('4'):
                ename[tgt] = p[7]
                if p[24].isdigit() and p[25].isdigit() and int(p[25]):
                    ehp[tgt] = 100.0 * int(p[24]) / int(p[25])
                if p[25].isdigit():
                    emax[tgt] = max(emax.get(tgt, 0), int(p[25]))
                x, y = num(p[30]), num(p[31])
                if x is not None and y is not None:
                    epos[tgt] = (x, y)
            if len(p) > 25 and tgt.startswith('10') and p[24].isdigit() and p[25].isdigit() and int(p[25]) > 0:
                hp[tgt], mhp[tgt] = int(p[24]), int(p[25])
            if src.startswith('10') and tgt.startswith('4') and p[5] not in SKIP:
                hits.append((t, tgt)); allhits.append((t, tgt))
            while hits and (t - hits[0][0]).total_seconds() > 6:
                hits.popleft()
            if src != me:
                continue
            key = (p[4], p[44] if len(p) > 44 else p[1])
            if key in seen or p[5] in SKIP or p[5].startswith(('Caramel', 'Grade', 'Rich')):
                continue
            seen.add(key)
            alive = {e for _, e in hits if e not in dead or dead[e] > t}
            near = None
            if tgt.startswith('4') and tgt in epos:
                tx, ty = epos[tgt]
                near = sum(1 for e in alive if e in epos and math.dist(epos[e], (tx, ty)) <= 8)
            party = [i for i in hp if i in jobs]
            pct = {i: 100.0 * hp[i] / mhp[i] for i in party}
            tanks = [pct[i] for i in party if jobs[i] in TANK_JOBS]
            thp = 100.0 * int(p[24]) / int(p[25]) if tgt.startswith('4') and p[24].isdigit() and p[25].isdigit() and int(p[25]) else None
            on = ('tank' if tgt in jobs and jobs[tgt] in TANK_JOBS else 'self' if tgt == me else
                  JOBN.get(jobs.get(tgt), 'party') if tgt.startswith('10') else 'enemy' if tgt.startswith('4') else '-')
            moved = math.dist(mypos[0][1:], mypos[-1][1:]) if len(mypos) >= 2 else 0.0
            phtp = pct.get(tgt) if tgt.startswith('10') else None
            uses.append(dict(t=t, name=p[5], tgt=tgt, tname=p[7], on=on, thp=thp, alive=len(alive), near=near, moving=moved > 0.6,
                             phtp=phtp, packhp=statistics.mean([ehp.get(e, 100.0) for e in alive]) if alive else None,
                             tank=min(tanks) if tanks else None, party=statistics.mean(pct.values()) if pct else None,
                             low=min(pct.values()) if pct else None,
                             buffs={n for n, e in mine.items() if e > t},
                             dotleft=(dots[tgt] - t).total_seconds() if tgt in dots else None))
    # Raidwides: one enemy ability landing on 3 or more party members within 1.5 s.
    rws, groups = [], {}
    for ht, ab, tg in ehits:
        g = groups.get(ab)
        if g and (ht - g[0]).total_seconds() <= 1.5:
            g[1].add(tg)
        else:
            if g and len(g[1]) >= 3:
                rws.append(g[0])
            groups[ab] = [ht, {tg}]
    rws += [g[0] for g in groups.values() if len(g[1]) >= 3]
    rws.sort()
    for u in uses:
        nx = next((r for r in rws if r >= u['t']), None)
        pv = next((r for r in reversed(rws) if r < u['t']), None)
        u['rw_next'] = (nx - u['t']).total_seconds() if nx else None
        u['rw_prev'] = (u['t'] - pv).total_seconds() if pv else None
    # Pulls
    allhits.sort()
    pulls = []
    for ht, e in allhits:
        if not pulls or (ht - pulls[-1]['end']).total_seconds() >= 10:
            pulls.append({'start': ht, 'end': ht, 'enemies': set()})
        pulls[-1]['end'] = ht; pulls[-1]['enemies'].add(e)
    top = max(emax.values()) if emax else 1
    for pl in pulls:
        big = [e for e in pl['enemies'] if emax.get(e, 0) >= top / 2]
        pl['boss'] = bool(big); pl['bossname'] = ', '.join(sorted({ename.get(e, '?') for e in big}))
        pl['n'] = len(pl['enemies'])
    for u in uses:
        u['pull'] = next((i for i, pl in enumerate(pulls) if pl['start'] - dt.timedelta(seconds=3) <= u['t'] <= pl['end'] + dt.timedelta(seconds=3)), None)
        pl = pulls[u['pull']] if u['pull'] is not None else None
        u['boss'] = pl['boss'] if pl else None
        u['since'] = (u['t'] - pl['start']).total_seconds() if pl else None
        u['left'] = (pl['end'] - u['t']).total_seconds() if pl else None
        u['life'] = ((dead.get(u['tgt']) or pl['end']) - u['t']).total_seconds() if pl and u['tgt'].startswith('4') else None
    dur = (allhits[-1][0] - allhits[0][0]).total_seconds() if allhits else 1
    return dict(path=path, me=me, uses=uses, pulls=pulls, dur=dur, jobs={i: JOBN.get(j, j) for i, j in jobs.items()}, rws=rws)


def med(xs):
    xs = [x for x in xs if x is not None]
    return f'{statistics.median(xs):.0f}' if xs else '-'


def q(xs):
    xs = sorted(x for x in xs if x is not None)
    if not xs:
        return '-'
    pick = lambda f: xs[min(len(xs) - 1, int(f * len(xs)))]
    return f'{pick(.25):.0f}/{statistics.median(xs):.0f}/{pick(.75):.0f}'


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('logs', nargs='+'); ap.add_argument('--csv')
    a = ap.parse_args()
    runs = [read(p) for p in a.logs]
    allu = [u for r in runs for u in r['uses']]
    tot_min = sum(r['dur'] for r in runs) / 60

    print('== Runs')
    for r in runs:
        bp = [pl for pl in r['pulls'] if pl['boss']]
        print(f"{os.path.basename(os.path.dirname(r['path']))}: {r['dur'] / 60:.1f} min, {len(r['pulls'])} pulls "
              f"({len(bp)} boss: {'; '.join(pl['bossname'] for pl in bp)}), trash pull sizes "
              f"{[pl['n'] for pl in r['pulls'] if not pl['boss']]}, party {sorted(r['jobs'].values())}")

    print(f'\n== Uses per 10 min (all runs: {tot_min:.0f} min)  per run: ' + ' '.join(os.path.basename(os.path.dirname(r['path'])) for r in runs))
    names = collections.Counter(u['name'] for u in allu)
    for n, c in names.most_common():
        per = [sum(1 for u in r['uses'] if u['name'] == n) / (r['dur'] / 600) for r in runs]
        tr = sum(1 for u in allu if u['name'] == n and u['boss'] is False)
        print(f"{n:24} {c / tot_min * 10:6.1f}   " + ' '.join(f'{x:5.1f}' for x in per) + f"   trash {tr}/{c}")

    print('\n== Damage GCDs by enemies near the target (8 y): Fall Malefic / Gravity II / Combust III')
    for n in range(1, 7):
        sel = [u for u in allu if u['near'] is not None and (u['near'] == n if n < 6 else u['near'] >= 6)]
        cnt = collections.Counter(u['name'] for u in sel)
        print(f"  {n if n < 6 else '6+'}: {cnt['Fall Malefic']:4} / {cnt['Gravity II']:4} / {cnt['Combust III']:4}")
    print('  by enemies alive (hit in 6 s):')
    for n in range(1, 7):
        sel = [u for u in allu if (u['alive'] == n if n < 6 else u['alive'] >= 6)]
        cnt = collections.Counter(u['name'] for u in sel)
        print(f"  {n if n < 6 else '6+'}: {cnt['Fall Malefic']:4} / {cnt['Gravity II']:4} / {cnt['Combust III']:4}")

    cb = [u for u in allu if u['name'] == 'Combust III']
    print('\n== Combust III: trash/boss, enemies near, target HP, target life left (s), refresh with s left (q1/med/q3)')
    for lab, sel in (('trash', [u for u in cb if u['boss'] is False]), ('boss', [u for u in cb if u['boss']])):
        print(f"  {lab:5} {len(sel):3}: near {q(u['near'] for u in sel)}  target HP {q(u['thp'] for u in sel)}  "
              f"life {q(u['life'] for u in sel)}  refresh left {q(u['dotleft'] for u in sel if u['dotleft'] and u['dotleft'] > 0)}")
    tr = [u for u in cb if u['boss'] is False]
    if tr:
        short = sum(1 for u in tr if u['life'] is not None and u['life'] < 15)
        print(f'  trash DoTs on an enemy dead within 15 s: {short}/{len(tr)}')
        per_pull = collections.Counter((id(u), u['pull']) for u in tr)
        multi = collections.Counter()
        for r in runs:
            for i, pl in enumerate(r['pulls']):
                if not pl['boss']:
                    multi[len({u['tgt'] for u in r['uses'] if u['name'] == 'Combust III' and u['pull'] == i})] += 1
        print(f'  trash pulls by distinct enemies given Combust: {dict(sorted(multi.items()))}')

    def ctx(n, extra=''):
        sel = [u for u in allu if u['name'] == n]
        if not sel:
            return
        on = collections.Counter(u['on'] for u in sel)
        print(f"  {n:22} {len(sel):3}  trash {sum(1 for u in sel if u['boss'] is False):3}  on {dict(on)}  "
              f"pull+{q(u['since'] for u in sel)}s  left {q(u['left'] for u in sel)}s  alive {q(u['alive'] for u in sel)}  "
              f"tgtHP {q(u['thp'] for u in sel)}  tank {q(u['tank'] for u in sel)}  party {q(u['party'] for u in sel)}  "
              f"low {q(u['low'] for u in sel)}  their HP {q(u['phtp'] for u in sel)}  packHP {q(u['packhp'] for u in sel)}  "
              f"moving {sum(1 for u in sel if u['moving'])}  raidwide in {q(u['rw_next'] for u in sel)}s / since {q(u['rw_prev'] for u in sel)}s" + extra)

    print('\n== Burst and cooldowns (q1/med/q3; pull+ = seconds into the pull, left = seconds the pull still lasted)')
    for n in ('Divination', 'Lightspeed', 'Oracle', 'Earthly Star', 'Stellar Detonation', 'Macrocosmos', 'Microcosmos',
              'Lord of Crowns', 'Swiftcast', 'Lucid Dreaming'):
        ctx(n)
    ls = [u for u in allu if u['name'] == 'Lightspeed']
    if ls:
        print(f"  Lightspeed with Divination up or within 5 s: "
              f"{sum(1 for u in ls if 'Divination' in u['buffs'] or any(v['name'] == 'Divination' and abs((v['t'] - u['t']).total_seconds()) <= 5 for v in allu))}/{len(ls)}")
    st = [u for u in allu if u['name'] == 'Earthly Star']; det = [u for u in allu if u['name'] == 'Stellar Detonation']
    if st:
        delays = []
        for s in st:
            d = next((x for x in det if 0 < (x['t'] - s['t']).total_seconds() <= 21), None)
            delays.append((d['t'] - s['t']).total_seconds() if d else None)
        print(f"  Earthly Star -> Stellar Detonation after s: {q(delays)}; detonated by hand {sum(1 for x in delays if x is not None)}"
              f"/{len(st)} (charged >= 10 s: {sum(1 for x in delays if x is not None and x >= 10)})")
    sw = [u for u in allu if u['name'] == 'Swiftcast']
    if sw:
        nxt = collections.Counter()
        for r in runs:
            for s in (u for u in r['uses'] if u['name'] == 'Swiftcast'):
                n2 = next((v['name'] for v in r['uses'] if v['t'] > s['t'] and v['name'] in GCDS and v['name'] not in ('Aspected Benefic', 'Combust III', 'Helios Conjunction')), None)
                nxt[n2] += 1
        print(f'  Swiftcast -> next cast-time GCD: {dict(nxt)}')
    for r in runs:
        dv = [u['t'] for u in r['uses'] if u['name'] == 'Divination']
        print(f"  {os.path.basename(os.path.dirname(r['path']))} Divination gaps (s): {[round((b - a).total_seconds()) for a, b in zip(dv, dv[1:])]}"
              f"  raidwides {len(r['rws'])}")

    print('\n== Cards')
    for n in ('The Balance', 'The Spear', 'The Arrow', 'The Bole', 'The Spire', 'The Ewer', 'Lady of Crowns', 'Astral Draw', 'Umbral Draw'):
        sel = [u for u in allu if u['name'] == n]
        if sel:
            ind = sum(1 for u in sel if 'Divination' in u['buffs'])
            ctx(n, f"  in Divination {ind}/{len(sel)}")

    print('\n== Heals and mitigation')
    for n in ('Essential Dignity', 'Celestial Intersection', 'Exaltation', 'Celestial Opposition', 'Horoscope', 'Helios Conjunction',
              'Aspected Benefic', 'Benefic II', 'Collective Unconscious', 'Neutral Sect', 'Sun Sign', 'Synastry', 'Ascend'):
        ctx(n)

    if a.csv:
        with open(a.csv, 'w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['run', 't', 'name', 'on', 'tname', 'boss', 'pull', 'since', 'left', 'alive', 'near', 'thp', 'life', 'tank',
                        'party', 'low', 'dotleft', 'buffs', 'their_hp', 'pack_hp', 'moving', 'raidwide_in', 'raidwide_since'])
            for r in runs:
                for u in r['uses']:
                    w.writerow([os.path.basename(os.path.dirname(r['path'])), u['t'].isoformat(), u['name'], u['on'], u['tname'],
                                u['boss'], u['pull'], u['since'], u['left'], u['alive'], u['near'], u['thp'], u['life'], u['tank'],
                                u['party'], u['low'], u['dotleft'], ';'.join(sorted(u['buffs'])), u['phtp'], u['packhp'], u['moving'],
                                u['rw_next'], u['rw_prev']])


if __name__ == '__main__':
    main()
