"""Writes the synthetic push fixtures: a baseline tree (S0) and a changed tree (S1).

The items live under /sitecore/system/Modules/Tern Tests, a scratch root that no real project
uses, and are written in the Official CLI's Item file format by hand so the fixture does not
depend on Tern being right.
"""
import os, sys, shutil

ROOT_PARENT = "08477468-d438-43d4-9d6a-6d84a611971c"   # /sitecore/system/Modules
FOLDER = "a87a00b1-e6db-45ab-8b54-636fec3b5523"         # Common/Folder
SAMPLE = "76036f5e-cbce-46d1-af0a-4143f9b557aa"         # Sample/Sample Item
TITLE = "75577384-3c97-45da-a847-81b00500e250"
TEXT = "a60acd61-a6db-4182-8329-c957982cec74"
SORT = "ba3f86a2-4a1c-4d78-b63d-91c2779c1b5e"           # __Sortorder
ROOT = "/sitecore/system/Modules/Tern Tests"

IDS = {n: f"7e5a{i:04x}-0000-4000-8000-00000000{i:04x}" for i, n in enumerate(
    ["root", "alpha", "beta", "gamma", "delta", "epsilon"], start=1)}

def q(v):  # the official writer's single-line encoding
    if any(c in v for c in '\n\r"\\'):
        raise ValueError("use block")
    return f'"{v}"' if any(c in v for c in '":[]{}!?-') else v

def field(ind, fid, hint, value):
    pad = " " * ind
    lines = [f"{pad[:-2]}- ID: {q(fid)}", f"{pad}Hint: {hint}"]
    if any(c in value for c in '\n\r"\\'):
        lines.append(f"{pad}Value: |")
        lines += [pad + "  " + l for l in value.split("\n")]
    else:
        lines.append(f"{pad}Value: {q(value)}")
    return lines

def item(iid, parent, template, path, shared=(), langs=()):
    out = ["---", f"ID: {q(iid)}", f"Parent: {q(parent)}", f"Template: {q(template)}", f"Path: {path}"]
    if shared:
        out.append("SharedFields:")
        for fid, hint, v in sorted(shared):
            out += field(2, fid, hint, v)
    if langs:
        out.append("Languages:")
        for lang, versions in sorted(langs):
            out.append(f"- Language: {q(lang)}")
            out.append("  Versions:")
            for num, fields in sorted(versions):
                out.append(f"  - Version: {num}")
                if fields:
                    out.append("    Fields:")
                    for fid, hint, v in sorted(fields):
                        out += field(6, fid, hint, v)
    return "\r\n".join(out) + "\r\n"

def write(base, items):
    if os.path.exists(base):
        shutil.rmtree(base)
    os.makedirs(base)
    plugins = ", ".join(f'"Sitecore.DevEx.Extensibility.{p}@6.0.23"' for p in ["Serialization", "Publishing", "Indexing", "ResourcePackage", "Database"])
    open(os.path.join(base, "sitecore.json"), "w").write('{\n  "modules": ["*.module.json"],\n  "plugins": [' + plugins + '],\n  "serialization": { "defaultMaxRelativeItemPathLength": 100, "defaultModuleRelativeSerializationPath": "items" },\n  "settings": { "telemetryEnabled": false }\n}\n')
    open(os.path.join(base, "TernTests.module.json"), "w").write('{\n  "namespace": "TernTests",\n  "items": { "includes": [ { "name": "tern-tests", "path": "%s", "scope": "ItemAndDescendants", "allowedPushOperations": "CreateUpdateAndDelete" } ] }\n}\n' % ROOT)
    for path, text in items:
        rel = path[len(ROOT):].strip("/")
        segs = ["Tern Tests"] + ([s for s in rel.split("/")] if rel else [])
        f = os.path.join(base, "items", "tern-tests", *segs) + ".yml"
        os.makedirs(os.path.dirname(f), exist_ok=True)
        open(f, "wb").write(b"\xef\xbb\xbf" + text.encode("utf-8"))

def s0():
    a, b, g, d = (ROOT + "/" + n for n in ["Alpha", "Beta", "Gamma", "Gamma/Delta"])
    return [
        (ROOT, item(IDS["root"], ROOT_PARENT, FOLDER, ROOT)),
        (a, item(IDS["alpha"], IDS["root"], SAMPLE, a, langs=[
            ("en", [(1, [(TITLE, "Title", "Alpha title"), (TEXT, "Text", "<p>Hello</p>\n<p>World</p>")])]),
            ("de-DE", [(1, [(TITLE, "Title", "Alpha DE")])])])),
        (b, item(IDS["beta"], IDS["root"], SAMPLE, b, langs=[
            ("en", [(1, [(TITLE, "Title", "Beta one")]), (2, [(TITLE, "Title", "Beta two")])])])),
        (g, item(IDS["gamma"], IDS["root"], FOLDER, g)),
        (d, item(IDS["delta"], IDS["gamma"], SAMPLE, d, langs=[("en", [(1, [(TITLE, "Title", "Delta")])])])),
    ]

def s1():
    a, b, d, e = (ROOT + "/" + n for n in ["Alpha", "Beta Renamed", "Delta", "Epsilon"])
    return [
        (ROOT, item(IDS["root"], ROOT_PARENT, FOLDER, ROOT)),
        (a, item(IDS["alpha"], IDS["root"], SAMPLE, a, shared=[(SORT, "__Sortorder", "100")], langs=[
            ("en", [(1, [(TITLE, "Title", "Alpha title, changed"), (TEXT, "Text", "<p>Hello</p>\n<p>Again</p>")])]),
            ("ja-JP", [(1, [(TITLE, "Title", "Alpha JA")])])])),
        (b, item(IDS["beta"], IDS["root"], SAMPLE, b, langs=[("en", [(1, [(TITLE, "Title", "Beta one")])])])),
        (d, item(IDS["delta"], IDS["root"], SAMPLE, d, langs=[("en", [(1, [(TITLE, "Title", "Delta moved")])])])),
        (e, item(IDS["epsilon"], IDS["root"], FOLDER, e)),
    ]

if __name__ == "__main__":
    out = sys.argv[1]
    write(os.path.join(out, "s0"), s0())
    write(os.path.join(out, "s1"), s1())
    print("fixtures written to", out)
