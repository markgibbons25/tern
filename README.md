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

Measured on 2026-10-20 against Sitecore CLI 6.0.23 and 7.0.24, on one Windows machine. Tern is the Native AOT build. "Local" is a SitecoreAI container on that machine; "SitecoreAI" is a personal SitecoreAI environment. The project is a sample one: 2,986 items in 5 modules, plus its content-sync modules (1,478 content and 80 media items). Scale tests use generated sets of up to 10,000 items, under their own scratch roots. Each pull was checked file for file against the Sitecore CLI's, and each push against the Sitecore CLI's view of the CM afterwards.

| Scenario | Local: CLI 6 | Local: CLI 7 | Local: Tern | SitecoreAI: CLI 6 | SitecoreAI: CLI 7 | SitecoreAI: Tern |
|---|---|---|---|---|---|---|
| Start up (`--version`) | 0.35 s | 0.65 s | **0.03 s** | | | |
| `ser info` | 0.89 s | 3.04 s | **0.09 s** | | | |
| `ser validate` (whole project) | 6.28 s | 36.66 s | **0.24 s** | | | |
| `itemres create` (whole project) | 7.13 s | 13.62 s | **0.56 s** | | | |
| `ser pull --what-if` (whole project) | 10.63 s | 12.67 s | **1.25 s** | 14.03 s | 17.74 s | **1.07 s** |
| `ser push --what-if` (whole project) | 11.36 s | 16.75 s | **1.15 s** | 10.37 s | 16.25 s | **1.12 s** |
| `ser push` (7 changes) | 1.22 s | 1.18 s | **0.27 s** | 1.64 s | 1.48 s | **0.58 s** |
| `ser push` with nothing to do | 1.16 s | 1.03 s | **0.24 s** | 1.39 s | 1.11 s | **0.10 s** |
| Full pull, content only | 2.88 s | 2.51 s | **1.44 s** | 2.76 s | 2.75 s | **1.30 s** |
| Full pull, content + media | 5.71 s | 5.66 s | **4.29 s** | 6.63 s | 6.59 s | **2.87 s** |
| Full push in sync, content + media | 3.84 s | 3.36 s | **0.33 s** | 3.28 s | 4.90 s | **0.39 s** |
| Create 100 content items | 3.34 s | 3.56 s | **2.02 s** | 9.41 s | 9.62 s | **8.03 s** |
| Create 1,000 content items | 27.62 s | 24.89 s | **17.37 s** | 83.80 s | 83.26 s | **80.36 s** |
| Create 1,000 media items | 23.61 s | 18.62 s | **16.05 s** | 67.39 s | 55.95 s | **49.99 s** |
| Pull 1,000 media items | 3.23 s | 3.15 s | **1.92 s** | 9.00 s | 9.08 s | **4.29 s** |
| Pull 10,000 content items | 11.36 s | 10.93 s | **8.26 s** | 12.14 s | 29.37 s | **4.99 s** |
| Push 10,000 content items in sync | 3.85 s | 4.15 s | **1.83 s** | 4.44 s | 4.34 s | **1.60 s** |

Where Tern's lead is small, the CM is doing the work: creating items costs the server about 13 ms each locally and 70 ms on SitecoreAI, whichever tool sends them. The client-side work Tern does is a fraction of the Sitecore CLI's; for example, a 10,000-item push with nothing to change used 0.19 s of CPU, against 4.5 s for CLI 6.

**Memory** (peak working set). Tern uses 11–85 MB where the Sitecore CLIs use 64–150 MB. With media, a full pull peaks at 207 MB (CLI 6: 378 MB, CLI 7: 401 MB) and an in-sync push at 46 MB (CLI 6: 329 MB).

**Install size.** Tern is one 11.9 MB file and needs no runtime. CLI 6 is 11.6 MB, plus 36.7 MB of plugins in every project's `.sitecore/package-cache`, plus the .NET 8 runtime (70 MB). CLI 7 is the same shape on .NET 10, with 31 MB of plugins.

What makes the difference, and the settings behind it, are in [docs/settings.md](docs/settings.md).

## How it stays compatible

- Every command is tested side by side with the Sitecore CLI ([docs/testing.md](docs/testing.md)).
- Where Tern behaves differently, it is only where the Sitecore CLI fails, and every case is listed in [docs/deviations.md](docs/deviations.md).

## Docs

- [Glossary](GLOSSARY.md)
- [Roadmap](docs/roadmap.md)
- [CLI contract](docs/cli-contract.md)
- [Settings](docs/settings.md)
- [Testing](docs/testing.md)
- [Protocol notes](docs/protocol/)
- [Decisions](docs/adr/)
- [Agent skill](skills/tern/SKILL.md)

## License

MIT
