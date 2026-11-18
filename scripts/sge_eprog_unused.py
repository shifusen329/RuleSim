#!/usr/bin/env python3
"""Eukrasian Prognosis II shields that nothing used, in ACT logs (2026-11-18).

  sge_eprog_unused.py <ACT log or folder> [...] [--since 20261020] [--me 10041112,1001A4A8] [--list]

Every Eukrasian Prognosis II the player (--me) cast in Dancing Mad (zone 553): the Eukrasian Prognosis shields it put on (26 lines from
the player within 2 s of the cast) and, for each, whether the player under it took a hit while it was up (any enemy or environment
21/22 line with a damage effect, a fully absorbed one included, or a damage-over-time tick) before it came off (its 30 line).
A shield nothing hit ran out (on for 29 s or more), was replaced or removed early (another shield, a death), or was still up when the
log's pull ended. Reports the casts where no shield was hit (all 8, or every one it reached), per-shield totals, and the casts' times
in the pull (from the in-combat 260 line) for the wasted ones.
"""
import bisect
import collections
import datetime as dt
import mmap
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dmu_black_hole import ts  # noqa: E402

CAST = b'|Eukrasian Prognosis II|'
STATUS = 'Eukrasian Prognosis'


def positions(mm, pat):
    out, p = [], -1
    while True:
        p = mm.find(pat, p + 1)
        if p < 0:
            return out
        out.append(p)


def line(mm, p):
    s = mm.rfind(b'\n', 0, p) + 1
    e = mm.find(b'\n', p)
    e = len(mm) if e < 0 else e
    return s, e, mm[s:e].decode('utf-8', 'replace').rstrip('\r').split('|')


def is_hit(l, target):
    """A 21/22 line on the target from an enemy or the environment with a damage effect (any amount), or a DoT tick on it."""
    if l[0] == '24':
        return len(l) > 4 and l[2] == target and l[4] == 'DoT'
    if l[0] not in ('21', '22') or len(l) < 24 or l[6] != target or l[2][:1] not in ('4', 'E'):
        return False
    for i in range(8, 24, 2):
        if l[i] and int(l[i], 16) & 0xFF in (3, 5, 6):
            return True
    return False


def scan(path, me, since):
    out = []
    with open(path, 'rb') as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
        zones = positions(mm, b'\n01|')
        combats = positions(mm, b'\n260|')
        seen = set()
        for p in positions(mm, CAST):
            s, e, l = line(mm, p)
            if l[0] not in ('21', '22') or l[2] not in me:
                continue
            T = ts(l[1])
            key = (l[2], l[1][:21])      # one cast: its 22 lines share the timestamp to 0.1 ms
            if key in seen:
                continue
            seen.add(key)
            if since and T.strftime('%Y%m%d') < since:
                continue
            zi = bisect.bisect_left(zones, s) - 1
            if zi < 0:
                continue
            z = line(mm, zones[zi] + 1)[2]
            if len(z) < 3 or z[2] != '553':
                continue
            # The pull's start: the last 260 line (in game combat 1) since the zone line.
            start = None
            ci = bisect.bisect_left(combats, s) - 1
            while ci >= 0 and combats[ci] > zones[zi]:
                c = line(mm, combats[ci] + 1)[2]
                if len(c) > 3 and c[3] == '1':
                    start = ts(c[1])
                    if ci == 0 or line(mm, combats[ci - 1] + 1)[2][3:4] != ['1']:
                        break
                elif len(c) > 3 and c[3] == '0':
                    break
                ci -= 1
            # Shields on, then each one's end and the hits under it (to 32 s after the cast, or the end of combat).
            shields = {}      # target id -> [on, off, hit, name]
            o = s
            end_combat = None
            while o < len(mm):
                e2 = mm.find(b'\n', o)
                e2 = len(mm) if e2 < 0 else e2
                raw = mm[o:e2]
                o = e2 + 1
                if raw[:3] not in (b'26|', b'30|', b'21|', b'22|', b'24|', b'260', b'01|'):
                    continue
                x = raw.decode('utf-8', 'replace').rstrip('\r').split('|')
                try:
                    r = (ts(x[1]) - T).total_seconds()
                except (IndexError, ValueError):
                    continue
                if r > 32 or x[0] == '01':
                    break
                if x[0] == '260' and len(x) > 3 and x[3] == '0' and r > 0:
                    end_combat = r
                    break
                if x[0] == '26' and x[3] == STATUS and x[5] in me and r <= 2 and x[7][:1] == '1' and x[7] not in shields:
                    shields[x[7]] = [r, None, False, x[8]]
                elif x[0] == '30' and x[3] == STATUS and x[7] in shields and shields[x[7]][1] is None:
                    shields[x[7]][1] = r
                elif x[0] in ('21', '22', '24'):
                    tgt = x[2] if x[0] == '24' else (x[6] if len(x) > 6 else '')
                    sh = shields.get(tgt)
                    if sh and sh[1] is None and not sh[2] and is_hit(x, tgt):
                        sh[2] = True
            out.append({'T': T, 'pull': (T - start).total_seconds() if start else None, 'shields': shields, 'end': end_combat,
                        'file': os.path.basename(path)})
        mm.close()
    return out


