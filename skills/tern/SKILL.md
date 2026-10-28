---
name: tern
description: Use Tern, the drop-in replacement for the Sitecore CLI, to pull, push, diff, query and publish Sitecore content from the shell. Use when a task touches Sitecore serialization (sitecore.json, *.module.json, .yml Item files), moves content between a CM and files or between environments, or reads items on a CM.
license: MIT
---

# Tern

`tern` takes the same commands, options, files and logins as the Sitecore CLI (`dotnet sitecore`). Every command also takes `--json`: stdout is then one JSON document, and progress goes to stderr.

If `tern --version` fails, Tern isn't installed. It is the `Tern.Cli` .NET tool (`dotnet tool install --global Tern.Cli`; standalone binaries are on the [releases](https://github.com/markgibbons25/tern/releases)). Ask before installing it. Where the repository still uses the Sitecore CLI and the user wants to move to Tern, follow the `switch-to-tern` skill instead.

## The safe way to change a CM or Item files

Never write first. Make a Plan, read it, then apply exactly that Plan.

```bash
tern ser push --plan-out push.json --json    # or: ser pull / ser diff -s dev -d uat
tern apply push.json                          # refuses if anything changed since the Plan
```

- Read `plan.summary` and every `delete` operation before applying. Delete removes items (to the recycle bin on a CM) or files.
- `--what-if` (`-w`) shows the changes without saving a Plan.
- Exit code 3 means a saved Plan is out of date. Make a new one; never force it.
- Push or apply only to the Endpoint the user named. `tern env list` shows Endpoints; a read-only one is marked.
- `ser diff --push` writes without a Plan; use `ser diff --plan-out` and `tern apply` instead.

## Commands that write without a Plan

These change the target as soon as they run and can't save a Plan. Run one only when the user asked for that change, against the Endpoint they named. Show the preview first, then get the user's go-ahead before running it for real.

| Command | What it writes | Preview |
|---|---|---|
| `content diff -s <src> -d <dst> --push` | Makes the target match the source, including deleting items the source lacks | The same command without `--push` |
| `content sync -s <src> -d <dst>` | Pulls Item files from the source, then pushes them to the target, deletes included | `-w`; or `--skip-push`, review the files, then `--push-only` |
| `content transfer -s <src> -d <dst>` | Copies whole trees through the Content Transfer API; never deletes, unless `--merge OverrideExistingTree` (which needs `--yes`: ask the user, never add it yourself) | `-w` |
| `item set <path> --field "Title=New"` | Changes fields on one item version; `--reset` reverts fields to standard values | `tern item get <path>` for the current values |
| `authoring query` | Whatever the GraphQL document does; a `mutation` writes | Read the document; run only queries without asking |
| `script run` | Whatever the PowerShell script does, on the CM | Read the script and tell the user what it does |
| `publish`, `publish item` | Publishes to the live site | None; confirm the path, languages and targets |
| `index rebuild`, `index schema-populate`, `dbcleanup` | Rebuilds search indexes or cleans databases; `dbcleanup` can delete orphan items | `tern index list`, `tern index statistic`; confirm the indexes or tasks |
| `ser push`, `ser pull` without `--plan-out` | The same as their Plans, unreviewed | Use the Plan workflow above |

An Endpoint without `allowWrite` refuses all of these, and `authoring query` refuses only a mutation, so reads still work. A refusal means the user marked the Endpoint read-only: report it, and never change the setting.

`ser pull`, `ser push` and `ser watch` also refuse to run inside the repository's `.tern` folder. The message names the folder of `sitecore.json` to change to: run the command from there. To move content, use the `content` commands, which work from any folder in the repository.

## Setting up a content migration

When the user wants to copy content between environments (prod to uat, prod to local and so on), or has a content-sync script built on `ser pull` and `ser push`, the configuration goes in a `.tern` folder at the repository root, and is committed. Tern finds it from any folder in the repository, so the migration runs from anywhere, and its Modules are never part of `ser pull` or `ser push`, so a pulled copy of prod's content can't be pushed by mistake as if it were the project's items.

