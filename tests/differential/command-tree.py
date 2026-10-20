"""Checks that Tern has every command and option of the Official CLI (docs/testing.md).

Walks both tools' --help recursively and lists what the Official CLI has that Tern doesn't.

Usage: command-tree.py <project-dir> <tern-exe> <official command>...
  <project-dir>       a folder whose sitecore.json names every plugin, so the Official CLI shows every command
  <official command>  how to start the Official CLI there, for example: dotnet sitecore
Exits 1 if anything is missing.
"""
import re, subprocess, sys

def tree(cwd, base):
    def help_of(path):
        out = subprocess.run(base + path + ["--help"], cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        return out.stdout + out.stderr

    def sections(text):
        result, current = {}, None
        for line in text.splitlines():
            m = re.match(r"^(\w[\w ]*):\s*$", line)
            if m:
                current = m.group(1).strip()
                result[current] = []
            elif current and line.startswith("  ") and line.strip():
                result[current].append(line)
        return result

    found = {}

    def walk(path):
        s = sections(help_of(path))
        options = set()
        for line in s.get("Options", []):
            if re.match(r"^\s{2,4}-", line):
                options.update(re.findall(r"(?<![\w-])(--?[\w?][\w-]*)", line.strip().split("  ")[0]))
        found[" ".join(path)] = options
        for line in s.get("Commands", []):
            if re.match(r"^\s{2,4}\S", line):
                name = line.strip().split("  ")[0].split(",")[0].strip().split(" ")[0]
                if name and len(path) < 4:
                    walk(path + [name])

    walk([])
    return found

cwd, tern, official = sys.argv[1], sys.argv[2], sys.argv[3:]
theirs, ours = tree(cwd, official), tree(cwd, [tern])
missing = [c for c in theirs if c not in ours]
options = {c: sorted(o - ours[c]) for c, o in theirs.items() if c in ours and o - ours[c]}
for c in missing:
    print(f"missing command: {c}")
for c, o in options.items():
    print(f"missing options on '{c}': {', '.join(o)}")
print(f"{len(theirs)} Official CLI commands; {len(missing)} missing, {len(options)} with missing options")
sys.exit(1 if missing or options else 0)
