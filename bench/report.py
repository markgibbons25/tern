"""Summary of a suite.sh TSV: per scenario, each tool's median wall time, peak working set and CPU.

report.py <results.tsv> <tool>...
The last tool is the one of interest; it is compared with the Sitecore CLIs, or with the first
tool when the run compares Tern builds.
"""
import statistics, sys

sys.stdout.reconfigure(encoding="utf-8")
from collections import OrderedDict, defaultdict

path, tools = sys.argv[1], sys.argv[2:]
names = {"official": "CLI 6", "official7": "CLI 7"}
rows = OrderedDict()
for line in open(path, encoding="utf-8"):
    sc, tool, run, ms, rc, ws, cpu = line.rstrip("\n").split("\t")
    rows.setdefault(sc, defaultdict(list))[tool].append((int(ms), float(ws), int(cpu), int(rc)))

def med(s, i):
    return statistics.median(x[i] for x in s)

last = tools[-1]
refs = [t for t in tools if t in names] or ([tools[0]] if len(tools) > 1 else [])
head = "| Scenario | " + " | ".join(names.get(t, t) for t in tools) + "".join(f" | {names.get(last, last)} vs {names.get(r, r)}" for r in refs) + " | Memory | CPU |"
print(head)
print("|---" * (2 + len(tools) + len(refs) + 1) + "|")
for sc, by in rows.items():
    cells, vals = [], {}
    for t in tools:
        s = by.get(t, [])
        if not s:
            cells.append("–")
            continue
        vals[t] = med(s, 0)
        bad = sum(1 for x in s if x[3] != 0)
        cells.append(f"{vals[t]/1000:.2f} s" + (" ⚠" if bad else ""))
    ratio = "".join(f" | {vals[r]/vals[last]:.1f}×" if r in vals and last in vals and vals[last] else " | –" for r in refs)
    mem = " / ".join(f"{med(by[t], 1):.0f}" for t in tools if by.get(t)) + " MB"
    cpu = " / ".join(f"{med(by[t], 2)/1000:.2f}" for t in tools if by.get(t)) + " s"
    print(f"| {sc} | " + " | ".join(cells) + ratio + f" | {mem} | {cpu} |")
