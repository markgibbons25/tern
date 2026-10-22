# Testing

| Layer | What it proves | Where it runs |
|---|---|---|
| Analyzers and format | The code passes the .NET analyzers' recommended rules and the `.editorconfig` code style (checked by every build, warnings are errors), and `dotnet format --verify-no-changes` finds nothing to change | CI and locally |
| Unit (`tests/Tern.Core.Tests`) | Item file format, Signatures (pinned to values a CM computed), path rules, the Plan engine, the login store, field-level push commands | CI and locally |
| Corpus | Every Item file in real projects round-trips byte for byte and maps to its own path (`TERN_CORPUS`, `TERN_PROJECTS`) | Locally only; the content isn't ours to commit |
| AOT build | The Native AOT publish succeeds and the binary runs, on Windows, Linux and macOS | CI |
| Differential (`tests/differential`, `tests/Tern.Differential`) | Tern and the Official CLI give identical results | Locally only ([ADR 0009](adr/0009-cloud-credentials-never-reach-ci.md)) |
| Benchmarks (`bench`) | Tern is no slower than Sitecore CLI 7 on any operation | Locally only |

All tests use xUnit v3 on Microsoft.Testing.Platform: `dotnet test --solution Tern.slnx`.

## Differential tests

- **Pull:** run both tools on two copies of a project and diff the folders. Only the `.scindex` caches may differ.
- **Push:** `tests/differential/push-diff.sh <fixture> <tern>` drives the synthetic fixture from `make-fixture.py` (`s0` baseline, `s1` changed) under the scratch root `/sitecore/system/Modules/Tern Tests`. For each direction:
  1. The Official CLI resets the CM to the starting state.
  2. One tool pushes.
  3. The Official CLI pulls a snapshot.
  4. Steps 1–3 repeat with the other tool.
  5. The two snapshots are compared after `normalize.py` removes the fields that change on every save.

  The Official CLI does the resets because Tern can't be trusted to reset state before it has been proven. The script also checks that a second push finds nothing to do.
- **Resource packages:** `tests/differential/compare-dat.py` compares two `.dat` files by content.
- **Commands and options:** `tests/differential/command-tree.py <project> <tern> <official command>` walks both tools' `--help` and lists every Official CLI command or option Tern lacks. Run it from a project whose `sitecore.json` names every plugin. Sitecore CLI 6.0.23 and 7.0.24 have the same 83 commands, and Tern has them all.
- **Through xUnit:** `TERN_DIFFERENTIAL_FIXTURE` and `TERN_EXE` make `Tern.Differential` run the push script.
- Plans may list siblings in a different order from the Official CLI; the scripts compare sets and resulting state, not order ([deviations](deviations.md)).

## Benchmarks

- **Suite:** `CLOUD_ENV=<endpoint> bench/suite.sh <local|cloud|both> <build>` times every scenario for Sitecore CLI 7 and one Tern build, taking turns, and reports median wall time, peak memory and CPU. `both` takes about 15 minutes. Its header lists the scenarios and options. Both tools are measured on every run, and each Tern pull is compared with CLI 7's pull from the same run.
- **Watch:** `ser watch` runs until stopped, so on local the suite measures it from outside. It edits one item's Title on the CM 8 times, 1–3 s apart at random so the edits land anywhere in the watcher's 2 s poll cycle, and reports the median time until the change is in the Item file. It also samples the watcher's CPU time and memory over 30 s with nothing changing, using `bench/procstat.ps1`.
- **Runs:** scenarios of fewer than 1,000 items run `RUNS` times (default 3) and report the median. Larger ones, and the whole-project what-ifs, run once: they are long enough to be steady.
- **Content:** nothing is generated outside a CM. The whole-project and full-pull scenarios use a copy of a real project. The sets under `/sitecore/system/Modules/Tern Bench <set>` (100 and 1,000 content items to create and keep, 10,000 to keep, 100 media items to create and keep) were made on the local CM by `bench/make-set.ps1`, so Sitecore assigned their IDs. They were pulled once into the workspace, and the suite pushes them to an environment that lacks them. No scenario touches more than 10,000 items.
- **Logins:** a refresh token is single-use, so the suite refreshes it once, in `$TERN_BENCH/cloud-probe`, and copies those endpoints into every folder it runs in. Log in there (`tern cloud login`) when it reports that the login failed.
- **Content transfer:** `ITEM=<page> bench/content-bench.sh <project> <tern> <source> <target>` compares `content sync`, `content diff --push` and `content transfer` between two environments ([content](content.md#performance-sync-diff---push-and-transfer)). Its header lists the settings. Every scenario writes to the target, so use an environment nobody else depends on. `--wait` transfers take minutes each, and back-to-back transfers queue background loads on the target, so space them out with `GAP=20`.
- **Workspace:** everything that isn't committed (Tern builds, project copies, the fixture, generated sets, logs, results) lives in `TERN_BENCH` (default `~/tern-bench`), and Sitecore CLI 7 with its plugins lives in `TERN_BENCH_TOOLS` (default `~/tern-bench-tools`). Put each Tern build under test in its own folder, such as `$TERN_BENCH/aot/tern.exe`, and build the timer with `dotnet publish bench/measure -c Release -o $TERN_BENCH/measure`.
- **Tuning:** `bench/tune.sh <local|cloud> <build>` sweeps the `TERN_*` performance settings one at a time ([settings](settings.md)).

## Targets

- **Local CM:** a SitecoreAI local container stack, such as the one Sitecore's starter solutions ship. Local stacks usually share ports (443, 1433, 8984), so run one at a time and pause the others with `docker compose stop`. Don't stop a stack with a script that runs `docker system prune`: it deletes every stopped container, other projects' included.
- **Cloud:** a SitecoreAI environment kept for testing, named to the scripts with `CLOUD_ENV`. Reads run anywhere on it; writes go only to the scratch roots. Content scenarios, such as `content-bench.sh`, write real content, so use an environment nobody else depends on.
- **On-prem:** XM 10.4 containers, from M5. This needs the XM images pulled and a licence.

## Server evidence

`tern --record <dir>` writes every request and response with tokens, secrets and blob contents removed. Use it to confirm protocol notes against a live CM ([ADR 0001](adr/0001-clean-room-protocol.md)).
