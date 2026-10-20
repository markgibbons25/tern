# Testing

| Layer | What it proves | Where it runs |
|---|---|---|
| Unit (`tests/Tern.Core.Tests`) | Item file format, Signatures (pinned to values a CM computed), path rules, the Plan engine, the login store, field-level push commands | CI and locally |
| Corpus | Every Item file in real projects round-trips byte for byte and maps to its own path (`TERN_CORPUS`, `TERN_PROJECTS`) | Locally only; the content isn't ours to commit |
| AOT build | The Native AOT publish succeeds and the binary runs, on Windows, Linux and macOS | CI |
| Differential (`tests/differential`, `tests/Tern.Differential`) | Tern and the Official CLI give identical results | Locally only ([ADR 0009](adr/0009-cloud-credentials-never-reach-ci.md)) |
| Benchmarks (`bench/run.sh`) | Tern is no slower than the Official CLI on any operation | Locally only |

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
- **Through xUnit:** `TERN_DIFFERENTIAL_FIXTURE` and `TERN_EXE` make `Tern.Differential` run the push script.
- Plans may list siblings in a different order from the Official CLI; the scripts compare sets and resulting state, not order ([deviations](deviations.md)).

## Targets

- **Local CM:** a SitecoreAI local container stack, such as the one Sitecore's starter solutions ship. Local stacks usually share ports (443, 1433, 8984), so run one at a time and pause the others with `docker compose stop`. Don't stop a stack with a script that runs `docker system prune`: it deletes every stopped container, other projects' included.
- **Cloud:** a SitecoreAI test environment only. Reads run anywhere on it; writes go only to the scratch root.
- **On-prem:** XM 10.4 containers, from M5. This needs the XM images pulled and a licence.

## Server evidence

`tern --record <dir>` writes every request and response with tokens, secrets and blob contents removed. Use it to confirm protocol notes against a live CM ([ADR 0001](adr/0001-clean-room-protocol.md)).
