#!/usr/bin/env bash
# Benchmarks Tern against the Sitecore CLI on the same project and CM (docs/testing.md).
#
# Usage: bench/run.sh <project-dir> <fixture-dir> <tern-exe> [runs]
#   <project-dir>  a copy of a real project (sitecore.json, modules, .sitecore/user.json, .config)
#                  whose default endpoint is a Local CM
#   <fixture-dir>  the push fixture (make-fixture.py output, with s0/s1 set up to log in)
#   <tern-exe>     the Tern binary to measure
#   [runs]         runs per scenario (default 5); the median is reported
#
# Prints a Markdown table. Nothing outside the project copy, the fixture and the fixture's
# scratch root on the CM is changed.
set -uo pipefail
P="$1"
F="$2"
TERN="$3"
RUNS="${4:-5}"

median() { sort -n | awk '{a[NR]=$1} END {print (NR%2 ? a[(NR+1)/2] : (a[NR/2]+a[NR/2+1])/2)}'; }

# time_runs <dir> <runs> <setup command or ""> <command...>: prints the median wall time in ms.
time_runs() {
  local dir="$1" n="$2" setup="$3"
  shift 3
  for _ in $(seq "$n"); do
    [ -n "$setup" ] && (cd "$dir" && eval "$setup" > /dev/null 2>&1)
    local start end
    start=$(date +%s%N)
    (cd "$dir" && "$@" > /dev/null 2>&1)
    end=$(date +%s%N)
    echo $(( (end - start) / 1000000 ))
  done | median
}

row() {
  local name="$1" official="$2" tern="$3"
  awk -v n="$name" -v o="$official" -v t="$tern" 'BEGIN { printf "| %s | %.2f s | %.2f s | %.1f× |\n", n, o/1000, t/1000, o/t }'
}

echo "| Scenario | Sitecore CLI | Tern | Speed-up |"
echo "|---|---|---|---|"
row "Start up (\`--version\`)" "$(time_runs "$P" "$RUNS" "" dotnet sitecore --version)" "$(time_runs "$P" "$RUNS" "" "$TERN" --version)"
row "\`ser info\`" "$(time_runs "$P" "$RUNS" "" dotnet sitecore ser info)" "$(time_runs "$P" "$RUNS" "" "$TERN" ser info)"
row "\`ser validate\`" "$(time_runs "$P" "$RUNS" "" dotnet sitecore ser validate)" "$(time_runs "$P" "$RUNS" "" "$TERN" ser validate)"
row "\`ser pull --what-if\` (whole project)" "$(time_runs "$P" "$RUNS" "" dotnet sitecore ser pull -w -fr)" "$(time_runs "$P" "$RUNS" "" "$TERN" ser pull -w -fr)"
row "\`ser push --what-if\` (whole project)" "$(time_runs "$P" "$RUNS" "" dotnet sitecore ser push -w)" "$(time_runs "$P" "$RUNS" "" "$TERN" ser push -w)"
row "\`itemres create\` (whole project)" "$(time_runs "$P" "$RUNS" "" dotnet sitecore itemres create -o bench-res --overwrite)" "$(time_runs "$P" "$RUNS" "" "$TERN" itemres create -o bench-res --overwrite)"

# Write path: push the changed fixture onto the baseline, resetting with the Sitecore CLI first.
RESET="(cd ../s0 && dotnet sitecore ser push)"
row "\`ser push\` (7 changes: create, update, move, rename, recycle)" \
  "$(time_runs "$F/s1" "$RUNS" "$RESET" dotnet sitecore ser push)" \
  "$(time_runs "$F/s1" "$RUNS" "$RESET" "$TERN" ser push)"
row "\`ser push\` with nothing to do" "$(time_runs "$F/s0" "$RUNS" "" dotnet sitecore ser push)" "$(time_runs "$F/s0" "$RUNS" "" "$TERN" ser push)"
