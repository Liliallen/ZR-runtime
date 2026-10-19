#!/usr/bin/env python3
"""Add or update BOTW-PC UI strings in assets/locales.json (en_US, zh_TW, zh_CN; other languages fall back to en_US).

usage: add_locales.py <locales.json> <strings.json>   strings.json: {"Id": {"en_US": ..., "zh_TW": ..., "zh_CN": ...}}
New IDs are appended as text before the closing bracket so existing lines stay byte-identical; existing IDs are refused.
"""
import json, sys

path, strings = sys.argv[1], json.load(open(sys.argv[2], encoding="utf-8"))
doc = json.load(open(path, encoding="utf-8"))
langs = doc["Languages"]
ids = {e["ID"] for e in doc["Locales"]}
dupes = [k for k in strings if k in ids]
if dupes:
    sys.exit(f"already present: {dupes}")
blocks = []
for key, tr in strings.items():
    entry = {"ID": key, "Translations": {lang: tr.get(lang, "") for lang in langs}}
    text = json.dumps(entry, ensure_ascii=False, indent=2)
    blocks.append("\n".join("    " + line for line in text.splitlines()))
raw = open(path, encoding="utf-8").read()
tail = raw.rstrip().rstrip("}").rstrip().rstrip("]").rstrip()
assert raw[len(tail):].split() == ["]", "}"], "unexpected file ending"
with open(path, "w", encoding="utf-8") as f:
    f.write(tail + ",\n" + ",\n".join(blocks) + "\n  ]\n}\n")
json.load(open(path, encoding="utf-8"))
