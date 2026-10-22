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
