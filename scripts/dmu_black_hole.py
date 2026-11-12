#!/usr/bin/env python3
"""Every Black Hole (Exdeath, Dancing Mad Phase 3) in ACT logs: your job, the pull time, and every death from the cast to the
end of the pull (or +75 s): who, when, the hit that killed them, their HP before it, and the mechanic statuses they held
(2026-11-11).

  dmu_black_hole.py <ACT log or folder> [...] [--me 10041112,1001A4A8] [--job SGE] [--since 20261024]
                    [--breaks] [--detail N [--from -10] [--to 60]]

The mechanic (BossMod's DMU Phase3.cs, checked against the logs): Earthquake leaves everyone on 1 HP with a Primordial Crust
and puts Accretion on a healer and a DPS. A Crust keeps you on 1 HP once when a hit would kill you, then breaks; every broken
Crust or healed-off Accretion makes the earth crystal pulse ~1.1 s later (50-90k to everyone, Earth Resistance Down II for
1.96 s), and a second pulse inside that window does 1.3-1.6 M to everyone, which breaks every Crust at once: a wipe.
--breaks lists each pulse and what set it off. --job keeps the Black Holes where you played that job (3-letter, from your 03 line). --detail N dumps the Nth listed Black
Hole: enemy casts, enemy hits on players with HP, mechanic statuses (from enemies or the environment), deaths, and your
own actions, statuses and the hits you took, from --from to --to seconds around the cast.
Pure stdlib Python; finds the casts with mmap, so multi-GB logs are fine.
"""
import datetime as dt
import mmap
import os
import re
import sys

JOBS = {0x13: 'PLD', 0x14: 'MNK', 0x15: 'WAR', 0x16: 'DRG', 0x17: 'BRD', 0x18: 'WHM', 0x19: 'BLM', 0x1B: 'SMN', 0x1C: 'SCH',
        0x1E: 'NIN', 0x1F: 'MCH', 0x20: 'DRK', 0x21: 'AST', 0x22: 'SAM', 0x23: 'RDM', 0x24: 'BLU', 0x25: 'GNB', 0x26: 'DNC',
        0x27: 'RPR', 0x28: 'SGE', 0x29: 'VPR', 0x2A: 'PCT'}
CAST = b'|BAFB|Black Hole|'


def ts(s):
    return dt.datetime.fromisoformat(s[:26])


def amount(v):
    v = int(v, 16)
    return (v >> 16) + ((v & 0xFF) << 16 if v & 0x4000 else 0)


def effects(l):
    """(damage, heal) from a 21/22 line's eight effect pairs."""
    d = h = 0
    for i in range(8, 24, 2):
        if not l[i]:
            continue
        e = int(l[i], 16) & 0xFF
        if e in (3, 5, 6):
            d += amount(l[i + 1])
        elif e == 4:
            h += amount(l[i + 1])
    return d, h


def line_at(mm, p):
    s = mm.rfind(b'\n', 0, p) + 1
    e = mm.find(b'\n', p)
    return s, e, mm[s:e].decode('utf-8', 'replace').rstrip('\r').split('|')


def back(mm, pat, end, stop, test):
    """Walk lines starting with pat backwards from end to stop; return the first split line for which test() is true."""
    while True:
        p = mm.rfind(pat, stop, end)
        if p < 0:
            return None, -1
        l = mm[p + 1:mm.find(b'\n', p + 1)].decode('utf-8', 'replace').rstrip('\r').split('|')
        if test(l):
            return l, p
        end = p


def seek_time(mm, lo, hi, T):
    """Byte offset of a line start at or before time T (binary search on the timestamps)."""
    while hi - lo > 100000:
        m = (lo + hi) // 2
        s = mm.find(b'\n', m) + 1
        e = mm.find(b'\n', s)
        try:
            t = ts(mm[s:e].split(b'|')[1].decode())
        except (IndexError, ValueError):
            lo = m
            continue
        if t < T:
            lo = m
        else:
            hi = m
    return mm.find(b'\n', lo) + 1