```text
.tern/
  tern.json                       endpoints and settings
  content-site-a.module.json      what the content commands copy
  media-site-a.module.json
  .env                            TERN_* credentials, gitignored
  items/                          Item files a content sync pulls, gitignored unless the team reviews them in git
```

1. **Ask** which environments are involved, their CM hosts, which ones are production, and which content (sites, item paths) moves. Read hosts from existing scripts, an existing `tern.json` or `tern env list`; never copy anything but endpoint names and hosts out of `.sitecore/user.json`.
2. **Write `.tern/tern.json`** with the environments. If the repository has a `tern.json` beside `sitecore.json`, move its contents here and delete it, since `.tern/tern.json` replaces it. Production gets `"allowWrite": false`; ask before marking anything writable you aren't sure is non-production. Endpoints reached with client credentials get `"useClientCredentials": true`:

   ```json
   {
     "endpoints": {
       "prod":  { "host": "https://<prod-cm>", "allowWrite": false, "useClientCredentials": true },
       "uat":   { "host": "https://<uat-cm>",  "allowWrite": true,  "useClientCredentials": true },
       "local": { "host": "https://xmcloudcm.localhost", "allowWrite": true },
       "xp-uat": { "host": "https://<xp-uat-cm>", "authority": "https://<identity-server>", "allowWrite": true, "useClientCredentials": true }
     }
   }
   ```

   `authority` defaults to SitecoreAI's, so every XM or XP endpoint needs it set to the customer's Identity Server; ask for its URL. No client ID, secret or token goes in `tern.json`; Tern refuses them. They go in `.tern/.env` as `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` lines, which the user fills in.
3. **Write one Module per site and kind of content** in `.tern`, named so the content and media Modules of a site differ only by `content` and `media` (`content-site-a.module.json`, `media-site-a.module.json`); then `-m site-a` takes both, media first. For example:

   ```json
   {
     "namespace": "content-site-a",
     "items": { "includes": [ { "name": "site-a", "path": "/sitecore/content/Tenant/Site A", "scope": "ItemAndDescendants" } ] }
   }
   ```

   Their Item files go to `.tern/items/<include name>`. Never copy the project's serialization Modules here: their includes cover templates and renderings, which a content copy must not touch. Once `.tern` has a Module, the content commands use only the `.tern` Modules, so every site to be copied needs one here. Without `-m`, every `.tern` Module runs; where some are for one-off jobs (templates, roles, test pages), list the everyday ones in `tern.json` as `"modules": ["migration-media.module.json", "migration-content.module.json"]` so the rest run only when named with `-m`.
4. **Gitignore** `.tern/.env` and `.tern/items/` (ask whether the team wants pulled content in git; if so, leave `items/` tracked), and `.sitecore/` if it isn't already.
5. **Check the project's `sitecore.json`**: if its `modules` globs reach `.tern` (such as `**/*.module.json`), Tern leaves those Modules out with a warning, but the Sitecore CLI doesn't, so narrow the glob (for example to `src/**/*.module.json`).
6. **Check**, from a folder other than the repository root: `tern env list` shows each endpoint marked `(tern.json)` with production `[read-only]`; `tern content diff -s <source> -d <target> -m <site> --json` names the `.tern` Modules in its scope and writes nothing; and `tern ser info` from the serialization project lists none of the `.tern` Modules.

Then the jobs are `tern content diff`, `content sync` and `content transfer`, run as the table above says: preview first, and only against the target the user named. The next section says which one fits.

## Choosing how to move content

| The job | Use | Why |
|---|---|---|
| Project items (templates, renderings, settings) from one CM to another | `ser diff -s <src> -d <dst> --plan-out p.json`, then `tern apply p.json` | The only route with a reviewable Plan; it covers `sitecore.json`'s Modules, not `.tern`'s |
| Keep an environment in step with another (prod to uat, uat to local) | `content diff -s <src> -d <dst>`, then the same with `--push` | Sends only the differences, CM to CM, and deletes what the source lacks; fastest (under a second for 1,500 items) |
| The same, but someone reviews the content in files or git first | `content sync --skip-push`, review `.tern/items`, then `content sync --push-only` | The same result as `diff --push`, with Item files in between |
| A one-off copy of a large tree, add-only, or where Sitecore Management Services isn't installed on a CM | `content transfer` | Sitecore's supported replacement for packages; never deletes items the source lacks (unless `OverrideExistingTree`) |
| One page or item | Any of the content commands with `-p <path> --scope SingleItem` | Keep the scope as narrow as the job |

