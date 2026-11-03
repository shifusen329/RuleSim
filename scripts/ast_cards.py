#!/usr/bin/env python3
"""Astrologian card flow in an ACT log, with the button you pressed for each card (2026-11-03).

The user played cards by hand in dungeons because the rules held them. This shows, for one Astrologian, every draw, card,
Lord/Lady and Divination: whether a rule or a manual press used it, and the state the rules saw (Divination's cooldown,
how long the Play I card had been held, the draw's cooldown, your target's HP).

  ast_cards.py <ACT Network log> [--me 10041112] [--from 2026-11-02T21:00] [--to ...]
               [--dalamud <dalamud.log> ...] [--zones dungeon|all] [--detail]

Manual presses come from AnoMech's "[LocalPlayerInputHooks] Action pressed: <id>" lines in dalamud.log: a card's own id
(e.g. 37023 The Balance) is a manual press; 1000000-1000003 or a replaced button (Malefic, Gravity, Benefic, Helios) is
the rules. The Dalamud-to-ACT clock offset is measured from the presses themselves.
"""
import argparse, bisect, collections, datetime as dt, os, re, statistics, sys

DRAWS = {37017: 'Astral Draw', 37018: 'Umbral Draw'}
PLAY1 = {37023: 'The Balance', 37026: 'The Spear'}
PLAY23 = {37024: 'The Arrow', 37027: 'The Bole', 37025: 'The Spire', 37028: 'The Ewer'}
CROWN = {7444: 'Lord of Crowns', 7445: 'Lady of Crowns'}
DIV = 16552
CARDISH = {**DRAWS, **PLAY1, **PLAY23, **CROWN, DIV: 'Divination', 37029: 'Oracle'}
# The buttons that press a card for you: the Play buttons and Minor Arcana map to whatever card is held.
PLAYBTN = {37019: PLAY1, 37020: {37024: 1, 37027: 1}, 37021: {37025: 1, 37028: 1}, 37022: CROWN}
RULE_BUTTONS = set(range(1000000, 1000004)) | {3596, 3598, 7442, 16555, 25871, 3615, 25872, 3594, 3600}
DUNGEON_ZONES = None  # filled from the 01 lines' names when --zones dungeon (see is_dungeon)
NOT_DUNGEON = re.compile(r'Syrcus Tower|Labyrinth of the Ancients|Crystal Tower|World of Darkness|Yak T.el|Kozama|Urqopacha|'
                         r'Shaaloani|Heritage Found|Living Memory|Tuliyollal|Solution Nine|Kobai Goten|Shirogane|Apartment|'
                         r'Limsa|Gridania|Ul.dah|Mist|Lavender|Goblet|Empyreum|Dancing Mad', re.I)


def parse_time(s):
    # ACT: 2026-11-03T13:56:53.1380000-06:00   Dalamud: 2026-11-03 13:56:52.550 -06:00
    s = s.replace('T', ' ')
    return dt.datetime.strptime(s[:23], '%Y-%m-%d %H:%M:%S.%f')


def seek_time(f, size, when):
    """Byte offset of the first line at or after `when` (binary search on line timestamps)."""
    lo, hi = 0, size
    while hi - lo > 1 << 16:
        mid = (lo + hi) // 2
        f.seek(mid); f.readline()
        line = f.readline()
        while line and not re.match(rb'^\d+\|\d{4}-', line):
            line = f.readline()
        if not line: hi = mid; continue
        t = parse_time(line.split(b'|')[1].decode())
        if t < when: lo = mid
        else: hi = mid
    return lo


