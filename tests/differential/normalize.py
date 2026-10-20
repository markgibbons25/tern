"""Removes the fields that change on every save from Item files, so CM snapshots taken after
different pushes can be compared. Edits the files in place."""
import os, re, sys

VOLATILE = {
    "8cdc337e-a112-42fb-bbb4-4143751e123f",  # __Revision
    "d9cf14b1-fa16-4ba6-9288-e8a174d4d522",  # __Updated
    "badd9cf9-53e0-4d0c-bcc0-2d784c282f6a",  # __Updated by
    "001dd393-96c5-490b-924a-b0f25cd9efd8",  # __Lock
    "52807595-0f8f-4b20-8d2a-cb71d28c6103",  # __Owner
    "25bed78c-4957-4165-998a-ca1b52f67497",  # __Created
    "5dd74568-4d4b-44c1-b513-0af5f4cda34f",  # __Created by
    "dbbbeca1-21c7-4906-9dd2-493c1efa59a2",  # __Shared revision
}

ID_LINE = re.compile(r'^(\s*)- ID: "?([0-9a-f-]{36})"?\s*$')

def normalize(path):
    lines = open(path, encoding="utf-8-sig").read().splitlines()
    out, skip_indent = [], None
    for line in lines:
        m = ID_LINE.match(line)
        if m:
            skip_indent = len(m.group(1)) if m.group(2) in VOLATILE else None
            if skip_indent is not None:
                continue
        elif skip_indent is not None:
            stripped = len(line) - len(line.lstrip(" -"))
            if line.strip() and stripped <= skip_indent:
                skip_indent = None
            else:
                continue
        out.append(line)
    open(path, "w", encoding="utf-8").write("\n".join(out) + "\n")

for root, _, files in os.walk(sys.argv[1]):
    for f in files:
        if f.endswith(".yml"):
            normalize(os.path.join(root, f))
        elif f == ".scindex":
            os.remove(os.path.join(root, f))
