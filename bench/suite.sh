#!/usr/bin/env bash
# End-to-end benchmark and check suite.
#
#   [CLI=1 [CLITOOLS="official official7"]] [ONLY=<regex>] suite.sh <quick|full> <local|cloud> <tool>...
#
# The Sitecore CLIs' numbers don't change when Tern does, so they are measured only with CLI=1
# (which adds CLITOOLS, both CLIs by default, to the tools) and saved, replacing earlier rows for
# the same scenario and CLI; other runs report the saved numbers beside Tern's. ONLY runs just the
# scenarios whose names match the regex.
#
# The Sitecore CLI 7 install and its plugins live in $TOOLS_DIR, outside %TEMP%: Windows' temp
# cleanup deletes files with old timestamps, which NuGet-extracted files have.
#
# A tool is `official` (Sitecore CLI 6), `official7` (Sitecore CLI 7) or the name of a folder under
# $B holding a Tern build (aot, opt, ...). quick (~30 s for two Tern builds, local) runs every kind
# of scenario at small sizes; full (~5 min with both CLIs and one Tern build, local) adds the
# larger sizes, content + media sync, and the correctness suites (unit tests, differential push,
# command tree). `cloud` runs the CM scenarios against the dev-mark endpoint.
#
# On cloud, dev-mark is a personal environment, so pushes are real there too. Creates go to
# /sitecore/system/Modules/Tern Bench <set> roots (deleted after) and the 7-change push to the
# Tern Tests scratch root; in-sync pushes send content pulled from the same CM.
#
# The workspace $B (TERN_BENCH, default ~/tern-bench) holds everything that isn't committed: Tern
# builds, the project copies (proj, content, all), the fixture, generated sets, pulls, reference
# pulls, logs, results, and measure.exe (`dotnet publish bench/measure -c Release -o $B/measure`).
#
# Results: $B/results/<mode>-<env>-<timestamp>.tsv, a summary on stdout.
set -uo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
B=${TERN_BENCH:-$HOME/tern-bench}
SCR="$(cygpath -m "$REPO/bench")"
MODE=$1; ENVN=$2; shift 2; TOOLS="$*"
[ "${CLI:-0}" = 1 ] && TOOLS="${CLITOOLS:-official official7} $TOOLS"
TOOLS_DIR=$(cygpath -m "${TERN_BENCH_TOOLS:-$HOME/tern-bench-tools}")
RUNS=${RUNS:-1}
CLOUD_ENV=${CLOUD_ENV:-dev-mark}
STAMP=$(date +%Y%m%d-%H%M%S)
OUT=$B/results/$MODE-$ENVN-$STAMP.tsv; mkdir -p $B/results; : > $OUT
LOG=$B/logs/suite-$ENVN; mkdir -p $LOG
START=$(date +%s)
[ $ENVN = cloud ] && EFLAG="-n $CLOUD_ENV" || EFLAG=""
WHATIF=""
SETUP_TERN=$B/opt1/tern.exe   # a known-good Tern for resets and setup, never one under test

cmd_of() { case $1 in official) echo dotnet sitecore ;; official7) echo $TOOLS_DIR/cli7/sitecore.exe ;; *) echo $B/$1/tern.exe ;; esac; }

