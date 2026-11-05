#!/usr/bin/env python3
"""A rule file in short form: its defines, then every rule as [action, weave, target] name / when (2026-11-04).

  rules_dump.py <rule file.json> [...]

Reads WrathCombo's commented JSON (// comments, trailing commas) the way the engine does.
"""
import json, re, sys


def load(path):
    s = open(path, encoding='utf-8-sig').read()
    out, i, ins = [], 0, False
    while i < len(s):
        c = s[i]
        if ins:
            out.append(c)
            if c == '\\':
                out.append(s[i + 1]); i += 2; continue
            if c == '"':
                ins = False
        elif c == '"':
            ins = True; out.append(c)
        elif s.startswith('//', i):
            while i < len(s) and s[i] != '\n':
                i += 1
            continue
        else:
            out.append(c)
        i += 1
    return json.loads(re.sub(r',(\s*[}\]])', r'\1', ''.join(out)))


def main():
    for f in sys.argv[1:]:
        d = load(f)
        print(f"===== {f}: {d.get('name')}  replaces {d.get('replaces')}  when {d.get('when')}  layer {d.get('layer', False)}")
        print('-- defines')
        for k, v in d.get('define', {}).items():
            print(f'  {k} = {v}')
        print('-- rules')
        for i, r in enumerate(d.get('rules', []), 1):
            tags = ''.join([' w' if r.get('weave') else '', ' hook' if r.get('hook') else '',
                            f" tgt={r['target']}" if r.get('target') else ''])
            print(f"{i:3}. [{r.get('action')}{tags}] {r.get('name')}\n      when: {r.get('when', '')}")


if __name__ == '__main__':
    main()
