"""Copies one endpoint from a user.json into others, with writes allowed (bench copies only).

endpoint.py <source user.json> <endpoint name> <target user.json>...
"""
import json, sys

source, name, targets = sys.argv[1], sys.argv[2], sys.argv[3:]
endpoint = dict(json.load(open(source, encoding="utf-8-sig"))["endpoints"][name])
endpoint["allowWrite"] = True
for t in targets:
    with open(t, encoding="utf-8-sig") as f:
        d = json.load(f)
    if d.get("endpoints", {}).get(name) == endpoint:
        continue
    d.setdefault("endpoints", {})[name] = endpoint
    with open(t, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=2)
