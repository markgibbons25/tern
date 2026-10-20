"""Checks that a Tern build has every command and option of a stored Official CLI command tree.

cmdtree-check.py <cwd with all plugins> <tern.exe> <official tree.json>
Prints one line: OK, or what is missing.
"""
import json, os, subprocess, sys

cwd, tern, ref = sys.argv[1:4]
here = os.path.dirname(os.path.abspath(__file__))
out = subprocess.run([sys.executable, os.path.join(here, "cmdtree.py"), cwd, tern], capture_output=True, text=True, timeout=600)
mine, official = json.loads(out.stdout), json.load(open(ref))
missing = [k for k in official if k not in mine]
options = {k: [o for o in v if o not in mine[k]] for k, v in official.items() if k in mine}
options = {k: v for k, v in options.items() if v}
if not missing and not options:
    print(f"command tree: OK, all {len(official)} Official CLI commands and their options are present")
else:
    print(f"command tree: MISSING commands {missing} options {options}")
