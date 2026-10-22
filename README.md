# Tern

[![NuGet](https://img.shields.io/nuget/v/Tern.Cli)](https://www.nuget.org/packages/Tern.Cli)

**The Sitecore Community CLI: fast, safe, and built for scripts and AI agents.** Tern is an open-source, native command-line tool for Sitecore, made by the Sitecore Community. It reads your existing `sitecore.json`, Module files and logins, writes byte-identical Item files, and takes the same commands and options. Many commands run ten times faster or more.

```bash
dotnet tool install --global Tern.Cli
tern ser pull          # the command you already know, as tern
```

Tern is named after the Arctic tern, a small, fast seabird that makes the longest migration of any animal, from the Arctic to the Antarctic and back every year. Tern the CLI is quick too, and moves content between environments.

## Why Tern

### It's much faster

Measured against Sitecore CLI 7 on the same project and the same CM ([details](#performance)):

| | Sitecore CLI 7 | Tern | |
|---|---|---|---|
| `ser pull --what-if`, whole project, SitecoreAI | 18.9 s | **1.1 s** | 17× faster |
| `ser push --what-if`, whole project, SitecoreAI | 14.3 s | **1.1 s** | 13× faster |
| `ser validate`, whole project | 5.0 s | **0.13 s** | 38× faster |
| Full push, nothing to change, SitecoreAI | 3.9 s | **0.4 s** | 10× faster |
| Pull 1,000 items, SitecoreAI | 2.6 s | **1.2 s** | 2× faster |

And for bulk copies between environments, `content transfer` put 10,000 new items into a SitecoreAI environment in 7.6 s, ready to use ([more](#first-class-support-for-syncing-and-moving-content-between-environments)).

The speed comes from Tern itself: it is compiled to native code, so it starts in milliseconds, sends larger batches, reads in parallel and skips work when nothing has changed. Where the CM does the work, as when creating items, both tools go at the CM's pace.

### It's safer, and much more AI-friendly

Tern is designed for AI agents working on your Sitecore content alongside you. The agent proposes a change as a Plan, you review it, and Tern applies exactly that or nothing. Environments you mark read-only can't be written to at all.

- **Review, then apply.** Save any push, pull or diff as a Plan, read it, then apply exactly that. A Plan records what it expects each target item to be, so if someone changes one in the meantime, Tern stops instead of overwriting their work.
- **Protected environments stay protected.** An Endpoint without `allowWrite` is never written to, so a production environment can be read and compared but never pushed to.
- **No surprise deletes.** Destructive actions need `--yes` or a reviewed Plan, and Tern never prompts when there is no terminal, so an agent can't get stuck on a question or answer one for you.
- **Output an agent can read.** With `--json`, every command prints exactly one JSON document on stdout, and its exit codes are documented and stable ([CLI contract](docs/cli-contract.md)). An agent works from structured results, not by scraping console text.
- **An agent skill.** [skills/tern](skills/tern/SKILL.md) teaches an AI agent to read content, make a Plan, and apply only what was reviewed.
- **No telemetry, ever.**

### First class support for syncing and moving content between environments

The Sitecore CLI can compare two environments, but it moves content only between a CM and files. Tern also copies content straight from one environment to another, by module, by a content and media pair, or by one item path, without rewriting `sitecore.json` or logging in again on every run:

```bash
tern content diff -s prod -d uat -m site-a           # what differs, nothing written
tern content diff -s prod -d uat -m site-a --push    # make uat match prod
tern content sync -s prod -d local -m site-a         # the same, through Item files you keep
tern content transfer -s prod -d uat -m site-a       # a bulk copy through the Content Transfer API
```

| Use | When |
|---|---|
| `content diff --push` | Keeping an environment in step with another. It compares the two CMs and writes only what differs, and deletes what the source lacks. Nothing is written locally, so it is the fastest way to bring an environment that is nearly in step up to date. |
| `content sync` | The same, but through Item files: pull, review or commit them, then push (`--skip-push`, `--push-only`). Use it when you want the content on disk, or to copy into a CM a transfer can't write to. |
| `content transfer` | Copying a large tree that the target mostly lacks, between SitecoreAI environments. Sitecore moves it CM to CM in chunks, and the items can be used as soon as the command returns: 10,000 new items took 7.6 s, where a sync creating the same items took 16 minutes. It sends every item every time and never deletes, so it doesn't keep environments in step. The target keeps transferred files in Azure Blob Storage, so a local container can be a target only once you have set that up for it ([how, with Azurite](https://stockpick.nl/sitecoreai/sitecore-content-transfer-api-locally-with-docker/)). |

`content transfer` uses Sitecore's Content Transfer API, the replacement for packages, which the Sitecore CLI has no command for. See [docs/content.md](docs/content.md) for the options and measurements.

### It's built for CI and scripts

- **`--json` on every command.** stdout carries exactly one JSON document with a `schemaVersion`; progress and logs go to stderr.
- **Credentials from the environment.** `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_<ENDPOINT>_CLIENT_ID` and so on) log a pipeline in without writing a secret to disk.
- **Logins that behave.** Tern refreshes a token before it expires instead of after a failed call, and a refresh writes only `user.json`, never `sitecore.json`, so it leaves no git diff.

### It does more than the Sitecore CLI

- **Read content without serializing it:** `item get` and `item query` find items by path, template or field value.
- **Edit without an Administrator:** `item set` and `authoring query` go through the Authoring GraphQL API.
- **Run scripts:** `script run` runs a Sitecore PowerShell Extensions script and captures its output.
- **Everything else is built in.** Publishing, indexing, resource packages and the SitecoreAI `cloud` commands need no plugins to download.

### It fixes the Sitecore CLI's rough edges

Beyond the new commands, Tern smooths out the small things that trip you up day to day:

- **Pushes don't stop on a duplicate item.** When the CM has an item at the same path as a serialized one but with a different ID, the Sitecore CLI stops with "Non-unique paths cannot be serialized" and you delete it by hand. Tern deletes it and creates the serialized item in the same push.
- **Pull from a read-only environment.** An Endpoint without `allowWrite` can still be pulled to files; only writes to the CM are blocked.
- **`ser diff` works with borrowed logins.** Endpoints that share a login through `ref`, such as those from `cloud environment connect`, compare without an authentication error.
- **Hand-edited Item files still load.** A blank line an editor trimmed from a multi-line value no longer breaks the parse.
- **Sensible defaults.** `cloud environment log download` saves to the current folder when no output path is given.

Each difference from the Sitecore CLI is listed and tested ([deviations](docs/deviations.md)).

### It's lighter

- **One file.** Tern is a single 12.5 MB native executable with no runtime to install. Sitecore CLI 7 is 12 MB, plus 31 MB of plugins in every project's `.sitecore/package-cache`, plus the .NET 10 runtime (77 MB).
- **Less memory and CPU.** Most commands peak at 12–86 MB, against 30–155 MB. A 10,000-item push with nothing to change uses 0.23 s of CPU, against 1.8 s.

### It's a drop-in replacement

- **Same files.** Tern reads the same `sitecore.json` and `*.module.json` and writes Item files that are byte for byte the same, so your repository doesn't change.
- **Same commands and logins.** Commands, options, short flags and exit codes match the Sitecore CLI, and Tern uses the same `.sitecore/user.json` logins.
- **Switching is a find-and-replace.** Install `Tern.Cli` and replace `dotnet sitecore` with `tern` in your scripts; the arguments stay the same.
- **Proven side by side.** Pulls, pushes and resource packages are tested against the Sitecore CLI for identical results, and Tern has every one of its 83 commands ([testing](docs/testing.md)). Tern differs only where the Sitecore CLI fails, and each such case is listed and tested ([deviations](docs/deviations.md)).

## Install

### Quickly switch with an AI agent

Paste this prompt into your agent:

```text
Switch this repository from the Sitecore CLI to Tern. Follow the skill at
https://github.com/markgibbons25/tern/blob/main/skills/switch-to-tern/SKILL.md:
survey where the Sitecore CLI is installed and called, record a baseline,
install Tern.Cli, replace dotnet sitecore with tern, and prove nothing else changed.
Show me what you found before you change anything.
```

### Install manually

```bash
dotnet tool install --global Tern.Cli
```

Standalone binaries for Windows, Linux and macOS are attached to each release.

To switch a repository over, replace `dotnet sitecore` with `tern` in its scripts and pipelines, install Tern in CI the same way, and remove the Sitecore CLI from the tool manifest:

```bash
dotnet tool uninstall sitecore.cli
```

### Update

```bash
dotnet tool update --global Tern.Cli
tern --version
```

Add `--version <version>` to move to a particular release, and `--allow-downgrade` as well if it is older than the one installed. Where CI installs a pinned version, change the version there too.

### Start without a sitecore.json

If the repository has never used the Sitecore CLI, install Tern manually as above, then create the configuration from the repository root:

```bash
tern init
```

This writes a `sitecore.json` that picks up every `src/*/*.module.json`. Add a module to say which items to serialize (see Sitecore's [module file reference](https://doc.sitecore.com/sai/en/developers/sitecoreai/sitecore-content-serialization/set-up-content-serialization.html); Tern reads the same format). Then log in and pull, as below.

## Use

Run Tern from the folder that holds `sitecore.json`:

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
| Login | `init`, `login`, `connect`, `env list/current/set-default/info`, `cloud login/logout` |
| Serialization | `ser info`, `explain`, `validate`, `pull`, `push`, `diff`, `watch`, `package create/install`, `apply` |
| Content between environments | `content sync`, `content diff`, `content transfer` ([docs/content.md](docs/content.md)) |
| Items | `item get`, `item query`, `item set` |
| Jobs | `publish` (and `publish item`, `list-targets`), `index list/statistic/rebuild/schema-populate`, `dbcleanup` |
| Resource packages | `itemres create`, `itemres unicorn`, `itemres cleanup` |
| SitecoreAI | `cloud project/environment/deployment/editinghost/organization …` |
| Beyond Management Services | `item set` and `authoring query` (Authoring API, no Administrator needed), `script run` (SPE remoting) |

Run `tern <command> --help` for options. They match the Sitecore CLI's, so Sitecore's [CLI command reference](https://doc.sitecore.com/sai/en/developers/sitecoreai/sitecore-command-line-interface/sitecore-cli-command-reference/index.html) applies too.

## Performance

Compared with Sitecore CLI 7:

- **Work done on your machine** is 4–38 times faster: starting up, reading the configuration, validating Item files, building resource packages.
- **What-if pulls and pushes** of a whole project are 12–17 times faster.
- **Pushes with little or nothing to change** are 1.7–11 times faster.
- **Pulls** are 1.3–4.4 times faster.
- **Creating items** is 1.1–1.6 times faster. Here the CM sets the pace, at about 25 ms per item on a local container and 95 ms on SitecoreAI, whichever tool sends them.

Measured on 2026-10-22 with `bench/suite.sh both` ([testing](docs/testing.md#benchmarks)), which runs both tools in turn on the same content: a real project, and sets of up to 10,000 items that Sitecore created. Scenarios under 1,000 items are the median of 3 runs; the others are one run. Tern is the Native AOT build.

### Local instance

| Scenario | Sitecore CLI 7 | Tern | Speed-up |
|---|---|---|---|
| Start up (`--version`) | 0.10 s | **0.03 s** | 4.1× |
| `ser info` | 0.68 s | **0.05 s** | 14.5× |
| `ser validate` (whole project) | 4.97 s | **0.13 s** | 37.7× |
| `itemres create` (whole project) | 4.83 s | **0.34 s** | 14.2× |
| `ser pull --what-if` (whole project) | 13.13 s | **0.87 s** | 15.1× |
| `ser push --what-if` (whole project) | 17.30 s | **1.47 s** | 11.7× |
| Full pull, content only | 3.94 s | **2.15 s** | 1.8× |
| Full push in sync, content only | 3.97 s | **0.36 s** | 11.2× |
| Full pull, content + media | 7.24 s | **4.64 s** | 1.6× |
| Full push in sync, content + media | 2.92 s | **0.40 s** | 7.3× |
| Create 100 content items | 4.15 s | **2.69 s** | 1.5× |
| Create 100 media items | 3.88 s | **2.48 s** | 1.6× |
| Pull 100 media items | 1.28 s | **0.29 s** | 4.4× |
| Push 10 changes (1,000 items) | 1.43 s | **0.49 s** | 2.9× |
| Pull 1,000 content items | 3.92 s | **1.27 s** | 3.1× |
| Push 1,000 content items in sync | 1.15 s | **0.25 s** | 4.6× |
| Pull 10,000 content items | 32.39 s | **25.65 s** | 1.3× |
| Push 10,000 content items in sync | 3.68 s | **2.19 s** | 1.7× |
| `ser watch`: a CM edit reaches its Item file | 1.61 s | **1.59 s** | 1.0× |
| `ser watch`: CPU used while idle for 30 s | 0.03 s | **0.00 s** | – |

Both tools check the CM's change history every 2 s, so with edits made at random moments a change reaches its file in about the same time. Once Tern sees a change, it pulls just the changed items, in about 25 ms. While idle, it uses no measurable CPU and 36 MB of memory, against 111 MB.

### SitecoreAI cloud instance

The cloud project's media module holds fewer items than the local one (about 30 against 80), so its content + media figures differ little from content only.

| Scenario | Sitecore CLI 7 | Tern | Speed-up |
|---|---|---|---|
| `ser pull --what-if` (whole project) | 18.89 s | **1.09 s** | 17.4× |
| `ser push --what-if` (whole project) | 14.27 s | **1.11 s** | 12.8× |
| Full pull, content only | 3.74 s | **1.35 s** | 2.8× |
| Full push in sync, content only | 3.92 s | **0.40 s** | 9.9× |
| Full pull, content + media | 3.52 s | **1.32 s** | 2.7× |
| Full push in sync, content + media | 3.92 s | **0.41 s** | 9.6× |
| Create 100 content items | 10.77 s | **9.65 s** | 1.1× |
| Create 100 media items | 11.12 s | **9.84 s** | 1.1× |
| Pull 100 media items | 1.92 s | **1.02 s** | 1.9× |
| Push 10 changes (1,000 items) | 1.72 s | **0.74 s** | 2.3× |
| Pull 1,000 content items | 2.55 s | **1.16 s** | 2.2× |
| Push 1,000 content items in sync | 1.26 s | **0.43 s** | 3.0× |
| Pull 10,000 content items | 30.12 s | **23.12 s** | 1.3× |
| Push 10,000 content items in sync | 4.05 s | **1.08 s** | 3.8× |

### Beyond wall time

- **CPU.** Tern's own work is a fraction of the Sitecore CLI's: a 10,000-item push with nothing to change used 0.23 s of CPU, against 1.8 s.
- **Memory** (peak working set). For most commands, Tern uses 12–86 MB where Sitecore CLI 7 uses 30–155 MB. With media, a full pull peaks at 242 MB, against 392 MB.

The settings behind these numbers, and what changes them, are in [docs/settings.md](docs/settings.md). For copying content between environments, see [docs/content.md](docs/content.md#performance-sync-diff---push-and-transfer).

## Compatibility

- **Platforms.** Tern follows the Sitecore CLI line Sitecore currently supports: Management Services 5.1.25 and later, on SitecoreAI and XM/XP 10.1–10.4. It is tested on SitecoreAI and on local SitecoreAI containers; testing on on-prem XM 10.4 is still to come ([roadmap](docs/roadmap.md)).
- **Proven against the Sitecore CLI.** A milestone counts as finished only when its commands give the same results as the Sitecore CLI in the differential tests and are no slower on any benchmark.
- **Runs everywhere.** Native builds for Windows, Linux and macOS, on x64 and Arm64.

## Docs

- [Content sync, diff and transfer](docs/content.md)
- [CLI contract](docs/cli-contract.md)
- [Settings](docs/settings.md)
- [Deviations from the Sitecore CLI](docs/deviations.md)
- [Testing and benchmarks](docs/testing.md)
- [Roadmap](docs/roadmap.md)
- [Glossary](GLOSSARY.md)
- [Protocol notes](docs/protocol/)
- [Decisions](docs/adr/)
- [Agent skill: using Tern](skills/tern/SKILL.md)
- [Agent skill: switching a repository to Tern](skills/switch-to-tern/SKILL.md)

## License

MIT
