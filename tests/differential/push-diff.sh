#!/usr/bin/env bash
# Differential push test (docs/testing.md). Runs the same push with the Official CLI and with Tern
# from the same CM state, snapshots the CM after each with an Official CLI pull, and compares the
# snapshots with the fields that change on every save removed.
#
# Usage: push-diff.sh <fixture-dir> <tern-exe>
#   <fixture-dir> holds s0 (baseline) and s1 (changed) projects from make-fixture.py, each with a
#   .sitecore/user.json whose default endpoint is the Local CM.
set -euo pipefail

F="$1"
TERN="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"

push_official() { (cd "$F/$1" && dotnet sitecore ser push > "$F/log-official-$1-$2.txt" 2>&1); }
push_tern() { (cd "$F/$1" && "$TERN" ser push > "$F/log-tern-$1-$2.txt" 2>&1); }

# A snapshot is an Official CLI pull of the scratch root into an empty copy of the project.
snapshot() {
  local out="$F/snap-$1"
  rm -rf "$out"
  mkdir -p "$out"
  cp -r "$F/s0/sitecore.json" "$F/s0/TernTests.module.json" "$F/s0/.config" "$F/s0/.sitecore" "$F/s0/NuGet.config" "$out/"
  (cd "$out" && dotnet sitecore ser pull > "$F/log-snapshot-$1.txt" 2>&1)
  python "$HERE/normalize.py" "$out/items"
}

compare() {
  if diff -r "$F/snap-$1/items" "$F/snap-$2/items" > "$F/diff-$1-$2.txt"; then
    echo "PASS: $1 == $2"
  else
    echo "FAIL: $1 != $2 (see $F/diff-$1-$2.txt)"
    head -40 "$F/diff-$1-$2.txt"
    return 1
  fi
}

status=0
echo "== baseline"
push_official s0 baseline
snapshot baseline

echo "== s0 -> s1"
push_official s1 forward
snapshot official-forward
push_official s0 reset1
push_tern s1 forward
snapshot tern-forward
compare official-forward tern-forward || status=1

echo "== s1 -> s0"
push_official s0 back
snapshot official-back
push_official s1 reset2
push_tern s0 back
snapshot tern-back
compare official-back tern-back || status=1

echo "== no-op push"
push_tern s0 noop
grep -q "Discovered 0 changes" "$F/log-tern-s0-noop.txt" && echo "PASS: no-op push found no changes" || { echo "FAIL: no-op push found changes"; status=1; }

exit $status
