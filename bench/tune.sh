#!/usr/bin/env bash
# One-knob-at-a-time tuning sweep for a Tern build against an environment.
# Usage: [SET=c100n] [BATCHES="10 26 100"] [CLOUD_ENV=<endpoint>] tune.sh <local|cloud> <build> [runs]
# Each row is printed as soon as it is measured: the median wall time per scenario.
# The workspace is the suite's (suite.sh); run the suite once first so the pulls and sets exist.
B=${TERN_BENCH:-$HOME/tern-bench}
ENVN=$1; T=$B/$2/tern.exe; RUNS=${3:-3}
SET=${SET:-c100n}; BATCHES=${BATCHES:-10 26 100}
[ $ENVN = cloud ] && EFLAG="-n ${CLOUD_ENV:?Set CLOUD_ENV to the endpoint of a SitecoreAI environment the sweep may write to}" || EFLAG=""
DEL="mutation { deleteItem(input: { path: \"/sitecore/system/Modules/Tern Bench $SET\", permanently: true }) { successful } }"
med() { sort -n | awk '{a[NR]=$1} END{print a[int((NR+1)/2)]}'; }
time_it() { # time_it <runs> <dir> <setup> <env assignments> <args...>
  local runs=$1 dir=$2 setup=$3 envs=$4; shift 4
  for i in $(seq $runs); do
    [ -n "$setup" ] && (cd $dir && eval "$setup" > /dev/null 2>&1)
    local s=$(date +%s%N)
    (cd $dir && env $envs $T "$@" $EFLAG > /dev/null 2>&1)
    echo $(( ($(date +%s%N) - s) / 1000000 ))
  done | med
}
PULL1K=$B/pulls/$ENVN/c1k; PROJ=$B/proj
row() {
  printf "%-36s pull-1k %6s ms   pull-whatif-proj %6s ms\n" "$1" \
    "$(time_it $RUNS $PULL1K 'rm -rf ./items' "$1" ser pull)" \
    "$(time_it $RUNS $PROJ '' "$1" ser pull -w -fr)"
}
echo "== $ENVN, $2, runs $RUNS"
for p in 4 8 16 32; do row "TERN_PARALLEL_REQUESTS=$p"; done
for a in 10 26 50 100; do row "TERN_ALIASES_PER_REQUEST=$a"; done
for h in false true; do row "TERN_HTTP2=$h"; done
CS=$B/sets/$SET
for w in $BATCHES; do for h in false true; do
  printf "%-36s create-%s %6s ms\n" "TERN_WRITE_BATCH_SIZE=$w TERN_HTTP2=$h" $SET \
    "$(time_it 1 $CS "$B/setup/tern.exe authoring query $EFLAG -q '$DEL'" "TERN_WRITE_BATCH_SIZE=$w TERN_HTTP2=$h" ser push)"
done; done
(cd $CS && $B/setup/tern.exe authoring query $EFLAG -q "$DEL" > /dev/null 2>&1)
