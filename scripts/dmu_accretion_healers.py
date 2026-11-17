#!/usr/bin/env python3
"""What the healers did for Dancing Mad's Phase 3 Accretions (2026-11-16).

  dmu_accretion_healers.py <ACT log or folder> [...] [--since 20261020] [--me 10041112,1001A4A8] [--not-healer] [--clean]
                           [--quiet]

Per Earthquake (dmu_earthquake.py's scan): every press by a healer (WHM, SCH, AST, SGE) on players from the cast until 3 s
after the second Accretion came off, in seconds after the cast: a party-wide one as "Medica III x8 (avg 21k)", a single
one with its target, how much it healed and the target's HP after (holders marked ACC1/ACC2, at full marked FULL); each
Accretion's removal (whose heal, the holder's own or not); and just before each, the lowest non-holder's HP and the tanks'.
--not-healer keeps Earthquakes where you were not a healer; --clean those that reached Black Hole with nobody dead.
The summary: what the healers pressed between the Accretions landing (+7.4 s) and the first coming off, how many of them
healed the whole party, when and by whom each came off, and the party's HP at the first.

--mits (2026-11-16) adds the mitigation: every mitigation press by anyone from 30 s before the cast to 3 s after the second
Accretion came off, and what was up when the first pulse landed: party mitigation and shields (the number of players with
each) and debuffs on Chaos, whose Earthquake the pulse is (Reprisal, Addle, Feint, Dismantle). The summary counts, over the
Earthquakes, how often each was up at the first pulse.
"""
import collections
import datetime as dt
import mmap
import os
import re
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dmu_black_hole import effects, seek_time, ts  # noqa: E402
from dmu_earthquake import scan, analyse  # noqa: E402

MIT_STATUS = {'Kerachole', 'Holos', 'Holosakos', 'Panhaima', 'Sacred Soil', 'Fey Illumination', 'Seraphic Illumination',
              'Desperate Measures', 'Temperance', 'Divine Caress', 'Collective Unconscious', 'Sun Sign', 'Shield Samba',
              'Troubadour', 'Tactician', 'Magick Barrier', 'Dark Missionary', 'Heart of Light', 'Divine Veil',
              'Passage of Arms', 'Shake It Off', 'Tempera Grassa', 'Radiant Aegis', 'Galvanize', 'Catalyze',
              'Eukrasian Prognosis', 'Seraphism', 'Intersection', 'Exaltation', 'Aquaveil', 'Divine Benison',
              'Improvised Finish', 'Rampart', 'Vengeance', 'Damnation', 'Shadowed Vigil', 'Guardian', 'Great Nebula',
              'Holy Sheltron', 'Bloodwhetting', 'The Blackest Night', 'Oblation', 'Heart of Corundum', 'Camouflage',
              'Liturgy of the Bell', 'Thrill of Battle', 'Dark Mind', 'Bulwark'}
DEBUFFS = {'Reprisal', 'Addle', 'Feint', 'Dismantle'}
MIT_PRESS = {'Kerachole', 'Holos', 'Panhaima', 'Sacred Soil', 'Fey Illumination', 'Seraphism', 'Expedient', 'Temperance',
             'Divine Caress', 'Collective Unconscious', 'Sun Sign', 'Shield Samba', 'Troubadour', 'Tactician',
             'Magick Barrier', 'Dark Missionary', 'Heart of Light', 'Divine Veil', 'Passage of Arms', 'Shake It Off',
             'Tempera Grassa', 'Tempera Coat', 'Radiant Aegis', 'Reprisal', 'Addle', 'Feint', 'Dismantle',
             'Liturgy of the Bell', 'Eukrasian Prognosis II', 'Concitation', 'Accession', 'Celestial Intersection',
             'Exaltation', 'Aquaveil', 'Divine Benison', 'Improvised Finish', 'Neutral Sect', 'Rampart', 'Vengeance',
             'Damnation', 'Shadowed Vigil', 'Sentinel', 'Guardian', 'Great Nebula', 'Holy Sheltron', 'Bloodwhetting',
             'The Blackest Night', 'Oblation', 'Heart of Corundum', 'Camouflage', 'Thrill of Battle', 'Dark Mind',
             'Bulwark', 'Holmgang', 'Living Dead', 'Superbolide', 'Hallowed Ground'}


