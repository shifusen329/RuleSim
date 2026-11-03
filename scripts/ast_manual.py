#!/usr/bin/env python3
"""What an Astrologian pressed by hand, and in what situation (2026-11-03).

Every action the Astrologian used in an ACT log is matched to the button pressed for it (AnoMech's "Action pressed" lines
in dalamud.log): one of the Custom Rules buttons (Wrath's custom actions 1000000-1000003, or the buttons the rule modes
replace: Malefic, Gravity, Benefic, Helios) is the rules; any other button is a manual press. For each action this prints
the uses by the rules and by hand, and for the manual ones the situation: enemies being fought, the tank's HP, the party's
average and lowest HP, your target's HP, trash or a single enemy. Manual presses are what the rules did not do in time.

  ast_manual.py <ACT Network log> --dalamud <dalamud.log> [...] [--from ISO] [--to ISO] [--me 10041112]
                [--zones dungeon|all] [--detail ACTION_NAME]
"""
import argparse, bisect, collections, datetime as dt, os, re, statistics, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ast_cards import parse_time, seek_time, NOT_DUNGEON  # noqa: E402

RULE_BUTTONS = set(range(1000000, 1000004)) | {3596, 3598, 7442, 16555, 25871, 3615, 25872, 3594, 3600}
TANK_JOBS = {0x01, 0x03, 0x13, 0x15, 0x20, 0x25}
COMBUST = {'346', '34B', '759'}   # Combust, Combust II, Combust III


def press_log(paths, t0, t1):
    pat = re.compile(r'^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) .*Action pressed: (\d+) \(Action\) -- .*\(job=33\)')
    out = []
    for p in paths:
        with open(p, encoding='utf-8', errors='replace') as f:
            for line in f:
                m = pat.match(line)
                if m:
                    t = parse_time(m.group(1))
                    if t0 - dt.timedelta(minutes=5) <= t <= t1 + dt.timedelta(minutes=5):
                        out.append((t, int(m.group(2))))
    return sorted(out)