# The folder a tool runs in: CLI 7 gets a mirror under $B/v7 whose sitecore.json names the 7.x plugins.
dir_of() {
  local tool=$1 dir=$2
  if [ $tool != official7 ]; then echo $dir; return; fi
  local m=$B/v7${dir#$B}
  if [ ! -f $m/.v7-ready ]; then
    mkdir -p $m; cp -r $dir/. $m/; (cd $m && rm -rf ./.sitecore/package-cache)
    [ -f $m/sitecore.json ] && sed -i 's/@6\.0\.23/@7.0.24/g; s/XMCloud@1\.1\.120/XMCloud@2.0.14/' $m/sitecore.json
    touch $m/.v7-ready
  fi
  # The shared plugin cache, linked rather than copied; checked every time, since temp cleanup can remove the link.
  if [ ! -d $m/.sitecore/package-cache/nuget ]; then
    mkdir -p $m/.sitecore && MSYS_NO_PATHCONV=1 cmd /c mklink /J "$(cygpath -w $m/.sitecore/package-cache)" "$(cygpath -w $TOOLS_DIR/v7plugins/.sitecore/package-cache)" > /dev/null
  fi
  # The endpoints follow the source folder's user.json, which setup may have changed.
  cp $dir/.sitecore/user.json $m/.sitecore/user.json
  echo $m
}

# A folder for one pull scenario: the source project's configuration, no items.
pull_dir() {
  local src=$1 name=$2 d=$B/pulls/$ENVN/$2
  if [ ! -f $d/sitecore.json ]; then
    mkdir -p $d; cp -r $src/.config $src/.sitecore $d/; cp $src/*.json $d/
  fi
  echo $d
}

# measure <scenario> <tool> <run> <dir> <args...>
measure() {
  local sc=$1 tool=$2 run=$3 dir=$(dir_of $2 $4); shift 4
  local res=$LOG/m.txt; rm -f $res
  (cd $dir && $B/measure/measure.exe "$(cygpath -w $res)" $(cmd_of $tool) "$@" > "$LOG/$tool.log" 2>&1)
  read ms ws commit cpu rc < $res
  printf "%s\t%s\t%s\t%s\t%s\t%.1f\t%s\n" "$sc" $tool $run $ms $rc $(awk -v b=$ws 'BEGIN{print b/1048576}') $cpu >> $OUT
  [ "$rc" = 0 ] || { echo "  FAILED: $tool $sc (exit $rc): $(grep -v '^\s*at ' "$LOG/$tool.log" | tail -2 | tr '\n' ' ')"; }
}

# scenario <name> <dir> <setup or ""> <args...>: every tool, RUNS times, taking turns.
# With FRESH=1 the first measurement skips the setup: the state is already right (a create set
# that the start of the suite made sure is absent).
scenario() {
  local sc=$1 dir=$2 setup=$3 first=1; shift 3
  if [ -n "${ONLY:-}" ] && ! [[ "$sc" =~ $ONLY ]]; then return; fi
  for r in $(seq $RUNS); do
    for tool in $TOOLS; do
      [ -n "$setup" ] && ! { [ "${FRESH:-0}" = 1 ] && [ $first = 1 ]; } && (cd $(dir_of $tool $dir) && eval "$setup" > /dev/null 2>&1)
      first=0
      measure "$sc" $tool $r $dir "$@"
      [ "${AFTER:-}" ] && $AFTER "$sc" $tool $dir
    done
  done
}

# Pulled files must match the reference pull for that scenario and environment (the first full
# run's Sitecore CLI 6 pull); .scindex is the Official CLI's cache and may differ.
check_pull() {
  local sc=$1 tool=$2 dir=$(dir_of $2 $3) ref="$B/ref/$ENVN/$1"
  if [ ! -d "$ref" ]; then
    [ $tool = official ] || return 0
    mkdir -p "$(dirname "$ref")"; cp -r $dir/items "$ref"; return 0
  fi
  diff -rq -x '*.scindex' "$ref" $dir/items > $LOG/diff.txt 2>&1 || echo "  PULL DIFFERS: $tool $sc ($(wc -l < $LOG/diff.txt) paths; $LOG/diff.txt)"
}

DEL() { echo "mutation { deleteItem(input: { path: \"/sitecore/system/Modules/Tern Bench $1\", permanently: true }) { successful } }"; }
reset_set() { echo "$SETUP_TERN authoring query $EFLAG -q '$(DEL $1)'"; }

# A generated set, created if missing. Persistent sets are pushed to the CM once.
set_dir() {
  local name=$1 kind=$2 n=$3 d=$B/sets/$1
  if [ ! -f $d/sitecore.json ]; then python "$SCR/make-bench.py" $d $kind $n > /dev/null; fi
  cp -r $B/all/.config $d/ 2>/dev/null; mkdir -p $d/.sitecore; cp -r $B/all/.sitecore/schemas $d/.sitecore/ 2>/dev/null
  cp $B/all/.sitecore/user.json $d/.sitecore/user.json; [ -d $d/.sitecore/package-cache ] || cp -r $B/all/.sitecore/package-cache $d/.sitecore/
  echo $d
}
ensure_on_cm() { (cd $1 && $SETUP_TERN ser push $EFLAG > $LOG/setup.log 2>&1) || echo "  SETUP FAILED for $1: $(tail -2 $LOG/setup.log)"; }

# --- setup -------------------------------------------------------------------------------------
P=$B/proj; F=$B/fixture
# The create sets are deleted after each run, in the background; wait for that, then make sure.
JANITOR=$B/logs/janitor-$ENVN.pid
while [ -f $JANITOR ] && kill -0 $(cat $JANITOR) 2>/dev/null; do sleep 1; done
CREATE_SETS="c100n m100n"; [ $MODE = full ] && CREATE_SETS="$CREATE_SETS c1kn m1kn"
if [ $ENVN = cloud ]; then
  python "$SCR/endpoint.py" "$(cygpath -w $B/cloud-probe/.sitecore/user.json)" $CLOUD_ENV $(for d in $P $F/s0 $F/s1 $B/content $B/all $B/sets/* $B/pulls/cloud/*; do [ -f $d/.sitecore/user.json ] && cygpath -w $d/.sitecore/user.json; done)
fi
C1K=$(set_dir c1kp content 1000); M100=$(set_dir m100p media 100)
C100N=$(set_dir c100n content 100); M100N=$(set_dir m100n media 100)
if [ $MODE = full ]; then
  C10K=$(set_dir c10kp content 10000); M1K=$(set_dir m1kp media 1000)
  C1KN=$(set_dir c1kn content 1000); M1KN=$(set_dir m1kn media 1000)
  for d in $C1K $M100 $C10K $M1K; do ensure_on_cm $d; done
elif [ ! -f $B/sets/.on-cm-$ENVN ]; then
  for d in $C1K $M100; do ensure_on_cm $d; done
fi
touch $B/sets/.on-cm-$ENVN
for s in $CREATE_SETS; do (cd $B/sets/$s && eval "$(reset_set $s)" > /dev/null 2>&1); done
[ $ENVN = cloud ] && python "$SCR/endpoint.py" "$(cygpath -w $B/cloud-probe/.sitecore/user.json)" $CLOUD_ENV $(for d in $B/sets/*; do cygpath -w $d/.sitecore/user.json; done)
SETUP=$(( $(date +%s) - START ))

# --- scenarios ---------------------------------------------------------------------------------
if [ $ENVN = local ]; then
  # Commands that never talk to the CM run only in the local suite.
  scenario "Start up (--version)" $P "" --version
  scenario "ser info" $P "" ser info
  scenario "ser validate (whole project)" $P "" ser validate
  scenario "itemres create (whole project)" $P "" itemres create -o bench-res --overwrite
fi
scenario "ser pull --what-if (whole project)" $P "" ser pull -w -fr $EFLAG
scenario "ser push --what-if (whole project)" $P "" ser push -w $EFLAG
# The reset uses a known-good Tern build (the differential test proves its pushes match the Sitecore CLI's).
scenario "ser push (7 changes)" $F/s1 "(cd $F/s0 && $SETUP_TERN ser push $EFLAG)" ser push $EFLAG
scenario "ser push, nothing to do" $F/s0 "" ser push $EFLAG

AFTER=check_pull scenario "Full pull (content only)" $(pull_dir $B/content content) "rm -rf ./items" ser pull $EFLAG
scenario "Full push, in sync (content only)" $(pull_dir $B/content content) "" ser push $WHATIF $EFLAG
if [ $MODE = full ]; then
  AFTER=check_pull scenario "Full pull (content + media)" $(pull_dir $B/all all) "rm -rf ./items" ser pull $EFLAG
  scenario "Full push, in sync (content + media)" $(pull_dir $B/all all) "" ser push $WHATIF $EFLAG
fi

FRESH=1 scenario "Create 100 content items" $C100N "$(reset_set c100n)" ser push $EFLAG
FRESH=1 scenario "Create 100 media items" $M100N "$(reset_set m100n)" ser push $EFLAG
AFTER=check_pull scenario "Pull 100 media items" $(pull_dir $M100 m100p) "rm -rf ./items" ser pull $EFLAG
AFTER=check_pull scenario "Pull 1,000 content items" $(pull_dir $C1K c1kp) "rm -rf ./items" ser pull $EFLAG
scenario "Push 1,000 content items, in sync" $C1K "" ser push $EFLAG
if [ $MODE = full ]; then
  FRESH=1 scenario "Create 1,000 content items" $C1KN "$(reset_set c1kn)" ser push $EFLAG
  FRESH=1 scenario "Create 1,000 media items" $M1KN "$(reset_set m1kn)" ser push $EFLAG
  for d in $C100N $M100N $C1KN $M1KN; do (cd $d && dotnet sitecore ser push -w $EFLAG 2>&1 | grep -q "Discovered 0 changes after evaluating") || echo "  CREATE CHECK FAILED: the Sitecore CLI still sees changes in $d"; done
  AFTER=check_pull scenario "Pull 1,000 media items" $(pull_dir $M1K m1kp) "rm -rf ./items" ser pull $EFLAG
  AFTER=check_pull scenario "Pull 10,000 content items" $(pull_dir $C10K c10kp) "rm -rf ./items" ser pull $EFLAG
  scenario "Push 10,000 content items, in sync" $C10K "" ser push $EFLAG
fi
# Delete what the creates made, after the run and without holding it up.
( for s in $CREATE_SETS; do (cd $B/sets/$s && eval "$(reset_set $s)" > /dev/null 2>&1); done; rm -f $JANITOR ) > /dev/null 2>&1 &
echo $! > $JANITOR

# --- correctness suites (full, local) -------------------------------------------------------------
if [ $MODE = full ] && [ $ENVN = local ]; then
  for t in $TOOLS; do
    case $t in official|official7) continue ;; esac
    echo "== correctness: $t"
    (cd $REPO && dotnet test --solution Tern.slnx -c Release 2>&1 | grep -E "^\s+(total|failed|succeeded):" | tr -s ' ' | tr '\n' ' '); echo
    (cd $REPO && bash tests/differential/push-diff.sh $F $B/$t/tern.exe 2>&1 | grep -E "PASS|FAIL" | tr '\n' ' '); echo
    python "$SCR/cmdtree-check.py" "$(cygpath -w $B/v7/allplugins)" "$(cygpath -w $B/$t/tern.exe)" "$(cygpath -w $B/tree-cli7.json)"
  done
fi

echo "== $MODE suite, $ENVN, tools: $TOOLS, $(( $(date +%s) - START )) s (setup ${SETUP} s) -> $OUT"
CACHE=$B/results/cli-$ENVN.tsv
if [ "${CLI:-0}" = 1 ]; then
  # Keep this run's CLI rows, replacing earlier ones for the same scenarios.
  python - "$(cygpath -w $OUT)" "$(cygpath -w $CACHE)" <<'PY'
import os, sys
out, cache = sys.argv[1], sys.argv[2]
new = [l for l in open(out, encoding="utf-8") if l.split("	")[1] in ("official", "official7")]
key = lambda l: tuple(l.split("	")[:2])
fresh = {key(l) for l in new}
old = [l for l in open(cache, encoding="utf-8")] if os.path.exists(cache) else []
open(cache, "w", encoding="utf-8").writelines([l for l in old if key(l) not in fresh] + new)
PY
  python "$SCR/report.py" "$(cygpath -w $OUT)" $TOOLS
elif [ -f $CACHE ]; then
  cat $CACHE $OUT > $LOG/combined.tsv
  echo "(Sitecore CLI columns are the saved numbers from $(date -r $CACHE '+%Y-%m-%d %H:%M'); CLI=1 measures them again)"
  python "$SCR/report.py" "$(cygpath -w $LOG/combined.tsv)" official official7 $TOOLS
else
  python "$SCR/report.py" "$(cygpath -w $OUT)" $TOOLS
fi
