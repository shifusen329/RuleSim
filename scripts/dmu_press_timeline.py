#!/usr/bin/env python3
"""A tank's mitigation presses in Dancing Mad against the boss's casts and the hits they were for (2026-11-26).

  dmu_press_timeline.py <ACT log> [--since 20261126] [--me 1001A4A8] [--from 0] [--to 999] [--pull 15:29:11]

User, 2026-11-26: "Fix the Reprisal and Knight's Benediction timing" (DMU_Mit_PLD.json). Per pull (combat windows in zone
553, from the 260 lines), one timeline in seconds after the pull's start:
  - the boss's casts (20 lines from enemies, the first of each burst), numbered per name ("Mystery Magic #2");
  - your presses of the tank mitigation and utility actions below, with their target;
  - the statuses those presses put up (26 lines from you: Knight's Benediction, Knight's Resolve, Reprisal on an enemy,
    Divine Veil, Intervention, Holy Sheltron, Rampart, Guardian, Bulwark, Hallowed Ground, Sprint and the others), with
    the target's HP % at that moment (from its last 21/22/39 line);
  - each boss hit on players (21/22 lines from enemies, grouped by name within 1 s): its source (name and the last four
    digits of its id: one name can be several actors), the players hit, the damage, whether your Reprisal (or another
    player's) was on that source, and Knight's Resolve / Knight's Benediction on each player it hit.
Each line also says how long after the latest boss cast it came ("Light of Judgment #1 +6.2").
"""
import argparse
import collections
import datetime as dt

PRESS = {'Reprisal', 'Intervention', 'Holy Sheltron', 'Sheltron', 'Divine Veil', 'Rampart', 'Guardian', 'Sentinel',
         'Bulwark', 'Hallowed Ground', 'Sprint', 'Provoke', 'Shirk', "Arm's Length", 'Passage of Arms', 'Cover',
         'Iron Will', 'Release Iron Will', 'Royal Guard', 'Release Royal Guard', 'Heart of Light', 'Heart of Corundum',
         'Great Nebula', 'Camouflage', 'Aurora', 'Superbolide', 'Nascent Flash', 'Bloodwhetting', 'Damnation',
         'Thrill of Battle', 'Shake It Off', 'Holmgang', 'Dark Missionary', 'The Blackest Night', 'Oblation',
         'Shadowed Vigil', 'Living Dead', 'Dark Mind'}
STATUS = {"Knight's Benediction", "Knight's Resolve", 'Reprisal', 'Divine Veil', 'Intervention', 'Holy Sheltron',
          'Rampart', 'Guardian', 'Bulwark', 'Hallowed Ground', 'Sprint', 'Passage of Arms', 'Cover', 'Heart of Light',
          'Heart of Corundum', 'Clarity of Corundum', 'Catharsis of Corundum', 'Great Nebula', 'Camouflage', 'Aurora'}
WATCH = {"Knight's Benediction", "Knight's Resolve"}


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def damage(p):
    total = 0
    for k in range(8, 24, 2):
        try:
            flags, v = int(p[k], 16), int(p[k + 1], 16)
        except (ValueError, IndexError):
            break
        if flags & 0xFF in (3, 5, 6):
            total += (v >> 16) + ((v & 0xFF) << 16 if v & 0x4000 else 0)
    return total


