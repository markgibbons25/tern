# Testing

| Layer | What it proves | Where it runs |
|---|---|---|
| Unit (`tests/Tern.Core.Tests`) | Item file format, Signatures (pinned to values a CM computed), path rules, the Plan engine, the login store, field-level push commands | CI and locally |
| Corpus | Every Item file in real projects round-trips byte for byte and maps to its own path (`TERN_CORPUS`, `TERN_PROJECTS`) | Locally only; the content isn't ours to commit |
| AOT build | The Native AOT publish succeeds and the binary runs, on Windows, Linux and macOS | CI |
| Differential (`tests/differential`, `tests/Tern.Differential`) | Tern and the Official CLI give identical results | Locally only ([ADR 0009](adr/0009-cloud-credentials-never-reach-ci.md)) |
| Benchmarks (`bench`) | Tern is no slower than the Official CLI on any operation | Locally only |

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

- **Suite:** `bench/suite.sh <quick|full> <local|cloud> <build>...` times each scenario for each tool and reports median wall time, peak memory and CPU. Its header lists the scenarios and options. `CLI=1` measures Sitecore CLI 6 and 7 as well; otherwise their saved numbers are reported. A `full` local run also runs the unit tests, the push differential test and the command tree check against each Tern build.
- **Workspace:** everything that isn't committed (Tern builds, project copies, the fixture, generated sets, logs, results) lives in `TERN_BENCH` (default `~/tern-bench`), and Sitecore CLI 7 with its plugins lives in `TERN_BENCH_TOOLS` (default `~/tern-bench-tools`). Put each Tern build under test in its own folder, such as `$TERN_BENCH/aot/tern.exe`, and build the timer with `dotnet publish bench/measure -c Release -o $TERN_BENCH/measure`.
- **Synthetic sets:** `bench/make-bench.py <out-dir> <content|media> <N>` writes N items under `/sitecore/system/Modules/Tern Bench <set>`. The suite creates the sets it needs.
- **Tuning:** `bench/tune.sh <local|cloud> <build>` sweeps the `TERN_*` performance settings one at a time ([settings](settings.md)).
- **Quick table:** `bench/run.sh` times one project and the push fixture with `date` alone and prints a Markdown table.

## Targets

- **Local CM:** a SitecoreAI local container stack, such as the one Sitecore's starter solutions ship. Local stacks usually share ports (443, 1433, 8984), so run one at a time and pause the others with `docker compose stop`. Don't stop a stack with a script that runs `docker system prune`: it deletes every stopped container, other projects' included.
- **Cloud:** a SitecoreAI test environment only. Reads run anywhere on it; writes go only to the scratch root.
- **On-prem:** XM 10.4 containers, from M5. This needs the XM images pulled and a licence.

## Server evidence

`tern --record <dir>` writes every request and response with tokens, secrets and blob contents removed. Use it to confirm protocol notes against a live CM ([ADR 0001](adr/0001-clean-room-protocol.md)).
