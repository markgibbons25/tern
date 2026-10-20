# Content sync and diff

`tern content sync` and `tern content diff` move and compare content between two environments. They replace the `ContentSync.ps1` and `ContentDiff.ps1` scripts that projects kept beside a content-only `sitecore.json`, and they do it without rewriting `sitecore.json` or logging in again on every run.

Run them from the folder that holds that `sitecore.json` (or pass `-c`). `--source` and `--target` are Endpoint names, so set each environment up once:

```bash
tern login -n prod --authority https://auth.sitecorecloud.io --cm https://<prod-cm> --client-credentials true --client-id <id> --client-secret <secret> --allow-write false
tern login -n uat  --authority https://auth.sitecorecloud.io --cm https://<uat-cm>  --client-credentials true --client-id <id> --client-secret <secret> --allow-write true
```

To keep the secret off disk, leave out `--client-id` and `--client-secret` and set `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_PROD_CLIENT_ID` and so on) instead. `--allow-write false` is what keeps an environment such as prod from ever being pushed to.

## What to compare or copy

| Options | Covers |
|---|---|
| none | Every Module `sitecore.json` registers |
| `--module <name>` / `-m` | One Module, matched by file name, namespace, or the end of its file name. It need not be registered in `sitecore.json`: any `*.module.json` beside it can be named. When the name matches a content Module and its media twin (file names that differ only by `content` and `media`, such as `migration-content-hospitals` and `migration-media-hospitals`), both are used. Any other multiple match is refused as ambiguous. |
| `--path <item path>` / `-p` | One item path. `--scope` is `ItemAndDescendants` (the default), `ItemAndChildren`, `SingleItem` or `DescendantsOnly`; `--database` defaults to `master`. Item files go to `items/adhoc` beside `sitecore.json`. |
| `--skip-media` (or `--exclude-media`) | Leaves out media Modules: those whose includes all lie under `/sitecore/media library`. Not allowed with `--path`. |

Media Modules always go first, so content never arrives before the media it links to.

## `content sync`

Pulls from `--source` into the Item files, then pushes those files to `--target`.

```bash
tern content sync -s prod -d uat                         # every module
tern content sync -s prod -d uat -m hospitals             # hospitals content and media
tern content sync -s prod -d uat -m hospitals --skip-media
tern content sync -s prod -d local -p "/sitecore/content/Site/Home/page" --scope SingleItem
tern content sync -s prod --skip-push                     # pull only, review the files...
tern content sync -d uat --push-only                      # ...then push them
tern content sync -s prod -d uat -w                       # what would change, nothing written
```

- The pull makes each include's folder match the source exactly, so there is no need to delete `items/` first, and a later `--push-only` pushes what was pulled.
- Media and content are pushed separately. A pull or push that fails with a gateway timeout (502, 503, 504, 520, 522, 524) or a dropped connection is tried again, up to three times, ten seconds apart. Each attempt works out afresh what still differs, so a retry never repeats work.
- `--what-if` with both endpoints compares the two CMs directly, because a what-if pull writes no files for a push to read. It ignores the includes' push operations, so it can list updates that a `CreateOnly` rule would skip.
- A sync deletes what the target has under the synced paths and the source lacks, as `ser push` does. Keep `--path` and `--scope` as narrow as the job needs.

## `content diff`

Compares the two CMs directly. No Item files are read or written.

```bash
tern content diff -s prod -d uat
tern content diff -s prod -d uat -m hospitals --skip-media
tern content diff -s prod -d local -p "/sitecore/content/Site/Home/page"
tern content diff -s prod -d uat -m hospitals --push     # and apply the differences to uat
```

`--push` writes to the target, and is refused unless the target's Endpoint allows writes.

## From the scripts

| Script | Tern |
|---|---|
| `-Source prod -Target uat` | `-s prod -d uat` (Endpoint names) |
| `-Module hospitals` | `-m hospitals` |
| `-ItemPath <p>` | `-p <p>` |
| `-ItemOnly` / `-Children` | `--scope SingleItem` / `--scope ItemAndChildren` |
| `-SkipMedia` / `-ExcludeMedia` | `--skip-media` |
| `-SkipPush` / `-PushOnly` | `--skip-push` / `--push-only` |
| `-SkipClean` | Not needed: the pull only touches the selected includes' folders |
| `-ApplyPush` | `content diff --push` |
| `-Database` | `--database` |
| `-WhatIf` | `-w` |
| `-FixDuplicates` | Not needed: when the target has another item at a path the source uses, the Plan deletes it before creating the source's item, where the Sitecore CLI stops with "Non-unique paths cannot be serialized" |
| `-ClientId` / `-ClientSecret` / `.env` | Stored once by `tern login`, or `TERN_CLIENT_ID` / `TERN_CLIENT_SECRET` |
| Environment table and `AllowWrite` in the script | Endpoints in `.sitecore/user.json` and their `allowWrite` |

Both commands take `--json`: stdout is one document with the Modules used and a `steps` array holding each pull, push or diff's own document.
