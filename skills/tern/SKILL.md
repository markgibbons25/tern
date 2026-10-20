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

## Reading

| Task | Command |
|---|---|
| What modules and includes exist | `tern ser info --json` |
| Is a path serialized, and where | `tern ser explain -p /sitecore/content/Home` |
| Are the Item files consistent | `tern ser validate` (add `--fix` only when asked) |
| One item with all fields | `tern item get /sitecore/content/Home --format json` |
| Find items | `tern item query /sitecore/content/Site --template <id> --field "Title=*News*" --show Title --json` |
| What differs between two Endpoints | `tern ser diff -s dev -d uat -i <module> --json` |
| CM version and features | `tern env info` |

## Logging in without prompts

Set `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` (or `TERN_CLIENT_ID` / `TERN_CLIENT_SECRET`). Tern uses them for that run only and never writes them to disk. Device login (`tern login`, `tern cloud login`) needs a person, so ask instead of running it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure; the message on stderr says why |
| 3 | A saved Plan no longer matches the target |
