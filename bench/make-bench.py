"""Writes a synthetic project of N items for the scale benchmarks, under its own root
/sitecore/system/Modules/Tern Bench <set>, where <set> is the out-dir's name, so sets live side by side.

Usage: make-bench.py <out-dir> <content|media> <N>

Items sit in folders of at most 100 children (Sitecore's practical limit), so N leaf items plus their
folders. Content items are Sample Items with an en Title and Text; media items are unversioned Files
with a 16 KB blob. IDs are deterministic, so two runs write identical files.
"""
import base64, os, random, sys, uuid

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "differential"))
from importlib import import_module
fx = import_module("make-fixture")

ROOT_PARENT = fx.ROOT_PARENT
FOLDER, SAMPLE, TITLE, TEXT = fx.FOLDER, fx.SAMPLE, fx.TITLE, fx.TEXT
MEDIA_FOLDER = "fe5dd826-48c6-436d-b87a-7c4210c7413b"   # System/Media/Media folder
FILE = "962b53c4-f93b-4df9-9821-415c867b8903"           # System/Media/Unversioned/File
BLOB, SIZE, MIME, EXT = ("40e50ed9-ba07-4702-992e-a912738d32dc", "6954b7c7-2487-423f-8600-436cb3b6dc0e",
                         "6f47a0a5-9c94-4b48-abeb-42d38def6054", "c06867fe-9a43-4c7d-b739-48780492d06f")
SET = os.path.basename(os.path.normpath(sys.argv[1]))
ROOT = "/sitecore/system/Modules/Tern Bench " + SET
NS = uuid.UUID("5e2b7f00-7e4b-4c3a-9a51-000000000000")
FANOUT = 100

KIND = sys.argv[2]
CREATED, REVISION, UPDATED, UPDATED_BY = ("25bed78c-4957-4165-998a-ca1b52f67497", "8cdc337e-a112-42fb-bbb4-4143751e123f",
                                          "d9cf14b1-fa16-4ba6-9288-e8a174d4d522", "badd9cf9-53e0-4d0c-bcc0-2d784c282f6a")

def gid(name):
    return str(uuid.uuid5(NS, SET + "/" + KIND + "/" + name))

def stats(i):
    """The statistics fields Sitecore stamps on save, so a pushed item round-trips with nothing left to change."""
    return [(CREATED, "__Created", "20260101T000000Z"), (REVISION, "__Revision", gid(f"rev{i}")),
            (UPDATED, "__Updated", "20260101T000000Z"), (UPDATED_BY, "__Updated by", "sitecore\\admin")]

def blob_item(iid, parent, path, n):
    rnd = random.Random(n)
    data = bytes(rnd.getrandbits(8) for _ in range(16384))
    lines = fx.item(iid, parent, FILE, path, shared=[(SIZE, "Size", str(len(data))), (MIME, "Mime Type", "application/octet-stream"), (EXT, "Extension", "bin")]).split("\r\n")
    blob = [f'- ID: "{BLOB}"', "  Hint: Blob", f'  BlobID: "{gid("blob" + str(n))}"', "  Value: " + base64.b64encode(data).decode()]
    at = lines.index("SharedFields:") + 1
    return "\r\n".join(lines[:at] + blob + lines[at:])

def main():
    out, kind, n = sys.argv[1], sys.argv[2], int(sys.argv[3])
    folder_tpl = MEDIA_FOLDER if kind == "media" else FOLDER
    os.makedirs(out, exist_ok=True)
    plugins = ", ".join(f'"Sitecore.DevEx.Extensibility.{p}@6.0.23"' for p in ["Serialization", "Publishing", "Indexing", "ResourcePackage", "Database"])
    open(os.path.join(out, "sitecore.json"), "w").write('{\n  "modules": ["*.module.json"],\n  "plugins": [' + plugins + '],\n  "serialization": { "defaultMaxRelativeItemPathLength": 100, "defaultModuleRelativeSerializationPath": "items", "removeOrphansForRoles": false, "removeOrphansForUsers": false },\n  "settings": { "telemetryEnabled": false, "apiClientTimeoutInMinutes": 60 }\n}\n')
    open(os.path.join(out, "TernBench.module.json"), "w").write('{\n  "namespace": "TernBench",\n  "items": { "includes": [ { "name": "bench", "path": "%s", "scope": "ItemAndDescendants", "allowedPushOperations": "CreateUpdateAndDelete" } ] }\n}\n' % ROOT)
    base = os.path.join(out, "items", "bench")

    def emit(path, text):
        rel = path[len(ROOT):].strip("/")
        leaf = ROOT.rsplit("/", 1)[1]
        f = os.path.join(base, leaf, *rel.split("/")) + ".yml" if rel else os.path.join(base, leaf + ".yml")
        os.makedirs(os.path.dirname(f), exist_ok=True)
        with open(f, "wb") as h:
            h.write(b"\xef\xbb\xbf" + text.encode("utf-8"))

    emit(ROOT, fx.item(gid("root"), ROOT_PARENT, folder_tpl, ROOT))
    depth = 0
    while FANOUT ** (depth + 1) < n:
        depth += 1
    made = set()
    count = 0
    for i in range(n):
        parent_id, parent_path = gid("root"), ROOT
        for level in range(depth, 0, -1):
            idx = (i // FANOUT ** level) % FANOUT
            key = f"{parent_path}/f{idx:02d}"
            fid = gid(key)
            if key not in made:
                emit(key, fx.item(fid, parent_id, folder_tpl, key))
                made.add(key)
                count += 1
            parent_id, parent_path = fid, key
        path = f"{parent_path}/i{i:07d}"
        iid = gid(f"item{i}")
        if kind == "media":
            emit(path, blob_item(iid, parent_id, path, i))
        else:
            emit(path, fx.item(iid, parent_id, SAMPLE, path, langs=[("en", [(1, [(TITLE, "Title", f"Item {i}"), (TEXT, "Text", f"<p>Body of item {i}</p>")] + stats(i))])]))
        count += 1
    print(f"{out}: {n} {kind} items + {count - n + 1} folders")

main()
