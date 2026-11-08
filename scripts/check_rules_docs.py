#!/usr/bin/env python3
"""Check docs/CustomRules.md against the code and the bundled rule files (2026-11-08).

  check_rules_docs.py <WrathCombo repo root>

Reports: options the docs name that no bundled file defines; bundled files the docs don't name; rule values and functions in
RuleBuiltins.cs the docs don't list, and names the docs list that it doesn't define; target kinds in RuleSet.cs the docs don't
describe; and, for documented "<option> ... default" style facts, nothing: numbers in prose still need reading by hand.
"""
import os, re, sys


def main():
    root = sys.argv[1]
    doc = open(os.path.join(root, 'docs', 'CustomRules.md'), encoding='utf-8').read()
    ex = os.path.join(root, 'WrathCombo', 'CustomRules', 'Examples')
    files = sorted(f for f in os.listdir(ex) if f.endswith('.json'))
    text = {f: open(os.path.join(ex, f), encoding='utf-8').read() for f in files}
    alltext = '\n'.join(text.values())
    bad = 0

    def report(title, items):
        nonlocal bad
        items = sorted(set(items))
        print(f'{title}: {len(items)}')
        for i in items:
            print('   ', i)
        bad += len(items)

    opts = set(re.findall(r'`(opt[A-Z]\w+)`', doc))
    report('options named in the docs that no bundled file defines', [o for o in opts if f'"{o}"' not in alltext])
    # File names: exact, or the shared-prefix lists the docs use.
    report('bundled files the docs do not name', [f for f in files if f not in doc])

    src = open(os.path.join(root, 'WrathCombo', 'CustomRules', 'RuleBuiltins.cs'), encoding='utf-8').read()
    values = set(re.findall(r'\bValue\("(\w+)"', src))
    funcs = set(re.findall(r'\bAdd\("(\w+)"', src))
    report('rule values and functions the docs do not mention', [n for n in values | funcs if not re.search(r'`' + n + r'(\(|`)', doc)])

    listed = set()
    m = re.search(r'Values with no arguments:(.*?)\n\n', doc, re.S)
    if m:
        listed = set(re.findall(r'`(\w+)`', m.group(1)))
    report('names in the docs\' value list the code does not define', [n for n in listed if n not in values])

    rs = open(os.path.join(root, 'WrathCombo', 'CustomRules', 'RuleSet.cs'), encoding='utf-8').read()
    kinds = set(re.findall(r'case "(\w+)"(?: or "\w+")*: kind = RuleTarget', rs))
    report('target kinds the docs do not describe', [k for k in kinds if k not in ('current', 'target', 'none') and not re.search('`' + k.lower() + '[`( ]', doc, re.I)])
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