def main():
    args = sys.argv[1:]
    me, since, show, paths = {'10041112', '1001A4A8'}, '20261020', False, []
    i = 0
    while i < len(args):
        if args[i] == '--me':
            me = set(args[i + 1].split(',')); i += 2
        elif args[i] == '--since':
            since = args[i + 1]; i += 2
        elif args[i] == '--list':
            show = True; i += 1
        else:
            paths.append(args[i]); i += 1
    logs = []
    for p in paths:
        logs += sorted(os.path.join(p, f) for f in os.listdir(p) if re.match(r'Network_\d+_\d+\.log$', f)) if os.path.isdir(p) else [p]
    casts = []
    for p in logs:
        casts += scan(p, me, since)
    fate = collections.Counter()
    n_by_size = collections.Counter()
    wasted, wasted8, all8 = [], [], 0
    for c in casts:
        sh = c['shields']
        n_by_size[len(sh)] += 1
        for on, off, hit, _ in sh.values():
            fate['hit' if hit else 'ran out untouched' if off is not None and off - on >= 29 else
                 'removed early untouched' if off is not None else 'pull ended, untouched'] += 1
        if len(sh) == 8:
            all8 += 1
        if sh and not any(v[2] for v in sh.values()):
            wasted.append(c)
            if len(sh) == 8:
                wasted8.append(c)
    print(f"{len(casts)} Eukrasian Prognosis II casts in Dancing Mad ({len(logs)} logs from {since}); shields put on per cast: {dict(sorted(n_by_size.items()))}")
    print(f"shields: {sum(fate.values())}: {dict(fate.most_common())}")
    print(f"casts that reached all 8: {all8}; of those, no shield hit: {len(wasted8)}")
    print(f"casts with no shield hit at all (any number reached): {len(wasted)}")
    def why(c):
        f = collections.Counter('ran out' if off is not None and off - on >= 29 else 'removed early' if off is not None else 'pull ended'
                                for on, off, hit, _ in c['shields'].values())
        return ', '.join(f'{k} {v}' for k, v in f.most_common())
    buckets = collections.Counter(f"{int(c['pull'] // 60)}:{int(c['pull'] % 60) // 10 * 10:02d}" if c['pull'] is not None else '?' for c in wasted8)
    print("wasted 8-player casts by time in the pull (10 s):", dict(sorted(buckets.items())))
    if show:
        for c in sorted(wasted8, key=lambda c: c['T']):
            t = c['pull']
            print(f"  {c['T']:%Y-%m-%d %H:%M:%S}  {int(t // 60)}:{t % 60:04.1f} in the pull  {why(c)}" if t is not None else f"  {c['T']}  {why(c)}")


if __name__ == '__main__':
    main()
