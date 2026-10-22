#!/usr/bin/env bash
# Compares the three ways Tern copies content between two SitecoreAI environments:
# content sync (pull to Item files, push from them), content diff --push (CM to CM through
# Management Services) and content transfer (CM to CM through the Content Transfer API).
#
#   ITEM=<item path> [CONTENT_MODULE=content] [MEDIA_MODULE=media] \
#     bench/content-bench.sh <project-dir> <tern-exe> <source> <target> [runs] [transfer-runs]
#
# <project-dir> holds sitecore.json, a content module and a media module (named by CONTENT_MODULE
# and MEDIA_MODULE, as --module takes them), and a .sitecore/user.json with both endpoints. ITEM
# is a page in the content module: the single-item scenarios copy it, and the "10 changed"
# scenario edits the first ten items below it in the target before each run. Every scenario
# writes to <target>, which ends up matching <source> for those modules, so use an environment
# nobody else depends on. A sync deletes what the target has and the source lacks; back such
# items up first.
#
# Each run is measured with $MEASURE (bench/measure): wall time, peak working set and CPU. The
# single-item and media scenarios run [runs] times (default 3), and a transfer [transfer-runs]
# times (default 1), and the median is reported. The content module scenarios, which are meant
# for 1,000 items or more, run LARGE_RUNS times (default 1): one run is steady enough, and
# --wait alone takes minutes each. Keep the media module small, or set LARGE_RUNS for it too.
# Results: a TSV in $OUT_DIR (default <project-dir>/results) and a Markdown table on stdout.
# ONLY=<regex> runs just the scenarios whose names match, and METHODS=<regex> just the methods.
# GAP=<seconds> waits before each run: a transfer's target is busy for about 10 s after its load
# starts, so back-to-back runs wait for each other.
set -uo pipefail
P=$(cygpath -m "$1"); TERN=$2; SRC=$3; DST=$4; RUNS=${5:-3}; XRUNS=${6:-1}; LARGE_RUNS=${LARGE_RUNS:-1}
MEASURE=${MEASURE:-$(cygpath -m "${TERN_BENCH:-$HOME/tern-bench}")/measure/measure.exe}
OUT_DIR=${OUT_DIR:-$P/results}; mkdir -p "$OUT_DIR"
STAMP=$(date +%Y%m%d-%H%M%S); TSV=$OUT_DIR/content-$STAMP.tsv; LOG=$OUT_DIR/content-$STAMP.log
export MSYS_NO_PATHCONV=1
ITEM=${ITEM:?Set ITEM to a page in the content module}
CONTENT_MODULE=${CONTENT_MODULE:-content}; MEDIA_MODULE=${MEDIA_MODULE:-media}
# The ten items a "10 changed" scenario edits in the target before each run: the first ten below ITEM.
mapfile -t CHANGED < <("$TERN" -c "$P" item query "$ITEM" -n "$SRC" --limit 11 --json 2>/dev/null \
  | python -c "import json,sys; [print(i['path']) for i in json.load(sys.stdin)['items'][1:]]")
