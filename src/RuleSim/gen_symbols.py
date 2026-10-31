"""Generates <job>_symbols.json for the rule replay from WrathCombo's sources, the way JobRuleSymbols
(WrathCombo/CustomRules/RuleBuiltins.cs) builds its tables by reflection:

  actions:  "Wait" = All.Cease (1000004); every uint/ushort const declared in the job class (plain names);
            every role class in RoleActions (plain and "Role." names).
  statuses: the job's nested Buffs/Debuffs (plain and "Buffs."/"Debuffs." names); the role classes'
            nested Buffs/Debuffs (plain and "Role.Buffs." names); every other job's Buffs/Debuffs by
            qualified name only ("DNC.Buffs.Partner").
First definition wins (Dictionary.TryAdd), names are case-insensitive in the replay.

usage: python -I gen_symbols.py SAM out.json [repo root]
"""
import json
import os
import re
import sys

JOB = sys.argv[1].upper()
OUT = sys.argv[2]
ROOT = sys.argv[3] if len(sys.argv) > 3 else r"C:\Users\quanh\Workspace\WrathCombo\WrathCombo"
PVE = os.path.join(ROOT, "Combos", "PvE")


def strip_comments(src):
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", " ", src)
    # string literals (so braces inside them don't count)
    src = re.sub(r'@"(?:[^"]|"")*"', '""', src)
    src = re.sub(r'\$?"(?:\\.|[^"\\])*"', '""', src)
    src = re.sub(r"'(?:\\.|[^'\\])'", "''", src)
    return src


def parse_int(v):
    v = v.strip().replace("_", "")
    m = re.fullmatch(r"\(?\s*(?:u?int|ushort)?\s*\)?\s*(0x[0-9A-Fa-f]+|\d+)[uUlL]*", v)
    if not m:
        return None
    t = m.group(1)
    return int(t, 16) if t.lower().startswith("0x") else int(t)


def classes(src):
    """Yields (path tuple, body text) for every class, body = text directly inside it (nested bodies blanked)."""
    src = strip_comments(src)
    out = []
    stack = []  # (name or None, index of the opening brace, nested block spans)
    pending = None
    token = re.compile(r"\b(class|struct|record|interface|enum)\s+(\w+)|[{};]")
    for m in token.finditer(src):
        if m.group(1):
            pending = m.group(2) if m.group(1) in ("class", "struct", "record") else ("<" + m.group(1) + ">")
            continue
        if m.group(0) == ";":
            pending = None
            continue
        if m.group(0) == "{":
            stack.append((pending, m.start(), []))
            pending = None
            continue
        if not stack:
            continue
        name, open_at, holes = stack.pop()
        if name is not None and not name.startswith("<"):
            path = tuple(n for n, _, _ in stack if n is not None) + (name,)
            chars = list(src[open_at + 1:m.start()])
            for a, b in holes:  # blank nested blocks (methods, nested classes) so only direct members remain
                for k in range(a - open_at - 1, b - open_at - 1):
                    chars[k] = " "
            out.append((path, "".join(chars)))
        if stack:
            stack[-1][2].append((open_at, m.end()))
    return out


def consts(body):
    res = []
    for m in re.finditer(r"\bconst\s+(uint|ushort)\s+([^;]+);", body):
        for decl in m.group(2).split(","):
            if "=" not in decl:
                continue
            n, v = decl.split("=", 1)
            val = parse_int(v)
            if val is None:
                print(f"  skipped non-literal const {n.strip()} = {v.strip()}", file=sys.stderr)
                continue
            res.append((n.strip(), val))
    return res


def files_in(d):
    for dp, _, fs in os.walk(d):
        for f in sorted(fs):
            if f.endswith(".cs"):
                yield os.path.join(dp, f)


actions, statuses = {}, {}


def add(table, name, value):
    key = name.lower()
    if key not in {k.lower() for k in table}:
        table[name] = value


actions["Wait"] = 1_000_004

# the job class (all partial files in the job folder), then its Buffs/Debuffs
job_classes = []
for path in files_in(os.path.join(PVE, JOB)):
    job_classes += [(p, b) for p, b in classes(open(path, encoding="utf-8-sig").read()) if p[0] == JOB]
for p, b in job_classes:
    if p == (JOB,):
        for n, v in consts(b):
            add(actions, n, v)
for p, b in job_classes:
    if len(p) == 2 and p[1] in ("Buffs", "Debuffs"):
        for n, v in consts(b):
            add(statuses, n, v)
            add(statuses, f"{p[1]}.{n}", v)

# role actions
roles = []
for path in files_in(os.path.join(PVE, "ALL", "Roles")):
    roles += [(p, b) for p, b in classes(open(path, encoding="utf-8-sig").read()) if p[0] == "RoleActions"]
for p, b in roles:
    if len(p) == 2:
        for n, v in consts(b):
            add(actions, n, v)
            add(actions, f"{p[1]}.{n}", v)
for p, b in roles:
    if len(p) == 3 and p[2] in ("Buffs", "Debuffs"):
        for n, v in consts(b):
            add(statuses, n, v)
            add(statuses, f"{p[1]}.{p[2]}.{n}", v)

# other jobs' statuses, qualified only
for d in sorted(os.listdir(PVE)):
    full = os.path.join(PVE, d)
    if not os.path.isdir(full):
        continue
    for path in files_in(full):
        for p, b in classes(open(path, encoding="utf-8-sig").read()):
            if len(p) == 2 and re.fullmatch(r"[A-Z]{3}", p[0]) and p[1] in ("Buffs", "Debuffs"):
                for n, v in consts(b):
                    add(statuses, f"{p[0]}.{p[1]}.{n}", v)

json.dump({"actions": actions, "statuses": statuses}, open(OUT, "w"), indent=1)
print(f"{OUT}: {len(actions)} actions, {len(statuses)} statuses")