def mit_window(path, T, end):
    """Mitigation presses and status changes from 30 s before T to end seconds after it."""
    presses, status = [], []
    with open(path, 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        o = seek_time(mm, 0, len(mm), T - dt.timedelta(seconds=32))
        while o < len(mm):
            e = mm.find(b'\n', o)
            e = len(mm) if e < 0 else e
            raw = mm[o:e]
            o = e + 1
            if raw[:3] not in (b'21|', b'22|', b'26|', b'30|'):
                continue
            x = raw.decode('utf-8', 'replace').rstrip('\r').split('|')
            try:
                r = (ts(x[1]) - T).total_seconds()
            except (IndexError, ValueError):
                continue
            if r < -30:
                continue
            if r > end:
                break
            if x[0] in ('21', '22') and x[2][:1] == '1' and x[5] in MIT_PRESS:
                if not presses or presses[-1][1:3] != (x[3], x[5]) or r - presses[-1][0] > 1:
                    presses.append((r, x[3], x[5], x[7]))
            elif x[0] in ('26', '30') and len(x) > 8 and (x[3] in MIT_STATUS or x[3] in DEBUFFS):
                status.append((r, x[0] == '26', x[3], x[7], x[8]))
        mm.close()
    return presses, status


def up_at(status, r0):
    """{status: set of holders} at r0."""
    on = collections.defaultdict(set)
    for r, gain, name, tgt, tname in status:
        if r > r0:
            break
        if gain:
            on[name].add((tgt, tname))
        else:
            on[name].discard((tgt, tname))
    return on

HEALERS = {'WHM', 'SCH', 'AST', 'SGE'}
TANKS = {'PLD', 'WAR', 'DRK', 'GNB'}
SKIP = {'attack', 'Sprint', 'Swiftcast', 'Lucid Dreaming', 'Surecast', 'Rescue', 'Esuna'}


def main():
    args = sys.argv[1:]
    me, since, paths, not_healer, clean, quiet, mits = {'10041112', '1001A4A8'}, None, [], False, False, False, False
    mit_up = collections.Counter()       # Earthquakes with each mitigation up at the first pulse (party: on 4+ players)
    mit_n = 0
    i = 0
    while i < len(args):
        a = args[i]
        if a == '--me':
            me = set(args[i + 1].split(','))
            i += 2
        elif a == '--since':
            since = args[i + 1]
            i += 2
        elif a in ('--not-healer', '--clean', '--quiet', '--mits'):
            not_healer |= a == '--not-healer'
            clean |= a == '--clean'
            quiet |= a == '--quiet'
            mits |= a == '--mits'
            i += 1
        else:
            paths.append(a)
            i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    pre = collections.Counter()          # what healers pressed between landing and the first removal
    pre_party = []                       # party-wide heals/shields in that span, per Earthquake
    first_heal = []                      # seconds after the cast of the first party-wide heal or shield
    first_off, second_off, by_self, order = [], [], collections.Counter(), collections.Counter()
    low_before, tank_before = [], []
    n = 0
    for log in logs:
        for path, T, jobs, rows in scan(log, me, since):
            res = analyse(T, jobs, rows, me)
            you = next((jobs.get(x) for x in me if x in jobs), '?')
            if not_healer and you in HEALERS:
                continue
            if clean and (res['bh'] is None or res['dead']):
                continue
            names, acc = {}, {}
            hp, maxhp = {}, {}
            for r, l in rows:
                if l[0] == '03' and len(l) > 3:
                    names[l[2]] = l[3]
            line = {}
            for r, l in rows:
                if l[0] in ('26', '30') and len(l) > 8 and l[7][:1] == '1':
                    names.setdefault(l[7], l[8])
                    if l[3] == 'Accretion' and l[0] == '26':
                        acc.setdefault(l[7], r)
                    if l[3] in ('First in Line', 'Second in Line') and l[0] == '26':
                        line[l[7]] = 1 if l[3].startswith('First') else 2
                if l[0] in ('21', '22') and len(l) > 7:
                    names.setdefault(l[2], l[3])
                    names.setdefault(l[6], l[7])
            if len(acc) < 2:
                continue
            n += 1
            healers = {i: j for i, j in jobs.items() if j in HEALERS}
            land = min(acc.values())
            offs = {}
            out = []
            last = None
            for r, l in rows:
                ty = l[0]
                if ty == '38' and l[2][:1] == '1':
                    try:
                        hp[l[2]], maxhp[l[2]] = int(l[5]), int(l[6])
                    except (ValueError, IndexError):
                        pass
                elif ty in ('21', '22') and len(l) > 35:
                    for who, h, mx in ((l[6], l[24], l[25]), (l[2], l[34], l[35])):
                        if who[:1] == '1':
                            try:
                                hp[who], maxhp[who] = int(h), int(mx)
                            except ValueError:
                                pass
                if ty == '30' and l[3] == 'Accretion' and l[7] in acc and l[7] not in offs:
                    offs[l[7]] = r
                    others = [100 * hp[x] / maxhp[x] for x in hp if x not in acc and maxhp.get(x) and jobs.get(x)]
                    tanks = [100 * hp[x] / maxhp[x] for x in hp if jobs.get(x) in TANKS and maxhp.get(x)]
                    who = last[1] if last and last[0] > r - 1.5 and last[2] == l[7] else '?'
                    out.append((r, f"OFF {'ACC%d' % line.get(l[7], 0)} {names.get(l[7], l[7])} ({jobs.get(l[7])}); last heal on them by "
                                   f"{who}; lowest other {min(others) if others else 0:.0f}%, under 45%: {sum(1 for x in others if x < 45)}; "
                                   f"tanks {', '.join('%.0f%%' % x for x in tanks)}"))
                    if len(offs) == 1:
                        low_before.append(min(others) if others else 0)
                        tank_before.append(min(tanks) if tanks else 0)
                        first_off.append(r)
                        by_self['own heal' if who == names.get(l[7]) else 'another healer' if who != '?' else 'unknown'] += 1
                        order['First in Line first' if line.get(l[7]) == 1 else 'Second in Line first'] += 1
                    else:
                        second_off.append(r)
                if ty not in ('21', '22') or l[2] not in healers or l[5] in SKIP or len(l) < 26:
                    continue
                if l[6][:1] != '1':
                    continue
                d, h = effects(l)
                tgt = l[6]
                if h and tgt in acc:
                    last = (r, names.get(l[2], l[2]), tgt)
                full = ''
                try:
                    if h and int(l[24]) >= int(l[25]) and tgt in acc and tgt not in offs:
                        full = ' FULL'
                except ValueError:
                    pass
                key = (round(r, 1), l[2], l[5])
                out.append((r, (key, tgt, h, l[24], l[25], full)))
            if not offs:
                continue
            end = max(offs.values()) + 3
            # Merge multi-target presses.
            merged = []
            groups = collections.OrderedDict()
            for r, x in out:
                if isinstance(x, str):
                    merged.append((r, x))
                    continue
                key, tgt, h, cur, mx, full = x
                g = groups.setdefault(key, [])
                g.append((tgt, h, cur, mx, full))
                if len(g) == 1:
                    merged.append((r, key))
            first_party = None
            if not quiet:
                print(f"== {T:%Y-%m-%d %H:%M}  you {you}; healers {', '.join(sorted(healers.values()))}; Accretions on "
                      + ', '.join(f"ACC{line.get(i, 0)} {names.get(i, i)} ({jobs.get(i)})" for i in acc)
                      + f"; Black Hole {'reached' if res['bh'] else 'not reached'}, {res['dead']} dead")
            for r, x in merged:
                if r > end:
                    break
                if isinstance(x, str):
                    if not quiet:
                        print(f'   {r:+6.1f}  {x}')
                    continue
                g = groups[x]
                src, name = names.get(x[1], x[1]), x[2]
                if len(g) >= 3:
                    hs = [h for _, h, *_ in g]
                    txt = f'{name} x{len(g)} (avg {statistics.mean(hs) / 1000:.0f}k)'
                    if any(t in acc for t, *_ in g):
                        txt += ' incl. ' + ', '.join(f"ACC{line.get(t, 0)} {int(c) * 100 // max(1, int(m))}%{f}" for t, h, c, m, f in g if t in acc)
                    if land <= r and (r < min(offs.values())) and first_party is None:
                        first_party = r
                    party = True
                else:
                    t, h, c, m, f = g[0]
                    tag = f"ACC{line.get(t, 0)} " if t in acc else ''
                    pct = int(c) * 100 // max(1, int(m)) if c.isdigit() and m.isdigit() else 0
                    txt = f"{name} -> {tag}{names.get(t, t)}" + (f" heal {h / 1000:.0f}k, now {pct}%{f}" if h else '')
                    party = False
                if land <= r < min(offs.values()):
                    pre[f"{healers.get(x[1])} {name}{' (party)' if party else ''}"] += 1
                if not quiet:
                    print(f'   {r:+6.1f}  {healers.get(x[1])} {src}: {txt}')
            if mits:
                presses, status = mit_window(path, T, end)
                p0 = next((p[0] for p in res['pulses']), None)
                if not quiet:
                    print('   mitigation pressed (s after the cast): ' + '; '.join(
                        f"{r:+.1f} {jobs.get(next((i for i, nm in names.items() if nm == who), ''), '?')} {nm}"
                        + (f" -> {tg.split()[0]}" if tg and tg != who and nm not in ('Reprisal', 'Addle', 'Feint', 'Dismantle') else '')
                        for r, who, nm, tg in presses if r <= end))
                if p0 is not None:
                    mit_n += 1
                    on = up_at(status, p0)
                    party = {k: len(v) for k, v in on.items() if k in MIT_STATUS and len(v) >= 4}
                    single = {k: len(v) for k, v in on.items() if k in MIT_STATUS and 0 < len(v) < 4}
                    deb = {k: ', '.join(sorted({t for _, t in v})) for k, v in on.items() if k in DEBUFFS and v}
                    for k in party:
                        mit_up[f'{k} (party)'] += 1
                    for k, v in on.items():
                        if k in DEBUFFS and any(t in ('Chaos',) for _, t in v):
                            mit_up[f'{k} on Chaos'] += 1
                    if not quiet:
                        print(f"   up at the first pulse (+{p0:.1f}): party {party or '-'}; few {single or '-'}; on enemies {deb or '-'}")
            if first_party is not None:
                first_heal.append(first_party)
            pre_party.append(sum(1 for r, x in merged if not isinstance(x, str) and len(groups[x]) >= 3 and land <= r < min(offs.values())))
    def q(v):
        v = sorted(v)
        return f'median {statistics.median(v):+.1f}  min {v[0]:+.1f}  max {v[-1]:+.1f}' if v else '-'
    print(f'\n==== {n} Earthquakes ====')
    print(f'first Accretion off: {q(first_off)}; second: {q(second_off)} (s after the cast; they land at +7.4)')
    print(f'first off by: {dict(by_self)}; order: {dict(order)}')
    print(f'party-wide heals or shields between landing and the first off, per Earthquake: {sorted(pre_party)}')
    print(f'the first of them: {q(first_heal)} ({len(first_heal)} of {n} had one)')
    print(f'lowest non-holder HP % at the first off: {q(low_before)}; lowest tank: {q(tank_before)}')
    print('healer presses between landing and the first off:')
    for k, v in pre.most_common(25):
        print(f'   {v:4}  {k}')
    if mits:
        print(f'up at the first pulse, of {mit_n} Earthquakes (party = on 4+ players):')
        for k, v in mit_up.most_common(30):
            print(f'   {v:4}  {k}')


if __name__ == '__main__':
    main()