def presses(paths, t0, t1):
    out = []
    # "Action pressed: 37023 (Action) -- the Balance (job=33)" or "... 1000000 (Action) -- 1000000 (job=33)"
    pat = re.compile(r'^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) .*Action pressed: (\d+) \(Action\) -- .*\(job=33\)')
    for p in paths:
        with open(p, encoding='utf-8', errors='replace') as f:
            for line in f:
                m = pat.match(line)
                if m:
                    t = parse_time(m.group(1))
                    if t0 - dt.timedelta(minutes=5) <= t <= t1 + dt.timedelta(minutes=5):
                        out.append((t, int(m.group(2))))
    out.sort()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log'); ap.add_argument('--me', default='10041112')
    ap.add_argument('--from', dest='frm', default=None); ap.add_argument('--to', default=None)
    ap.add_argument('--dalamud', nargs='*', default=[]); ap.add_argument('--zones', default='dungeon')
    ap.add_argument('--detail', action='store_true')
    a = ap.parse_args()
    me = a.me.upper()
    size = os.path.getsize(a.log)
    t_from = dt.datetime.fromisoformat(a.frm) if a.frm else None
    t_to = dt.datetime.fromisoformat(a.to) if a.to else dt.datetime(2100, 1, 1)

    events, visits = [], []   # events: (time, kind, data); visits: [zone, start, end, levels, combat seconds]
    zone, job, lvl, incombat, cstart = '?', None, None, False, None
    target_hp = {}            # enemy id -> last HP% seen
    my_target = None
    seen = set()
    with open(a.log, 'rb') as f:
        f.seek(seek_time(f, size, t_from) if t_from else 0)
        for raw in f:
            c = raw[:4]
            if not (c[:3] in (b'01|', b'03|', b'21|', b'22|') or c == b'260|'):
                continue
            p = raw.decode('utf-8', 'replace').split('|')
            t = parse_time(p[1])
            if t_from and t < t_from: continue
            if t > t_to: break
            if p[0] == '01':
                if visits: visits[-1][2] = t
                zone = p[3]; visits.append([zone, t, t, set(), 0.0, []]); continue
            if not visits: visits.append([zone, t, t, set(), 0.0, []])
            v = visits[-1]
            if p[0] == '03' and p[2] == me:
                job, lvl = int(p[4], 16), int(p[5], 16)
                if job == 0x21: v[3].add(lvl)
            elif p[0] == '260':
                now = p[3] == '1'
                if now and not incombat: cstart = t
                if incombat and not now and cstart: v[4] += (t - cstart).total_seconds()
                incombat = now
            elif p[0] in ('21', '22'):
                if p[6].startswith('4') and len(p) > 25 and p[25].isdigit() and int(p[25]) > 0:
                    target_hp[p[6]] = 100.0 * int(p[24]) / int(p[25])
                if p[2] != me: continue
                if p[6].startswith('4'): my_target = p[6]
                key = (p[4], p[44] if len(p) > 44 else p[1])
                if key in seen: continue
                seen.add(key)
                aid = int(p[4], 16)
                if aid in CARDISH and job == 0x21:
                    v[5].append((t, aid, incombat, target_hp.get(my_target)))
        if visits: visits[-1][2] = t

    ast_visits = [v for v in visits if v[3] and (a.zones == 'all' or not NOT_DUNGEON.search(v[0]))]
    if not ast_visits:
        print('no Astrologian visits in range'); return
    t0, t1 = ast_visits[0][1], ast_visits[-1][2]
    pr = presses(a.dalamud, t0, t1) if a.dalamud else []

    # Clock offset (Dalamud - ACT): the median gap from each manual card press to the same action's use 0-4 s later.
    all_uses = sorted((t, aid) for v in ast_visits for (t, aid, _, _) in v[5])
    gaps = []
    for (pt, pid) in pr:
        if pid not in CARDISH: continue
        for (ut, uid) in all_uses:
            d = (ut - pt).total_seconds()
            if uid == pid and -6 < d < 6: gaps.append(d); break
    off = statistics.median(gaps) if gaps else 0.0
    ptimes = [pt + dt.timedelta(seconds=off) for pt, _ in pr]

    def source(t, aid):
        """manual, rules or ? for a use at ACT time t: the press 0-1.5 s before it."""
        if not pr: return '?'
        i = bisect.bisect_right(ptimes, t + dt.timedelta(seconds=0.3))
        best = None
        for j in range(i - 1, max(-1, i - 25), -1):
            if (t - ptimes[j]).total_seconds() > 2.5: break
            pid = pr[j][1]
            if pid == aid or (pid in PLAYBTN and aid in PLAYBTN[pid]): return 'manual'
            if pid in RULE_BUTTONS and best is None: best = 'rules'
        return best or '?'

    tot = collections.Counter()
    print(f'clock offset Dalamud-ACT {off:+.2f} s from {len(gaps)} matched presses' if pr else 'no Dalamud press log given')
    for zone, start, end, levels, comb, uses in ast_visits:
        if not uses: continue
        c = collections.Counter()
        last_div, last_draw, card_since = None, None, None
        lines = []
        for t, aid, incomb, thp in uses:
            src = source(t, aid)
            name = CARDISH[aid]
            kind = ('draw' if aid in DRAWS else 'play1' if aid in PLAY1 else 'play23' if aid in PLAY23 else
                    'crown' if aid in CROWN else 'div' if aid == DIV else 'oracle')
            c[(kind, src)] += 1; tot[(kind, src)] += 1
            div_cd = max(0.0, 120 - (t - last_div).total_seconds()) if last_div else 0.0
            in_div = last_div is not None and (t - last_div).total_seconds() <= 20
            note = ''
            if kind == 'play1':
                held = (t - card_since).total_seconds() if card_since else None
                note = f"{'in Divination' if in_div else 'outside Divination'}; Divination cd {div_cd:.0f} s; card held {held:.0f} s" if held is not None else ''
                card_since = None
                c[('play1_in_div', in_div)] += 1; tot[('play1_in_div', in_div)] += 1
            elif kind == 'draw':
                ready = (t - last_draw).total_seconds() - 55 if last_draw else None
                note = (f'draw ready {ready:.0f} s before; ' if ready is not None and ready > 1 else '') + \
                       ('a Play I card was still held' if card_since else '')
                last_draw, card_since = t, t
            elif kind == 'div':
                note = f'target HP {thp:.0f}%' if thp is not None else 'no target HP'
                if last_div: note += f'; ready {(t - last_div).total_seconds() - 120:.0f} s before'
                last_div = t
            lines.append(f'    {t:%H:%M:%S} {src:6} {name:15} {"(out of combat) " if not incomb else ""}{note}')
        print(f'\n{zone}  {start:%m-%d %H:%M}-{end:%H:%M}  level {"/".join(map(str, sorted(levels)))}  combat {comb / 60:.1f} min')
        print('   ' + ', '.join(f'{k} {s} {n}' for (k, s), n in sorted(c.items(), key=lambda x: str(x[0])) if k != 'play1_in_div') +
              f"; Play I in Divination {c[('play1_in_div', True)]}/{c[('play1_in_div', True)] + c[('play1_in_div', False)]}")
        if a.detail: print('\n'.join(lines))
    print('\nTOTAL ' + ', '.join(f'{k} {s} {n}' for (k, s), n in sorted(tot.items(), key=lambda x: str(x[0]))))


if __name__ == '__main__':
    main()
