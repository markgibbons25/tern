# Tern

Tern is an open-source, Native AOT command-line tool for Sitecore (SitecoreAI and XM/XP 10.1–10.4).

- **It's a drop-in replacement for the Sitecore CLI.** It uses the same `sitecore.json` and Module files, writes byte-identical Item files, and shares the same stored logins (`.sitecore/user.json`). Existing scripts keep working when `dotnet sitecore` becomes `tern`.
- **It's faster.** See [Performance](#performance).
- **It adds what the Sitecore CLI lacks:**
  - Plans you save, review and apply, which are refused if the target changed in the meantime
  - `--json` on every command, for scripts and agents
  - `item get` and `item query`
  - client credentials from environment variables

> Status: `0.1.x`, pre-release. Tern is not affiliated with or endorsed by Sitecore.

## Install

```bash
dotnet tool install --global Tern.Cli
```

Or add it to a repo's tool manifest next to `sitecore.cli` and run `dotnet tern`. Standalone binaries for Windows, Linux and macOS are attached to each release.

## Use

Tern reads your existing configuration. Run it from the folder that holds `sitecore.json`:

```bash
tern login --authority https://auth.sitecorecloud.io --cm https://xmcloudcm.localhost --allow-write true
tern ser pull
tern ser push --what-if
tern ser diff -s dev -d uat
```

### Review, then apply

```bash
tern ser push --plan-out push.json      # nothing changes yet
tern apply push.json                     # exactly what was reviewed, or nothing
```

A Plan records the Signature it expects each target item to have. If anyone changes one of those items before you apply, Tern stops rather than overwriting their work.

### Read content without serializing it

```bash
tern item get /sitecore/content/Home --format json
tern item query /sitecore/content/Site --template <template-id> --field "Title=*News*" --show Title
```

## Commands

| Group | Commands |
|---|---|
| Login | `login`, `connect`, `env list/current/set-default/info`, `cloud login/logout` |
| Serialization | `ser info`, `explain`, `validate`, `pull`, `push`, `diff`, `watch`, `package create/install`, `apply` |
| Items | `item get`, `item query`, `item set` |
| Jobs | `publish` (and `publish item`, `list-targets`), `index list/statistic/rebuild/schema-populate`, `dbcleanup` |
| Resource packages | `itemres create`, `itemres unicorn`, `itemres cleanup` |
| SitecoreAI | `cloud project/environment/deployment/editinghost/organization …` |
| Beyond Management Services | `item set` and `authoring query` (Authoring API, no Administrator needed), `script run` (SPE remoting) |

Run `tern <command> --help` for options. They match the Sitecore CLI's.

## Performance

Measured on the same machine against the same local CM and project (2,981 items in 5 modules). The Sitecore CLI is 6.0.23. Each figure is the median of five runs, and `bench/run.sh` reproduces them.

| Scenario | Sitecore CLI | Tern | Speed-up |
|---|---|---|---|
| Start up (`--version`) | 0.40 s | 0.12 s | 3.4× |
| `ser info` | 0.96 s | 0.40 s | 2.4× |
| `ser validate` | 5.85 s | 1.23 s | 4.8× |
| `ser pull --what-if` (whole project) | 10.59 s | 1.53 s | 6.9× |
| `ser push --what-if` (whole project) | 11.63 s | 2.80 s | 4.2× |
| `itemres create` (whole project) | 5.64 s | 0.91 s | 6.2× |
| `ser push` (7 changes: create, update, move, rename, recycle) | 1.22 s | 0.49 s | 2.5× |
| `ser push` with nothing to do | 1.00 s | 0.35 s | 2.9× |

Tern here is a ReadyToRun build, not yet the Native AOT binary.

## How it stays compatible

- Every command is tested side by side with the Sitecore CLI ([docs/testing.md](docs/testing.md)).
- Where Tern behaves differently, it is only where the Sitecore CLI fails, and every case is listed in [docs/deviations.md](docs/deviations.md).

## Docs

- [Glossary](GLOSSARY.md)
- [Roadmap](docs/roadmap.md)
- [CLI contract](docs/cli-contract.md)
- [Testing](docs/testing.md)
- [Protocol notes](docs/protocol/)
- [Decisions](docs/adr/)
- [Agent skill](skills/tern/SKILL.md)

## License

MIT
