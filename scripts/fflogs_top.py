#!/usr/bin/env python3
"""Top FFLogs parses of a job on an encounter (2026-11-15).

  fflogs_top.py <spec> [--encounter 1085] [--metric rdps] [--n 10] [--page 1]

Prints rank, name, server, amount, duration and <report code>/<fight id> (what fflogs_pull.sh takes). Spec is FFLogs'
job name ("Gunbreaker", "Paladin", "Sage"...). Dancing Mad (Ultimate) is encounter 1085; dungeons have no rDPS (use
--metric dps). Credentials come from PartyVet's config through WrathCombo's utils/ff.py (UTILS env var to override the
folder); never printed. Run without python -I (ff.py is imported from that folder).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.environ.get('UTILS', os.path.join(HERE, '..', '..', 'WrathCombo', 'utils')))
import ff  # noqa: E402

Q = """query($enc: Int!, $spec: String!, $metric: CharacterRankingMetricType!, $page: Int!) {
  worldData { encounter(id: $enc) { name characterRankings(specName: $spec, metric: $metric, page: $page) } } }"""


def main():
    p = argparse.ArgumentParser()
    p.add_argument('spec')
    p.add_argument('--encounter', type=int, default=1085)
    p.add_argument('--metric', default='rdps')
    p.add_argument('--n', type=int, default=10)
    p.add_argument('--page', type=int, default=1)
    a = p.parse_args()
    d = ff.gql(Q, {'enc': a.encounter, 'spec': a.spec, 'metric': a.metric, 'page': a.page})
    enc = d['worldData']['encounter']
    r = enc['characterRankings']
    if isinstance(r, str):
        r = json.loads(r)
    print(f"{enc['name']}: {a.spec} by {a.metric}")
    for i, x in enumerate(r.get('rankings', [])[:a.n], 1):
        rep = x.get('report', {})
        srv = x.get('server', {})
        print(f"{i:3}  {x.get('name', '?'):24} {srv.get('name', '?'):14} {x.get('amount', 0):10,.0f}  "
              f"{x.get('duration', 0) / 1000:6.0f} s  {rep.get('code')}/{rep.get('fightID')}")


if __name__ == '__main__':
    main()
