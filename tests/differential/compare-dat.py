"""Compares two items-as-resources packages by content, ignoring the order of entries.

Usage: compare-dat.py <a.dat> <b.dat>
Exits 0 when both hold the same item definitions, shared fields and language data.
"""
import sys, uuid

def varint(b, i):
    r = s = 0
    while True:
        c = b[i]; i += 1; r |= (c & 0x7f) << s; s += 7
        if c < 0x80:
            return r, i

def fields(b):
    i = 0
    while i < len(b):
        key, i = varint(b, i)
        f, w = key >> 3, key & 7
        if w == 0:
            v, i = varint(b, i)
        elif w == 1:
            v = b[i:i + 8]; i += 8
        elif w == 2:
            n, i = varint(b, i); v = b[i:i + n]; i += n
        elif w == 5:
            v = b[i:i + 4]; i += 4
        else:
            raise ValueError(f"wire type {w}")
        yield f, v

def guid(b):
    raw = bytearray(16)
    for f, v in fields(b):
        raw[(f - 1) * 8:(f - 1) * 8 + 8] = v
    return str(uuid.UUID(bytes_le=bytes(raw)))

def field_map(entries):
    out = {}
    for e in entries:
        d = dict(fields(e))
        out[guid(d[1])] = d.get(2, b"").decode("utf-8")
    return out

def load(path):
    data = open(path, "rb").read()
    defs, shared, langs = {}, {}, {}
    for f, v in fields(data):
        parts = list(fields(v))
        key = guid(parts[0][1])
        rest = [p[1] for p in parts[1:]]
        if f == 1:
            rec = {}
            for rf, rv in fields(rest[0]):
                rec[rf] = guid(rv) if rf != 2 else rv.decode("utf-8")
            defs[key] = rec
        elif f == 2:
            shared[key] = field_map(rest)
        elif f == 3:
            item = {}
            for lang_entry in rest:
                lp = list(fields(lang_entry))
                name = lp[0][1].decode("utf-8")
                versions = {}
                for _, ve in lp[1:]:
                    vp = list(fields(ve))
                    versions[vp[0][1]] = field_map([x[1] for x in vp[1:]])
                item[name] = versions
            langs[key] = item
    return defs, shared, langs

a, b = load(sys.argv[1]), load(sys.argv[2])
names = ["definitions", "shared fields", "language data"]
ok = True
for name, x, y in zip(names, a, b):
    if x != y:
        ok = False
        only_a = set(x) - set(y)
        only_b = set(y) - set(x)
        diff = [k for k in set(x) & set(y) if x[k] != y[k]]
        print(f"{name}: {len(only_a)} only in A, {len(only_b)} only in B, {len(diff)} differ")
        for k in diff[:3]:
            print("  ", k, "\n    A:", str(x[k])[:300], "\n    B:", str(y[k])[:300])
    else:
        print(f"{name}: identical ({len(x)} entries)")
sys.exit(0 if ok else 1)
