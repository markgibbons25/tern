"""Walks a CLI's --help recursively and prints every command path with its options.

cmdtree.py <cwd> <exe> [<exe args>...]  ->  JSON {"ser pull": ["--force", "-f", ...], ...}
"""
import json, re, subprocess, sys

cwd, base = sys.argv[1], sys.argv[2:]

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

tree = {}
def walk(path):
    s = sections(help_of(path))
    opts = set()
    for line in s.get("Options", []):
        if re.match(r"^\s{2,4}-", line):
            opts.update(re.findall(r"(?<![\w-])(--?[\w?][\w-]*)", line.split("  ")[1] if line.startswith("  -") is False else line.strip().split("  ")[0]))
    tree[" ".join(path)] = sorted(opts)
    for line in s.get("Commands", []):
        if not re.match(r"^\s{2,4}\S", line):
            continue
        names = line.strip().split("  ")[0]
        name = names.split(",")[0].strip().split(" ")[0]
        if name and len(path) < 4:
            walk(path + [name])

walk([])
print(json.dumps(tree, indent=1))
