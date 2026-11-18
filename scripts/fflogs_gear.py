#!/usr/bin/env python3
"""A job's stats and item levels in fetched FFLogs fights (2026-11-18), from the fights' combatantinfo events.

  fflogs_gear.py <fight dir (events.json + meta.json)> [...] [--job Sage]

The fight dirs are what WrathCombo's utils/fetch.py writes. Prints, per fight, the players on the job: main stat, Critical Hit,
Determination, Direct Hit, Spell/Skill Speed, Piety/Tenacity and the gear's average item level (the weapon counted once, the
off-hand slot skipped when empty).
"""
import json
import os
import sys

args = sys.argv[1:]
job = 'Sage'
if '--job' in args:
    i = args.index('--job'); job = args[i + 1]; del args[i:i + 2]


def events(path):
    ev = json.load(open(path, encoding='utf-8'))
    return ev if isinstance(ev, list) else ev.get('events') or ev.get('data') or []


def actors(meta):
    """id -> (name, job) from the meta's report actors, wherever fetch.py put them."""
    out = {}
    def walk(x):
        if isinstance(x, dict):
            if 'id' in x and 'name' in x and ('subType' in x or 'type' in x):
                out[x['id']] = (x['name'], x.get('subType') or x.get('type'))
            for v in x.values():
                walk(v)
        elif isinstance(x, list):
            for v in x:
                walk(v)
    walk(meta)
    return out


KEYS = [('mind', 'Mind'), ('intelligence', 'Int'), ('criticalHit', 'Crit'), ('determination', 'Det'), ('directHit', 'DH'),
        ('spellSpeed', 'SpS'), ('skillSpeed', 'SkS'), ('piety', 'Pie'), ('tenacity', 'Ten')]
for d in args:
    meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
    who = actors(meta)
    for e in events(os.path.join(d, 'events.json')):
        if not isinstance(e, dict) or e.get('type') != 'combatantinfo':
            continue
        name, j = who.get(e.get('sourceID'), ('?', '?'))
        if str(j).lower() != job.lower():
            continue
        gear = [g for g in e.get('gear', []) if g.get('id')]
        ilvls = [g.get('itemLevel', 0) for g in gear if g.get('itemLevel')]
        stats = '  '.join(f"{lab} {e[k]}" for k, lab in KEYS if e.get(k) not in (None, 0))
        print(f"{os.path.basename(os.path.normpath(d)):22} {name:20} avg ilvl {sum(ilvls) / max(1, len(ilvls)):5.1f} ({len(ilvls)} items)  {stats}")
