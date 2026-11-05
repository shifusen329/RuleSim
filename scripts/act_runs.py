#!/usr/bin/env python3
"""Your duty runs in ACT logs, by job, and cutting one out as its own log (2026-11-04).

Lists every zone visit (from one 01 zone-change line to the next) in the logs: start, end, zone, your job and level (your
03 line after the 02), seconds in combat (260 lines), whether the duty was completed (a 33 director line 40000003), and
the byte range. --job and --dungeon filter it (--dungeon: the duty's director, 8003xxxx in the 33 lines, is an
InstanceContent row under 10000: dungeons; guildhests are 10000+, trials 20000+, raids 30000+; Brayflox 8, Skalla 58,
Porta Decumana 20082, Emanation 20048). --last N keeps the newest N. --out DIR writes each listed visit as its
own ACT log, DIR/<n>_<zone>_<yyyymmdd_HHMM>/Network_<...>.log, with the AnoMech "Action pressed" lines of the same window
from --dalamud files beside it (dalamud_presses.log), so ast_manual.py / ast_cards.py run on it alone.

  act_runs.py <ACT log> [...] [--from ISO] [--job AST] [--dungeon] [--last 2] [--out DIR] [--dalamud dalamud.log ...]
"""
import argparse, datetime as dt, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ast_cards import parse_time, seek_time  # noqa: E402

JOBS = {0x13: 'PLD', 0x14: 'MNK', 0x15: 'WAR', 0x16: 'DRG', 0x17: 'BRD', 0x18: 'WHM', 0x19: 'BLM', 0x1A: 'ACN', 0x1B: 'SMN',
        0x1C: 'SCH', 0x1D: 'ROG', 0x1E: 'NIN', 0x1F: 'MCH', 0x20: 'DRK', 0x21: 'AST', 0x22: 'SAM', 0x23: 'RDM', 0x24: 'BLU',
        0x25: 'GNB', 0x26: 'DNC', 0x27: 'RPR', 0x28: 'SGE', 0x29: 'VPR', 0x2A: 'PCT'}


def is_dungeon(director):
    """A dungeon's director: 8003 (InstanceContent) and a row under 10000."""
    return len(director) == 8 and director.startswith('8003') and int(director[4:], 16) < 10000


def visits(path, t_from):
    """Zone visits in one log: dicts with start/end time, byte range, zone, job, level, combat s, done."""
    out, cur, me = [], None, None
    size = os.path.getsize(path)
    with open(path, 'rb') as f:
        pos = seek_time(f, size, t_from) if t_from else 0
        f.seek(pos)
        combat_since = last_t = None
        for line in f:
            start = pos; pos += len(line)
            k = line[:4]
            if not (k.startswith((b'01|', b'02|', b'03|', b'33|')) or k == b'260|'):
                continue
            p = line.decode('utf-8', 'replace').rstrip('\r\n').split('|')
            t = last_t = parse_time(p[1])
            if p[0] == '01':
                if cur:
                    if combat_since: cur['combat'] += (t - combat_since).total_seconds(); combat_since = None
                    cur['end'], cur['b1'] = t, start; out.append(cur)
                cur = {'log': path, 'start': t, 'end': None, 'b0': start, 'b1': None, 'zone': p[2], 'name': p[3],
                       'job': None, 'level': None, 'combat': 0.0, 'done': False, 'instance': False, 'director': ''}
            elif cur is None:
                continue
            elif p[0] == '02':
                me = p[2].upper()
            elif p[0] == '03' and me and p[2].upper() == me:
                cur['job'] = JOBS.get(int(p[4], 16), p[4]); cur['level'] = int(p[5], 16)
            elif p[0] == '33':
                cur['instance'] = True; cur['director'] = p[2].upper()
                if len(p) > 3 and p[3].upper() == '40000003':
                    cur['done'] = True
            elif p[0] == '260' and len(p) > 3:
                if p[3] == '1' and combat_since is None: combat_since = t
                elif p[3] == '0' and combat_since is not None:
                    cur['combat'] += (t - combat_since).total_seconds(); combat_since = None
        if cur:
            cur['end'], cur['b1'] = last_t or cur['start'], pos; out.append(cur)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('logs', nargs='+'); ap.add_argument('--from', dest='frm')
    ap.add_argument('--job'); ap.add_argument('--dungeon', action='store_true'); ap.add_argument('--last', type=int)
    ap.add_argument('--out'); ap.add_argument('--dalamud', nargs='*', default=[])
    a = ap.parse_args()
    t_from = dt.datetime.fromisoformat(a.frm) if a.frm else None
    vs = []
    for p in a.logs:
        vs += visits(p, t_from)
    vs.sort(key=lambda v: v['start'])
    if a.job: vs = [v for v in vs if v['job'] == a.job.upper()]
    if a.dungeon: vs = [v for v in vs if is_dungeon(v['director'])]
    if a.last: vs = vs[-a.last:]
    for i, v in enumerate(vs, 1):
        print(f"{i:2} {v['start']:%Y-%m-%d %H:%M}-{v['end']:%H:%M}  {v['zone']:>4} {v['name'][:34]:34} {v['job'] or '?':3} "
              f"Lv{v['level'] or '?':<3} combat {v['combat'] / 60:5.1f} min  {'complete' if v['done'] else '-       '}  "
              f"{os.path.basename(v['log'])} bytes {v['b0']}-{v['b1']}")
    if not a.out:
        return
    for i, v in enumerate(vs, 1):
        name = re.sub(r'[^A-Za-z0-9]+', '_', v['name']).strip('_')
        d = os.path.join(a.out, f"{i}_{name}_{v['start']:%Y%m%d_%H%M}")
        os.makedirs(d, exist_ok=True)
        dst = os.path.join(d, f"Network_30304_{v['start']:%Y%m%d}.log")
        with open(v['log'], 'rb') as f, open(dst, 'wb') as o:
            f.seek(v['b0']); left = v['b1'] - v['b0']
            while left > 0:
                b = f.read(min(left, 1 << 22)); o.write(b); left -= len(b)
        lo, hi = v['start'] - dt.timedelta(minutes=5), v['end'] + dt.timedelta(minutes=5)
        n = 0
        with open(os.path.join(d, 'dalamud_presses.log'), 'w', encoding='utf-8') as o:
            for p in a.dalamud:
                with open(p, encoding='utf-8', errors='replace') as f:
                    for line in f:
                        if 'Action pressed' in line and line[:4].isdigit():
                            t = parse_time(line[:23])
                            if lo <= t <= hi: o.write(line); n += 1
        print(f"-> {dst} ({(v['b1'] - v['b0']) / 1e6:.1f} MB), {n} presses")


if __name__ == '__main__':
    main()
