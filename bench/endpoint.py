"""Copies endpoints from a user.json into others, with writes allowed (bench copies only).

endpoint.py <source user.json> <endpoint name>... -- <target user.json>...
"""
import json, sys

source, rest = sys.argv[1], sys.argv[2:]
names, targets = rest[:rest.index("--")], rest[rest.index("--") + 1:]
all_endpoints = json.load(open(source, encoding="utf-8-sig"))["endpoints"]
endpoints = {}
for name in names:
    endpoints[name] = dict(all_endpoints[name])
    endpoints[name]["allowWrite"] = True
for t in targets:
    with open(t, encoding="utf-8-sig") as f:
        d = json.load(f)
    if all(d.get("endpoints", {}).get(n) == e for n, e in endpoints.items()):
        continue
    d.setdefault("endpoints", {}).update(endpoints)
    with open(t, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=2)