Things that decide it:

- **Deletes.** `content diff --push` and `content sync` make the target match the source under the chosen paths, so items created only on the target are deleted. If the target has content of its own there (authors working on uat), use `content transfer`, which only adds and overwrites, or narrow `-p`.
- **No Plan.** The content commands can't save a Plan, so the preview and the write are separate runs: anything that changes on either CM between them is applied unreviewed. Preview, show the user `plan.summary` and every delete, and run the write straight after their go-ahead. Where a Plan matters for one item path, `ser diff -s <src> -d <dst> -p <path> --plan-out p.json` (from the folder of `sitecore.json`) saves one for `tern apply`.
- **Transfer sends everything.** Every item goes every time, so for a small change on a big tree `content diff --push` is lighter. Media is slower by transfer (about 6.5 s for 80 items against 0.5 s by diff).
- **Transfer finishes in the background.** It returns once the items are readable on the target (seconds), but the target goes on writing them to its database for minutes (about 0.2 s per item after a 40–50 s queue). Add `--wait` only where a pipeline must know the load has finished. Don't run transfers back to back in a loop: loads queue on the target one at a time, and its reads fail now and then while they run.
- **Transfer needs** an Organization Admin or Owner automation client (or an equivalent user login) on both sides, and a local container can be the target only with blob storage set up. To copy into a local CM, use `content diff --push` or `content sync`.
- **Check the result** with `content diff -s <src> -d <dst> --json`: `plan.summary` should show no changes. After a transfer, items whose parent the target lacks are copied but don't appear in the tree; Tern warns about each one.

## Starting a new project

With no `sitecore.json` yet, `tern init` creates one in the current folder that picks up every `src/*/*.module.json`. Ask which items the user wants serialized, write a Module file for them, then check it with `tern ser info`.

## Reading

| Task | Command |
|---|---|
| What modules and includes exist | `tern ser info --json` |
| Is a path serialized, and where | `tern ser explain -p /sitecore/content/Home` |
| Are the Item files consistent | `tern ser validate` (add `--fix` only when asked) |
| One item with all fields | `tern item get /sitecore/content/Home --format json` |
| Find items | `tern item query /sitecore/content/Site --template <id> --field "Title=*News*" --show Title --json` |
| What differs between two Endpoints | `tern ser diff -s dev -d uat -i <module> --json` |
| The same for a module not in sitecore.json, or one item path | `tern content diff -s prod -d uat -m <module> --json`, `-p <item path>` |
| What a Content Transfer API copy would carry | `tern content transfer -s dev -d uat -m <module> -w --json` |
| CM version and features | `tern env info` |

## Reading output without wasting context

`--json` is the cheaper output only once it is parsed. It carries every field of every operation, so read whole it is several times longer than the text output: a 168-item `content diff` printed 78,000 characters of JSON against 22,000 of text. Parse it in the shell and print only the answer:

- Totals: `plan.summary` (for the `content` commands, `steps[].plan.summary`).
- A check: compare the operations with what you expect, such as each operation's `include` against a Module's include names, and print only the mismatches.

For a quick look, the last lines of the text output carry the totals. Neither output says which fields an `update` changes: an operation carries only the item's source and expected signatures, so read the item itself (`tern item get`) when that matters.

## Logging in without prompts

Set `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` (or `TERN_CLIENT_ID` / `TERN_CLIENT_SECRET`). Tern uses them for that run only and never writes them to disk. Device login (`tern login`, `tern cloud login`) needs a person, so ask instead of running it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure; the message on stderr says why |
| 3 | A saved Plan no longer matches the target |
