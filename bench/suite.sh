#!/usr/bin/env bash
# The benchmark suite: Tern against Sitecore CLI 7, on a local CM, a SitecoreAI environment or both.
#
#   [RUNS=n] [ONLY=<regex>] [CLOUD_ENV=<endpoint>] suite.sh <local|cloud|both> <build>
#
# Every scenario runs both tools, taking turns, and the median is reported. Scenarios of fewer
# than 1,000 items run RUNS times (default 3); larger ones, and the whole-project what-ifs, run
# once, which is steady enough at their length. ONLY runs just the scenarios whose names match.
# `both` takes about 15 minutes; nothing touches more than 10,000 items.
#
# <build> is a folder under $B holding a Tern build (aot, opt, ...). Sitecore CLI 7 and its plugins
# live in $TOOLS_DIR, outside %TEMP%: Windows' temp cleanup deletes files with old timestamps,
# which NuGet-extracted files have. Both tools run in the same folders, whose sitecore.json names
# the 7.x plugins; Tern ignores the plugin list.
#
# The content is real, or made by Sitecore. The whole-project and full-pull scenarios use a copy
# of a real project ($B/proj, $B/content, $B/all). The sets under /sitecore/system/Modules/Tern
# Bench <set> were created on the local CM by bench/make-set.ps1, so Sitecore assigned their IDs,
# and pulled once into $B/sets/<set>:
#   c100n, m100n  100 content / media items that each create run pushes; the CM copy is deleted
#                 before every run
#   c1k, c10k     1,000 / 10,000 content items kept on the CM, for pulls and in-sync pushes; the
#                 "10 changes" scenario edits ten c1k files before each run
#   m100          100 media items kept on the CM, for pulls
# Setup pushes the kept sets from those files to the CM when they are missing or differ. On cloud,
# pushes are real, so CLOUD_ENV must be an environment nobody else depends on.
#
# Logins: every folder's user.json is given the same endpoints, copied from $B/cloud-probe after
# one call there has refreshed the token. A refresh token is single-use, so folders that each
# refreshed their own copy would invalidate each other's.
#
# Each tool's pull is compared with CLI 7's pull of the same scenario in the same run; only the
# .scindex cache may differ. Correctness suites (unit tests, tests/differential) run on their own.
#
# Results: $B/results/<env>-<timestamp>.tsv, and a Markdown table on stdout.
set -uo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
B=${TERN_BENCH:-$HOME/tern-bench}
SCR="$(cygpath -m "$REPO/bench")"
ENVS=$1; BUILD=$2
[ "$ENVS" = both ] && ENVS="local cloud"
TOOLS="official7 $BUILD"
TOOLS_DIR=$(cygpath -m "${TERN_BENCH_TOOLS:-$HOME/tern-bench-tools}")
RUNS=${RUNS:-3}
[[ " $ENVS " == *" cloud "* ]] && CLOUD_ENV=${CLOUD_ENV:?Set CLOUD_ENV to the endpoint of a SitecoreAI environment the suite may write to}
SETUP_TERN=$B/setup/tern.exe   # a known-good Tern for setup and resets, never one under test
PROBE=$B/cloud-probe
START=$(date +%s)

cmd_of() { case $1 in official7) echo $TOOLS_DIR/cli7/sitecore.exe ;; *) echo $B/$1/tern.exe ;; esac; }

# The folders the suite runs in: 7.x plugins in sitecore.json, CLI 7's plugin cache linked in.
FOLDERS="$B/proj $B/content $B/all $(ls -d $B/sets/*/ 2>/dev/null | sed 's#/$##')"
prep_dir() {
  local d=$1
  [ -f $d/sitecore.json ] && sed -i 's/@6\.0\.23/@7.0.24/g; s/XMCloud@1\.1\.120/XMCloud@2.0.14/' $d/sitecore.json
  # The shared plugin cache, linked rather than copied; checked every time, since temp cleanup can remove the link.
  # A folder that had CLI 6's own cache keeps it, moved aside.
  if [ ! -d $d/.sitecore/package-cache/nuget/Sitecore.DevEx.Extensibility.Serialization.7.0.24 ]; then
    mkdir -p $d/.sitecore
    if [ -d $d/.sitecore/package-cache ]; then mv $d/.sitecore/package-cache $d/.sitecore/package-cache-v6.$$ 2>/dev/null || rm -f $d/.sitecore/package-cache; fi
    MSYS_NO_PATHCONV=1 cmd /c mklink /J "$(cygpath -w $d/.sitecore/package-cache)" "$(cygpath -w $TOOLS_DIR/v7plugins/.sitecore/package-cache)" > /dev/null
  fi
}