def med(xs):
    xs = [x for x in xs if x is not None]
    return f'{statistics.median(xs):.0f}' if xs else '-'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log'); ap.add_argument('--dalamud', nargs='+', required=True)
    ap.add_argument('--from', dest='frm'); ap.add_argument('--to'); ap.add_argument('--me', default='10041112')
    ap.add_argument('--zones', default='dungeon'); ap.add_argument('--detail', default=None)
    a = ap.parse_args()
    me = a.me.upper()
    t_from = dt.datetime.fromisoformat(a.frm) if a.frm else None
    t_to = dt.datetime.fromisoformat(a.to) if a.to else dt.datetime(2100, 1, 1)

    uses = []                      # (t, action id, name, context dict)
    dots = {}                      # enemy id -> when your Combust on it runs out
    hp, maxhp, jobs = {}, {}, {}   # party id -> HP, max HP, job
    hits = collections.deque()     # (t, enemy id) party hits on enemies, last 6 s
    zone, myjob, level, incombat, seg_enemies = '?', None, None, False, set()
    seen = set()
    size = os.path.getsize(a.log)
    with open(a.log, 'rb') as f:
        f.seek(seek_time(f, size, t_from) if t_from else 0)
        for raw in f:
            c = raw[:3]
            if c not in (b'01|', b'03|', b'21|', b'22|', b'26|', b'30|', b'37|', b'38|', b'39|') and raw[:4] != b'260|':
                continue
            p = raw.decode('utf-8', 'replace').split('|')
            t = parse_time(p[1])
            if t_from and t < t_from: continue
            if t > t_to: break
            k = p[0]
            if k == '01':
                zone = p[3]; jobs.clear(); hp.clear(); maxhp.clear(); continue
            if k == '03':
                if p[2].startswith('10'): jobs[p[2]] = int(p[4], 16)
                if p[2] == me: myjob, level = int(p[4], 16), int(p[5], 16)
                continue
            if k == '260':
                now = p[3] == '1'
                if now and not incombat: seg_enemies = set()
                incombat = now; continue
            if k in ('26', '30'):
                # Your Combust on an enemy: when it runs out (26 gains, 30 loses).
                if p[5] == me and p[2] in COMBUST and p[7].startswith('4'):
                    dots[p[7]] = t + dt.timedelta(seconds=float(p[4] or 0)) if k == '26' else t
                continue
            if k in ('37', '38', '39'):
                i = 4 if k == '39' else 5
                if p[2].startswith('10') and len(p) > i + 1 and p[i].isdigit() and p[i + 1].isdigit() and int(p[i + 1]) > 0:
                    hp[p[2]], maxhp[p[2]] = int(p[i]), int(p[i + 1])
                continue
            # 21 / 22: an ability
            src, tgt = p[2], p[6]
            if len(p) > 25 and p[24].isdigit() and p[25].isdigit() and int(p[25]) > 0 and tgt.startswith('10'):
                hp[tgt], maxhp[tgt] = int(p[24]), int(p[25])
            if src.startswith('10') and tgt.startswith('4'):
                hits.append((t, tgt)); seg_enemies.add(tgt)
            while hits and (t - hits[0][0]).total_seconds() > 6: hits.popleft()
            if src != me or myjob != 0x21: continue
            key = (p[4], p[44] if len(p) > 44 else p[1])
            if key in seen: continue
            seen.add(key)
            party = [i for i in hp if i in jobs]
            pct = {i: 100.0 * hp[i] / maxhp[i] for i in party}
            tanks = [pct[i] for i in party if jobs[i] in TANK_JOBS]
            target_hp = 100.0 * int(p[24]) / int(p[25]) if tgt.startswith('4') and p[25].isdigit() and int(p[25]) > 0 else None
            ctx = dict(zone=zone, level=level, combat=incombat, enemies=len({e for _, e in hits}),
                       pull_enemies=len(seg_enemies), tank=min(tanks) if tanks else None,
                       party=statistics.mean(pct.values()) if pct else None, low=min(pct.values()) if pct else None,
                       target=target_hp, dot=((dots[tgt] - t).total_seconds() if tgt in dots else None), on=('tank' if tgt in jobs and jobs[tgt] in TANK_JOBS else 'self' if tgt == me else
                                             'party' if tgt.startswith('10') else 'enemy' if tgt.startswith('4') else '-'))
            uses.append((t, int(p[4], 16), p[5], ctx))

    if a.zones == 'dungeon':
        uses = [u for u in uses if not NOT_DUNGEON.search(u[3]['zone'])]
    if not uses:
        print('no Astrologian actions in range'); return
    pr = press_log(a.dalamud, uses[0][0], uses[-1][0])
    # Clock offset (Dalamud - ACT): median gap from a non-rule press to the same action's use within 4 s.
    by_action = collections.defaultdict(list)
    for t, aid, _, _ in uses: by_action[aid].append(t)
    gaps = []
    for pt, pid in pr:
        if pid in RULE_BUTTONS: continue
        ts = by_action.get(pid)
        if not ts: continue
        i = bisect.bisect_left(ts, pt - dt.timedelta(seconds=4))
        if i < len(ts) and abs((ts[i] - pt).total_seconds()) < 4: gaps.append((ts[i] - pt).total_seconds())
    off = statistics.median(gaps) if gaps else 0.0
    manual_presses = collections.defaultdict(list)
    for pt, pid in pr:
        if pid not in RULE_BUTTONS: manual_presses[pid].append(pt + dt.timedelta(seconds=off))

    rows = collections.defaultdict(lambda: {'rules': 0, 'manual': []})
    for t, aid, name, ctx in uses:
        ps = manual_presses.get(aid, [])
        i = bisect.bisect_right(ps, t + dt.timedelta(seconds=0.4))
        manual = i > 0 and (t - ps[i - 1]).total_seconds() < 2.5
        r = rows[name]
        if manual: r['manual'].append((t, ctx))
        else: r['rules'] += 1

    print(f'clock offset Dalamud-ACT {off:+.2f} s from {len(gaps)} presses; zones: '
          + ', '.join(sorted({u[3]["zone"] for u in uses})))
    print(f'{"action":24} {"rules":>5} {"manual":>6}  manual: on (tank/self/party/enemy)  in a pack(3+)  '
          f'tank HP  party avg  lowest  target HP   (medians)')
    for name, r in sorted(rows.items(), key=lambda x: -len(x[1]['manual'])):
        m = r['manual']
        if not m and r['rules'] == 0: continue
        on = collections.Counter(c['on'] for _, c in m)
        pack = sum(1 for _, c in m if c['enemies'] >= 3)
        print(f'{name:24} {r["rules"]:5} {len(m):6}  {on["tank"]:>3}/{on["self"]}/{on["party"]}/{on["enemy"]:<3}'
              f'                {pack:3}/{len(m):<3}     {med(c["tank"] for _, c in m):>4}  {med(c["party"] for _, c in m):>8}  '
              f'{med(c["low"] for _, c in m):>6}  {med(c["target"] for _, c in m):>8}')
    if a.detail:
        print(f'\n{a.detail}, manual:')
        for t, c in rows.get(a.detail, {'manual': []})['manual']:
            print(f'  {t:%m-%d %H:%M:%S} {c["zone"][:22]:22} lv{c["level"]} {"" if c["combat"] else "(out of combat) "}'
                  f'on {c["on"]:5} enemies {c["enemies"]} (pull {c["pull_enemies"]})  tank {c["tank"] or 0:.0f}%  '
                  f'party {c["party"] or 0:.0f}%  lowest {c["low"] or 0:.0f}%  target {c["target"] or 0:.0f}%'
                  + (f'  your Combust on it: {c["dot"]:.1f} s left' if c.get('dot') is not None else ''))


if __name__ == '__main__':
    main()
