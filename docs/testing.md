# Testing

| Layer | What it proves | Where it runs |
|---|---|---|
| Unit (`tests/Tern.Core.Tests`) | Configuration rules, Signatures, Plan engine | CI and locally |
| Golden files | Item file reading and writing, byte for byte, using no CM | CI and locally |
| AOT build | The Native AOT publish succeeds on Windows, Linux and macOS | CI |
| Differential (`tests/Tern.Differential`) | Tern and the Official CLI give identical results | Locally only ([ADR 0009](adr/0009-cloud-credentials-never-reach-ci.md)) |
| Benchmarks (`bench/Tern.Benchmarks`) | Tern is no slower than the Official CLI on any operation | Locally only |

All tests use xUnit v3.

## Differential tests

- They run the **published Native AOT binary**, not the library in-process, so anything that trimming breaks is caught.
- The Official CLI is downloaded from Sitecore's NuGet feed. It is never stored in the repo.
- **Pull** compares the file bytes each tool writes. **Push** compares the CM state each tool leaves behind.
- **Resetting before a push run** happens inside a scratch root. The Official CLI pushes a baseline fixture with `CreateUpdateAndDelete` before each tool's run, because Tern can't be trusted to reset state before it has been proven. The result is then snapshotted with the `serialize` query. Snapshots are compared without the fields that change on every save (`__Updated`, `__Revision`, …).
- Each Deviation in `docs/deviations.md` has a test showing that the Official CLI fails where Tern succeeds.

## Targets

- **Local CM:** a SitecoreAI local container stack, such as the one Sitecore's starter solutions ship. Local stacks usually share ports (443, 1433, 8984), so run one at a time and pause the others with `docker compose stop`. Don't stop a stack with a script that runs `docker system prune`: it deletes every stopped container, other projects' included.
- **Cloud:** a SitecoreAI test environment only, behind a host allowlist. Writes go only to a scratch root.
- **On-prem:** XM 10.4 containers, from M5.

## Server evidence

Schema introspection of each target CM, plus redacted recordings from Tern's `--record <dir>`, are committed as fixtures ([ADR 0001](adr/0001-clean-room-protocol.md)).