# One refresh in $PROBE, then its endpoints everywhere (see Logins above).
share_login() {
  local env=$1
  (cd $PROBE && $SETUP_TERN item query /sitecore -n $env --limit 1 > $LOG/login.log 2>&1) || { echo "LOGIN FAILED for $env: $(tail -1 $LOG/login.log). Log in again in $PROBE."; exit 1; }
  python "$SCR/endpoint.py" "$(cygpath -w $PROBE/.sitecore/user.json)" xmCloud default ${CLOUD_ENV:-} -- \
    $(for d in $FOLDERS $(ls -d $B/pulls/*/*/ 2>/dev/null | sed 's#/$##'); do [ -f $d/.sitecore/user.json ] && cygpath -w $d/.sitecore/user.json; done)
}

# A folder for one pull scenario: the source folder's configuration, no items.
pull_dir() {
  local src=$1 name=$2 d=$B/pulls/$ENVN/$2
  if [ ! -f $d/sitecore.json ]; then
    mkdir -p $d; cp -r $src/.sitecore $d/ 2>/dev/null; rm -rf $d/.sitecore/package-cache; cp $src/*.json $d/
    [ -d $src/.config ] && cp -r $src/.config $d/
  fi
  prep_dir $d
  echo $d
}

# measure <scenario> <tool> <run> <dir> <args...>
measure() {
  local sc=$1 tool=$2 run=$3 dir=$4; shift 4
  local res=$LOG/m.txt; rm -f $res
  (cd $dir && $B/measure/measure.exe "$(cygpath -w $res)" $(cmd_of $tool) "$@" > "$LOG/$tool.log" 2>&1)
  read ms ws commit cpu rc < $res
  printf "%s\t%s\t%s\t%s\t%s\t%.1f\t%s\n" "$sc" $tool $run $ms $rc $(awk -v b=$ws 'BEGIN{print b/1048576}') $cpu >> $OUT
  [ "$rc" = 0 ] || { echo "  FAILED: $tool $sc (exit $rc): $(grep -v '^\s*at ' "$LOG/$tool.log" | tail -2 | tr '\n' ' ')"; }
}

# scenario <name> <dir> <setup or ""> <args...>: both tools, taking turns. LARGE=1 runs it once.
scenario() {
  local sc=$1 dir=$2 setup=$3 runs=$RUNS; shift 3
  if [ -n "${ONLY:-}" ] && ! [[ "$sc" =~ $ONLY ]]; then return; fi
  [ "${LARGE:-0}" = 1 ] && runs=1
  for r in $(seq $runs); do
    for tool in $TOOLS; do
      [ -n "$setup" ] && (cd $dir && eval "$setup" > /dev/null 2>&1)
      measure "$sc" $tool $r $dir "$@"
      [ "${AFTER:-}" ] && $AFTER "$sc" $tool $dir
    done
  done
}

# CLI 7's pull is the reference; Tern's must match it.
check_pull() {
  local sc=$1 tool=$2 dir=$3 ref="$LOG/ref/$(echo "$1" | tr -c 'A-Za-z0-9\n' _)"
  if [ $tool = official7 ]; then rm -rf "$ref"; mkdir -p "$(dirname "$ref")"; cp -r $dir/items "$ref"; return; fi
  diff -rq -x '*.scindex' "$ref" $dir/items > $LOG/diff.txt 2>&1 || echo "  PULL DIFFERS: $tool $sc ($(wc -l < $LOG/diff.txt) paths; $LOG/diff.txt)"
}

# ser watch runs until it is stopped, so it is measured from outside: how long a change made on
# the CM takes to reach its Item file (WATCH_EDITS edits of one c1k item's Title, 1-3 s apart),
# and the watcher's CPU time and working set over WATCH_IDLE seconds with nothing changing,
# sampled with procstat.ps1. Each tool watches its own pull of c1k, started with --skip-pull.
WATCH_EDITS=${WATCH_EDITS:-8}; WATCH_IDLE=${WATCH_IDLE:-30}
watch_bench() {
  if [ -n "${ONLY:-}" ] && ! [[ "ser watch" =~ $ONLY ]]; then return; fi
  local dir item="/sitecore/system/Modules/Tern Bench c1k/f01/i0000100" tool
  dir=$(pull_dir $B/sets/c1k watch)
  local file="$dir/items/bench/Tern Bench c1k/f01/i0000100.yml"
  stat() { pwsh -NoProfile -File "$SCR/procstat.ps1" -Id $1 2>/dev/null; }
  row() { printf "%s\t%s\t%s\t%s\t%s\t%.1f\t%s\n" "$1" $2 $3 $4 $5 $(awk -v b=$6 'BEGIN{print b/1048576}') $7 >> $OUT; }
  for tool in $TOOLS; do
    (cd $dir && rm -rf items && $SETUP_TERN ser pull $EFLAG > /dev/null 2>&1)
    (cd $dir && exec $(cmd_of $tool) ser watch -s $EFLAG > $LOG/watch-$tool.log 2>&1) &
    local pid=$! waited=0
    until grep -qE "Watching .* for changes|Watcher is online" $LOG/watch-$tool.log 2>/dev/null || [ $waited -ge 120 ]; do sleep 1; waited=$((waited + 1)); done
    local wpid; wpid=$(cat /proc/$pid/winpid 2>/dev/null)
    if [ -z "$wpid" ] || [ $waited -ge 120 ]; then echo "  FAILED: $tool ser watch did not start: $(tail -2 $LOG/watch-$tool.log | tr '\n' ' ')"; kill $pid 2>/dev/null; continue; fi
    local i v t0 ms rc before after
    for i in $(seq $WATCH_EDITS); do
      # A random pause, so edits land anywhere in the watcher's poll cycle rather than locking
      # into step with it, which would favour whichever tool happened to start in a lucky phase.
      sleep $(awk -v r=$RANDOM 'BEGIN { printf "%.3f", 1 + (r % 2000) / 1000 }')
      read before _ _ < <(stat $wpid)
      v="Watch $i $RANDOM"   # plain, so the Item file never quotes it
      (cd $dir && MSYS_NO_PATHCONV=1 $SETUP_TERN item set "$item" $EFLAG --field "Title=$v") > $LOG/watch-set.log 2>&1 || echo "  WATCH EDIT FAILED: $(tail -1 $LOG/watch-set.log)"
      t0=$(date +%s%N); rc=1
      while [ $(( ($(date +%s%N) - t0) / 1000000 )) -lt 30000 ]; do grep -qF "$v" "$file" 2>/dev/null && { rc=0; break; }; sleep 0.02; done
      ms=$(( ($(date +%s%N) - t0) / 1000000 ))
      read after ws _ < <(stat $wpid)
      row "ser watch: CM edit to Item file" $tool $i $ms $rc $ws $((after - before))
      [ $rc = 0 ] || echo "  FAILED: $tool ser watch did not write the edit within 30 s"
    done
    read before _ _ < <(stat $wpid)
    sleep $WATCH_IDLE
    read after ws _ < <(stat $wpid)
    # The time column holds the CPU time used while idle, so the comparison reads like the others.
    row "ser watch: CPU while idle for ${WATCH_IDLE} s" $tool 1 $((after - before)) 0 $ws $((after - before))
    kill $pid 2>/dev/null; wait $pid 2>/dev/null
  done
}

DEL() { echo "mutation { deleteItem(input: { path: \"/sitecore/system/Modules/Tern Bench $1\", permanently: true }) { successful } }"; }
reset_set() { echo "$SETUP_TERN authoring query $EFLAG -q '$(DEL $1)'"; }
# Ten c1k items get a new Title in the files, so each push has exactly ten changes to send.
edit_ten() { for f in items/*/*/f00/i000000[0-9].yml; do sed -i "/Hint: Title/{n;s/Value: .*/Value: \"Item $RANDOM\"/}" "$f"; done; }
ensure_on_cm() { (cd $B/sets/$1 && $SETUP_TERN ser push $EFLAG > $LOG/setup.log 2>&1) || echo "  SETUP FAILED for $1: $(tail -2 $LOG/setup.log)"; }

