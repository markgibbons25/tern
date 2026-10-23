---
name: tern
description: Use Tern, the drop-in replacement for the Sitecore CLI, to pull, push, diff, query and publish Sitecore content from the shell. Use when a task touches Sitecore serialization (sitecore.json, *.module.json, .yml Item files), moves content between a CM and files or between environments, or reads items on a CM.
---

# Tern

`tern` takes the same commands, options, files and logins as the Sitecore CLI (`dotnet sitecore`). Every command also takes `--json`: stdout is then one JSON document, and progress goes to stderr.

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
       "local": { "host": "https://xmcloudcm.localhost", "allowWrite": true }
     }
   }
   ```

   No client ID, secret or token goes in `tern.json`; Tern refuses them. They go in `.tern/.env` as `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` lines, which the user fills in.
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

Then the jobs are `tern content diff`, `content sync` and `content transfer`, run as the table above says: preview first, and only against the target the user named.

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

## Logging in without prompts

Set `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` (or `TERN_CLIENT_ID` / `TERN_CLIENT_SECRET`). Tern uses them for that run only and never writes them to disk. Device login (`tern login`, `tern cloud login`) needs a person, so ask instead of running it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure; the message on stderr says why |
| 3 | A saved Plan no longer matches the target |