def scan(path, me, since):
    out = []
    with open(path, 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        p = 0
        while True:
            p = mm.find(CAST, p + 1)
            if p < 0:
                break
            s, e, l = line_at(mm, p)
            if l[0] != '20':
                continue
            T = ts(l[1])
            if since and T.strftime('%Y%m%d') < since:
                continue
            z, zp = back(mm, b'\n01|', s, 0, lambda x: True)
            if not z or z[2] != '553':
                continue
            j, _ = back(mm, b'\n03|', s, zp, lambda x: len(x) > 4 and x[2] in me)
            job = JOBS.get(int(j[4], 16), j[4]) if j else '?'
            # Pull start: the first in-combat 260 line after the last out-of-combat one.
            start = None
            end = s
            while True:
                c, cp = back(mm, b'\n260|', end, zp, lambda x: True)
                if not c or c[2] == '0':
                    break
                start = ts(c[1])
                end = cp
            out.append({'path': path, 'off': s, 'T': T, 'job': job, 'start': start, 'zp': zp, 'mm': None})
        mm.close()
    return out


def window(bh, me, a, b):
    """Parsed lines from a s to b s around the cast, stopping at the end of combat."""
    with open(bh['path'], 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        T = bh['T']
        o = seek_time(mm, bh['zp'], bh['off'], T + dt.timedelta(seconds=a))
        rows = []
        while o < len(mm):
            e = mm.find(b'\n', o)
            if e < 0:
                e = len(mm)
            raw = mm[o:e]
            o = e + 1
            if not raw[:3] in (b'20|', b'21|', b'22|', b'23|', b'25|', b'26|', b'30|', b'260', b'24|'):
                continue
            l = raw.decode('utf-8', 'replace').rstrip('\r').split('|')
            try:
                r = (ts(l[1]) - T).total_seconds()
            except (IndexError, ValueError):
                continue
            if r < a:
                continue
            if r > b:
                break
            if l[0] == '260' and r > 0 and l[2] == '0':
                rows.append((r, ['END']))
                break
            rows.append((r, l))
        mm.close()
    return rows


def enemyish(i):
    return i == '' or i[:1] in ('4', 'E')


def deaths(rows, me):
    """Deaths after the cast, with the last hit that took HP and the mechanic statuses held then."""
    held = {}      # target id -> {status: since}
    last = {}      # target id -> (r, source, ability, damage, hp before, max hp)
    names = {}
    out = []
    for r, l in rows:
        ty = l[0]
        if ty in ('26', '30') and enemyish(l[5]) and l[7][:1] == '1':
            names[l[7]] = l[8]
            st = held.setdefault(l[7], {})
            if ty == '26':
                st[l[3]] = r
            else:
                st.pop(l[3], None)
        elif ty in ('21', '22') and l[6][:1] == '1' and enemyish(l[2]):
            d, _ = effects(l)
            names[l[6]] = l[7]
            if d > 0:
                last[l[6]] = (r, l[3], l[5], d, l[24], l[25])
        elif ty == '25' and r >= 0 and l[2][:1] == '1':
            h = last.get(l[2])
            out.append({'r': r, 'id': l[2], 'name': l[3], 'by': l[5], 'hit': h,
                        'st': {k: v for k, v in held.get(l[2], {}).items()
                               if k not in ('Weakness', 'Brink of Death', 'Damage Down')},
                        'me': l[2] in me})
    return out


def breaks(rows, me):
    """The earth crystal's pulses and what set each one off: an Accretion healed off (the heal that did it) or a Primordial
    Crust broken (the hit that would have killed: the Crust leaves you on 1 HP). A pulse inside the last one's 1.96 s Earth
    Resistance Down II is the lethal one."""
    lasthit = {}   # target id -> (r, ability, source, damage, hp, maxhp)
    lastheal = {}  # target id -> (r, ability, source, amount, hp, maxhp)
    lasers = {}
    names = {}
    out = []
    pulse_at = None
    for r, l in rows:
        ty = l[0]
        if ty in ('21', '22') and l[6][:1] == '1':
            d, h = effects(l)
            names[l[6]] = l[7]
            if d > 0 and enemyish(l[2]):
                lasthit[l[6]] = (r, l[5], l[3], d, l[24], l[25])
                if l[5] == 'Earthquake' and (pulse_at is None or r - pulse_at > 0.3):
                    pulse_at = r
                    out.append((r, 'PULSE', f'Earthquake pulse, {d:,} on {l[7]}'))
            if l[5] == 'Nothingness':
                lasers[l[6]] = lasers.get(l[6], 0) + 1
            if h > 0:
                lastheal[l[6]] = (r, l[5], l[3], h, l[24], l[25])
        elif ty == '30' and l[3] in ('Accretion', 'Primordial Crust') and l[7][:1] == '1':
            who = ('YOU ' if l[7] in me else '') + l[8]
            if l[3] == 'Accretion':
                h = lastheal.get(l[7])
                why = f"healed off by {h[2]}'s {h[1]} (+{h[3]:,} on {int(h[4]):,}/{int(h[5]):,}) {r - h[0]:.1f} s before" if h else 'no heal seen'
                out.append((r, 'ACCRETION', f'{who}: {why}'))
            else:
                h = lasthit.get(l[7])
                why = (f"{h[2]}'s {h[1]} {h[3]:,} on {int(h[4]):,}/{int(h[5]):,} HP, {r - h[0]:.1f} s before"
                       if h else 'no hit seen')
                n = lasers.get(l[7], 0)
                out.append((r, 'CRUST', f"{who}{' (' + str(n) + ' lasers)' if n else ''}: {why}"))
        elif ty == '25' and l[2][:1] == '1':
            out.append((r, 'DEATH', ('YOU ' if l[2] in me else '') + l[3]))
    return out


def detail(rows, me):
    for r, l in rows:
        ty = l[0]
        if ty == 'END':
            print(f'{r:7.2f} END OF COMBAT')
        elif ty == '20' and l[2][:1] == '4':
            print(f'{r:7.2f} CAST {l[3]} {l[5]} -> {l[7]} ({l[8]} s)')
        elif ty == '20' and l[2] in me:
            print(f'{r:7.2f}   ME cast {l[5]} -> {l[7]} ({l[8]} s)')
        elif ty == '23' and l[2] in me:
            print(f'{r:7.2f}   ME cancel {l[5]}')
        elif ty in ('21', '22') and l[2] in me:
            d, h = effects(l)
            amt = (f' dmg {d}' if d else '') + (f' heal {h}' if h else '')
            print(f'{r:7.2f}   ME {l[5]} -> {l[7]}{amt} hp {l[24]}/{l[25]}')
        elif ty in ('21', '22') and enemyish(l[2]) and l[6][:1] == '1':
            d, h = effects(l)
            print(f'{r:7.2f} HIT {l[3]} {l[5]} -> {l[7]} dmg {d} hp {l[24]}/{l[25]}')
        elif ty in ('26', '30') and enemyish(l[5]) and l[7][:1] == '1':
            print(f'{r:7.2f} {"GAIN" if ty == "26" else "LOSE"} {l[3]} ({float(l[4]):.1f} s) on {l[8]} from {l[6] or "-"}')
        elif ty in ('26', '30') and l[7] in me:
            print(f'{r:7.2f}   me {"+" if ty == "26" else "-"}{l[3]} ({float(l[4]):.1f} s) from {l[6]}')
        elif ty == '25':
            print(f'{r:7.2f} DEATH {l[3]} by {l[5]}')


def main():
    args = sys.argv[1:]
    opt = {'--me': '10041112,1001A4A8', '--job': None, '--since': None, '--detail': None, '--from': '-10', '--to': '60',
           '--breaks': False}
    paths = []
    i = 0
    while i < len(args):
        if args[i] == '--breaks':
            opt['--breaks'] = True
            i += 1
        elif args[i] in opt:
            opt[args[i]] = args[i + 1]
            i += 2
        else:
            paths.append(args[i])
            i += 1
    me = set(opt['--me'].split(','))
    logs = []
    for p in paths:
        if os.path.isdir(p):
            logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f))
        else:
            logs.append(p)
    bhs = []
    for p in logs:
        bhs += [b for b in scan(p, me, opt['--since']) if not opt['--job'] or b['job'] == opt['--job'].upper()]
    for n, b in enumerate(bhs):
        pt = (b['T'] - b['start']).total_seconds() if b['start'] else 0
        print(f"#{n} {b['T']:%Y-%m-%d %H:%M:%S}  {os.path.basename(b['path'])}  you {b['job']}  Black Hole at "
              f"{int(pt // 60)}:{pt % 60:04.1f}")
        if opt['--detail'] is not None:
            continue
        if opt['--breaks']:
            for r, kind, text in breaks(window(b, me, -25, 75), me):
                print(f'    {r:6.1f}  {kind:9} {text}')
            continue
        rows = window(b, me, -20, 75)
        ended = next((r for r, l in rows if l[0] == 'END'), None)
        for d in deaths(rows, me):
            h = d['hit']
            hit = f"{h[2]} ({h[1]}) {h[3]:,} on {int(h[4]):,}/{int(h[5]):,} HP" if h else d['by']
            st = ', '.join(sorted(d['st'])) or '-'
            print(f"    {d['r']:6.1f}  {'YOU ' if d['me'] else ''}{d['name']}: {hit} | holding: {st}")
        print(f"    combat ended at {ended:+.1f} s" if ended is not None else '    combat continued past +75 s')
    if opt['--detail'] is not None:
        b = bhs[int(opt['--detail'])]
        print()
        detail(window(b, me, float(opt['--from']), float(opt['--to'])), me)


if __name__ == '__main__':
    main()