STAMP=$(date +%Y%m%d-%H%M%S)
for ENVN in $ENVS; do
  OUT=$B/results/$ENVN-$STAMP.tsv; mkdir -p $B/results; : > $OUT
  LOG=$B/logs/suite-$ENVN; mkdir -p $LOG
  [ $ENVN = cloud ] && EFLAG="-n $CLOUD_ENV" || EFLAG=""
  T0=$(date +%s)

  # --- setup ---------------------------------------------------------------------------------
  for d in $FOLDERS; do prep_dir $d; done
  share_login $([ $ENVN = cloud ] && echo $CLOUD_ENV || echo default)
  for s in c1k c10k m100; do ensure_on_cm $s; done
  for s in c100n m100n; do (cd $B/sets/$s && eval "$(reset_set $s)" > /dev/null 2>&1); done
  echo "== $ENVN: setup $(( $(date +%s) - T0 )) s"

  # --- scenarios -----------------------------------------------------------------------------
  P=$B/proj; S=$B/sets
  if [ $ENVN = local ]; then
    # Commands that never talk to the CM run only on local.
    scenario "Start up (--version)" $P "" --version
    scenario "ser info" $P "" ser info
    LARGE=1 scenario "ser validate (whole project)" $P "" ser validate
    LARGE=1 scenario "itemres create (whole project)" $P "" itemres create -o bench-res --overwrite
  fi
  LARGE=1 scenario "ser pull --what-if (whole project)" $P "" ser pull -w -fr $EFLAG
  LARGE=1 scenario "ser push --what-if (whole project)" $P "" ser push -w $EFLAG
  AFTER=check_pull scenario "Full pull (content only)" $(pull_dir $B/content content) "rm -rf ./items" ser pull $EFLAG
  scenario "Full push, in sync (content only)" $(pull_dir $B/content content) "" ser push $EFLAG
  AFTER=check_pull scenario "Full pull (content + media)" $(pull_dir $B/all all) "rm -rf ./items" ser pull $EFLAG
  scenario "Full push, in sync (content + media)" $(pull_dir $B/all all) "" ser push $EFLAG
  scenario "Create 100 content items" $S/c100n "$(reset_set c100n)" ser push $EFLAG
  scenario "Create 100 media items" $S/m100n "$(reset_set m100n)" ser push $EFLAG
  AFTER=check_pull scenario "Pull 100 media items" $(pull_dir $S/m100 m100) "rm -rf ./items" ser pull $EFLAG
  scenario "Push 10 changes (1,000 items)" $S/c1k edit_ten ser push $EFLAG
  LARGE=1 AFTER=check_pull scenario "Pull 1,000 content items" $(pull_dir $S/c1k c1k) "rm -rf ./items" ser pull $EFLAG
  LARGE=1 scenario "Push 1,000 content items, in sync" $S/c1k "" ser push $EFLAG
  # A developer watches the CM they are working on, so watch runs only on local.
  [ $ENVN = local ] && watch_bench
  LARGE=1 AFTER=check_pull scenario "Pull 10,000 content items" $(pull_dir $S/c10k c10k) "rm -rf ./items" ser pull $EFLAG
  LARGE=1 scenario "Push 10,000 content items, in sync" $S/c10k "" ser push $EFLAG
  # Leave the create sets as setup expects them: absent from the CM.
  for s in c100n m100n; do (cd $S/$s && eval "$(reset_set $s)" > /dev/null 2>&1); done

  echo "== $ENVN: $(( $(date +%s) - T0 )) s -> $OUT"
  python "$SCR/report.py" "$(cygpath -w $OUT)" $TOOLS
done
echo "== total $(( $(date +%s) - START )) s"