[ ${#CHANGED[@]} -eq 10 ] || { echo "ITEM needs at least ten items below it in $SRC; found ${#CHANGED[@]}." >&2; exit 2; }

median() { sort -n | awk '{a[NR]=$1} END {print (NR%2 ? a[(NR+1)/2] : (a[NR/2]+a[NR/2+1])/2)}'; }

drift_home() { "$TERN" -c "$P" item set "$ITEM" -n "$DST" --field "__Display name=bench $RANDOM" >> "$LOG" 2>&1; }
drift_ten() { for p in "${CHANGED[@]}"; do "$TERN" -c "$P" item set "$p" -n "$DST" --field "__Display name=bench $RANDOM" >> "$LOG" 2>&1; done; }
cold() { rm -rf "$P/items"; }

# run <scenario> <method> <runs> <setup function or ""> <tern args...>
run() {
  local scenario=$1 method=$2 n=$3 setup=$4; shift 4
  [[ -n "${ONLY:-}" && ! "$scenario" =~ $ONLY ]] && return
  [[ -n "${METHODS:-}" && ! "$method" =~ $METHODS ]] && return
  local tmp; tmp=$(cygpath -m "$(mktemp)")
  for i in $(seq "$n"); do
    # A transfer's target is busy for about 10 s after its load starts, so back-to-back runs wait;
    # GAP=<seconds> spaces the runs out, as separate transfers would be.
    [ -n "${GAP:-}" ] && sleep "$GAP"
    [ -n "$setup" ] && $setup
    echo "### $scenario | $method | run $i: tern $*" >> "$LOG"
    local json=$OUT_DIR/last.json
    "$MEASURE" "$tmp" "$TERN" -c "$P" "$@" --json > "$json" 2>> "$LOG"
    cat "$json" >> "$LOG"
    # The transfer's own phase times, when there are any: prepare, copy and load, in ms.
    local phases; phases=$(python -c "
import json,sys
try: d=json.load(open(sys.argv[1]))
except Exception: print('-'); sys.exit()
t=d.get('transfers') or []
print(' '.join(str(x) for x in [sum(r.get('prepareMs',0) for r in t), sum(r.get('copyMs',0) for r in t), sum(f.get('loadMs',0) for r in t for f in r.get('files',[]))]) if t else '-')" "$json")
    echo "$phases" >> "$tmp.phases"
  done
  local ms ws cpu ok
  ms=$(awk '{print $1}' "$tmp" | median); ws=$(awk '{print $2}' "$tmp" | median); cpu=$(awk '{print $4}' "$tmp" | median)
  ok=$(awk '$5!=0{f=1} END{print f?"FAILED":"ok"}' "$tmp")
  # Each transfer phase's median, over the runs that report them.
  local phases="-"
  if grep -qv '^-$' "$tmp.phases"; then
    phases=$(for c in 1 2 3; do grep -v '^-$' "$tmp.phases" | awk -v c=$c '{print $c}' | median; done | paste -sd' ')
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$scenario" "$method" "$n" "$ms" "$ws" "$cpu" "$ok" "$phases" >> "$TSV"
  # The median can be fractional, which shell arithmetic cannot take.
  awk -v s="$scenario" -v m="$method" -v ms="$ms" -v ok="$ok" 'BEGIN { printf "%s | %s: %.1f s (%s)\n", s, m, ms/1000, ok }' >&2
  rm -f "$tmp" "$tmp.phases"
}

ONE=(-p "$ITEM" --scope SingleItem)
for scenario in "1 item, in sync" "1 item, changed"; do
  setup=""; [ "$scenario" = "1 item, changed" ] && setup=drift_home
  run "$scenario" "content sync" "$RUNS" "$setup" content sync -s "$SRC" -d "$DST" "${ONE[@]}"
  run "$scenario" "content diff --push" "$RUNS" "$setup" content diff -s "$SRC" -d "$DST" "${ONE[@]}" --push
  run "$scenario" "content transfer" "$XRUNS" "$setup" content transfer -s "$SRC" -d "$DST" "${ONE[@]}"
  run "$scenario" "content transfer --wait" "$XRUNS" "$setup" content transfer -s "$SRC" -d "$DST" "${ONE[@]}" --wait
done

run "Content module, in sync" "content sync" "$LARGE_RUNS" cold content sync -s "$SRC" -d "$DST" -m "$CONTENT_MODULE"
run "Content module, in sync" "content sync, files kept" "$LARGE_RUNS" "" content sync -s "$SRC" -d "$DST" -m "$CONTENT_MODULE"
run "Content module, in sync" "content diff --push" "$LARGE_RUNS" "" content diff -s "$SRC" -d "$DST" -m "$CONTENT_MODULE" --push
run "Content module, in sync" "content transfer" "$LARGE_RUNS" "" content transfer -s "$SRC" -d "$DST" -m "$CONTENT_MODULE"
run "Content module, in sync" "content transfer --wait" "$LARGE_RUNS" "" content transfer -s "$SRC" -d "$DST" -m "$CONTENT_MODULE" --wait

drift_cold() { drift_ten; cold; }
run "Content module, 10 changed" "content sync" "$LARGE_RUNS" drift_cold content sync -s "$SRC" -d "$DST" -m "$CONTENT_MODULE"
run "Content module, 10 changed" "content diff --push" "$LARGE_RUNS" drift_ten content diff -s "$SRC" -d "$DST" -m "$CONTENT_MODULE" --push
run "Content module, 10 changed" "content transfer" "$LARGE_RUNS" drift_ten content transfer -s "$SRC" -d "$DST" -m "$CONTENT_MODULE"
run "Content module, 10 changed" "content transfer --wait" "$LARGE_RUNS" drift_ten content transfer -s "$SRC" -d "$DST" -m "$CONTENT_MODULE" --wait

run "Media module, in sync" "content sync" "$RUNS" cold content sync -s "$SRC" -d "$DST" -m "$MEDIA_MODULE"
run "Media module, in sync" "content diff --push" "$RUNS" "" content diff -s "$SRC" -d "$DST" -m "$MEDIA_MODULE" --push
run "Media module, in sync" "content transfer" "$XRUNS" "" content transfer -s "$SRC" -d "$DST" -m "$MEDIA_MODULE"
run "Media module, in sync" "content transfer --wait" "$XRUNS" "" content transfer -s "$SRC" -d "$DST" -m "$MEDIA_MODULE" --wait

run "Content + media, in sync" "content sync" "$LARGE_RUNS" cold content sync -s "$SRC" -d "$DST"
run "Content + media, in sync" "content diff --push" "$LARGE_RUNS" "" content diff -s "$SRC" -d "$DST" --push
run "Content + media, in sync" "content transfer" "$LARGE_RUNS" "" content transfer -s "$SRC" -d "$DST"
run "Content + media, in sync" "content transfer --wait" "$LARGE_RUNS" "" content transfer -s "$SRC" -d "$DST" --wait

echo "| Scenario | Method | Runs | Wall time | Peak memory | CPU | Transfer: prepare / copy / load (median) |"
echo "|---|---|---|---|---|---|---|"
awk -F'\t' '{ ph = ""; if ($8 != "-" && $8 != "") { split($8, p, " "); ph = sprintf("%.1f / %.1f / %.1f s", p[1]/1000, p[2]/1000, p[3]/1000) }
  printf "| %s | %s | %s | %.2f s | %.0f MB | %.2f s | %s |%s\n", $1, $2, $3, $4/1000, $5/1048576, $6/1000, ph, ($7=="ok"?"":" FAILED") }' "$TSV"
echo
echo "Raw results: $TSV; log: $LOG" >&2