def pulls(path, since, me_opt):
    zone, T, cur, me, names = None, None, None, me_opt, {}
    hp = {}                                   # id -> (current, max)
    up = collections.defaultdict(dict)        # target id -> {status: (source, expiry datetime)}
    for raw in open(path, 'rb'):
        h = raw[:4]
        if h.startswith(b'01|'):
            zone = raw.split(b'|')[2].decode()
            continue
        if h.startswith(b'02|') and not me_opt:
            me = raw.decode('utf-8', 'replace').split('|')[2]
            continue
        if h.startswith(b'03|'):
            l = raw.decode('utf-8', 'replace').split('|')
            names[l[2]] = l[3]
            continue
        if zone != '553':
            continue
        if h.startswith(b'39|'):
            l = raw.decode('utf-8', 'replace').split('|')
            try:
                hp[l[2]] = (int(l[4]), int(l[5]))
            except (ValueError, IndexError):
                pass
            continue
        if h.startswith(b'26|') or h.startswith(b'30|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if len(l) < 9:
                continue
            name, src, tgt = l[3], l[5], l[7]
            if h.startswith(b'26|'):
                try:
                    up[tgt][name] = (src, ts(l[1]) + dt.timedelta(seconds=float(l[4])))
                except ValueError:
                    pass
                if T is not None and src == me and name in STATUS:
                    c = hp.get(tgt)
                    cur['ev'].append(((ts(l[1]) - T).total_seconds(), 'status',
                                      f"+{name} ({float(l[4]):.0f} s) on {names.get(tgt, tgt)}" + (f" at {100 * c[0] / c[1]:.0f}% HP" if c and c[1] else '')))
            else:
                up[tgt].pop(name, None)
            continue
        if h == b'260|':
            l = raw.decode().split('|')
            if since and l[1][:10].replace('-', '') < since:
                continue
            if l[3] == '1' and T is None:
                T = ts(l[1])
                cur = {'T': T, 'ev': [], 'hits': collections.OrderedDict(), 'casts': collections.Counter(), 'last': {}}
            elif l[3] == '0' and T is not None:
                yield cur, me, names
                T = None
            continue
        if T is None:
            continue
        if h.startswith(b'20|'):
            l = raw.decode('utf-8', 'replace').split('|')
            if l[2][:1] == '4' and l[3] and not l[5].startswith('unknown'):
                r = (ts(l[1]) - T).total_seconds()
                if r - cur['last'].get(l[5], -99) > 2:
                    cur['casts'][l[5]] += 1
                    cur['ev'].append((r, 'cast', f"{l[3]}: {l[5]} #{cur['casts'][l[5]]}"))
                cur['last'][l[5]] = r
        elif h.startswith(b'21|') or h.startswith(b'22|'):
            l = raw.decode('utf-8', 'replace').split('|')
            r = (ts(l[1]) - T).total_seconds()
            try:
                hp[l[6]] = (int(l[24]), int(l[25]))
            except (ValueError, IndexError):
                pass
            if l[2] == me and l[5] in PRESS:
                if not cur['ev'] or cur['ev'][-1][2] != f"PRESS {l[5]} -> {l[7]}" or r - cur['ev'][-1][0] > 1:
                    cur['ev'].append((r, 'press', f"PRESS {l[5]} -> {l[7]}"))
            elif l[2][:1] == '4' and l[6][:1] == '1' and l[5] and l[4] != 'C252' and not l[5].startswith('unknown'):
                d = damage(l)
                if d <= 0:
                    continue
                key = (l[5], round(r))
                k2 = next((k for k in cur['hits'] if k[0] == l[5] and abs(cur['hits'][k]['t'] - r) <= 1.0), None)
                if k2 is None:
                    cur['hits'][key] = {'t': r, 'src': l[2], 'srcName': l[3], 'players': [], 'rep': False, 'anyRep': False}
                    k2 = key
                g = cur['hits'][k2]
                now = ts(l[1])
                # Reprisal lands only on targetable enemies, never on the untargetable helpers that cast most of the boss's
                # mechanics under its name (all "Kefka" at the arena centre); the sheet counts those as covered (its notes
                # exclude only other enemies' hits, such as Graven Image's Wave Cannon). So a Reprisal on any actor of the
                # hit's source name counts.
                for actor in [a for a, n in names.items() if n == l[3] and a[:1] == '4'] or [l[2]]:
                    rep = up[actor].get('Reprisal')
                    if rep and rep[1] > now:
                        g['rep' if rep[0] == me else 'anyRep'] = True
                kb = [s for s in WATCH if (x := up[l[6]].get(s)) and x[1] > now]
                g['players'].append((names.get(l[6], l[6]), d, kb))
    if T is not None:
        yield cur, me, names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log')
    ap.add_argument('--since')
    ap.add_argument('--me')
    ap.add_argument('--from', dest='frm', type=float, default=0)
    ap.add_argument('--to', type=float, default=9999)
    ap.add_argument('--pull')
    a = ap.parse_args()
    for c, me, names in pulls(a.log, a.since, a.me):
        if a.pull and f"{c['T']:%H:%M:%S}" != a.pull:
            continue
        ev = list(c['ev'])
        for g in c['hits'].values():
            n = len(g['players'])
            who = ', '.join(f"{p} {d // 1000}k" + (f" [{'/'.join(sorted(kb))}]" if kb else '') for p, d, kb in g['players'][:3])
            ev.append((g['t'], 'hit', f"HIT {next(k for k, v in c['hits'].items() if v is g)[0]} ({g['srcName']} {g['src'][-4:]}) on {n}"
                       + (f": {who}" if n <= 3 else f" (avg {sum(d for _, d, _ in g['players']) // n // 1000}k)")
                       + (" | your Reprisal on it" if g['rep'] else " | another Reprisal on it" if g['anyRep'] else '')))
        ev.sort(key=lambda e: (e[0], e[1] != 'cast'))
        print(f"== {c['T']:%Y-%m-%d %H:%M:%S} (you {names.get(me, me)})")
        anchor = None
        for r, kind, text in ev:
            if kind == 'cast':
                anchor = (text.split(': ', 1)[1], r)
            if r < a.frm or r > a.to:
                continue
            rel = f"{anchor[0]} +{r - anchor[1]:.1f}" if anchor and kind != 'cast' else ''
            print(f"  {r:7.1f}  {text:<78} {rel}")


if __name__ == '__main__':
    main()
