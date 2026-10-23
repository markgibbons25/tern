# Settings

Tern's own settings live outside `sitecore.json` ([ADR 0011](adr/0011-tern-settings-live-outside-sitecore-json.md)). Each one can be set in the project's `tern.json`, in `~/.config/tern/settings.json`, or with its environment variable, which wins. The project's `tern.json` lives in the [`.tern` folder](#the-tern-folder) at the repository root; a `tern.json` beside `sitecore.json` is still read when there is no `.tern/tern.json`.

| Setting | Variable | Default | What it does |
|---|---|---|---|
| `aliasesPerRequest` | `TERN_ALIASES_PER_REQUEST` | 26 | Single-item reads packed into one request |
| `parallelRequests` | `TERN_PARALLEL_REQUESTS` | 8 | Read requests in flight at once, shared by every Include in the run |
| `writeBatchSize` | `TERN_WRITE_BATCH_SIZE` | 1000 | Write commands per batch; a batch also ends at about 8 MB of data |
| `splitThreshold` | `TERN_SPLIT_THRESHOLD` | 200 on a remote CM, 0 on a local one | Subtrees of at least this many items are read as several parallel requests; 0 never splits |
| `fileIndex` | `TERN_FILE_INDEX` | `true` | Keep an index of Item files so unchanged files aren't read again |
| `useKeychain` | `TERN_USE_KEYCHAIN` | `false` | Keep tokens in the OS keychain |

## Shared endpoints

The project's `tern.json` can also list the team's endpoints, so nobody has to add them by hand ([ADR 0013](adr/0013-shared-endpoints-in-tern-json.md)):

```json
{
  "endpoints": {
    "dev":  { "host": "https://<dev-cm>",  "allowWrite": true,  "useClientCredentials": true },
    "prod": { "host": "https://<prod-cm>", "allowWrite": false, "useClientCredentials": true }
  }
}
```

Each endpoint takes `host` (required), `authority`, `audience`, `allowWrite` and `useClientCredentials`, and nothing else: a client ID, secret or token is refused, since the file is committed. Authority and audience default to SitecoreAI's. Credentials come from `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET`, as variables or in a gitignored `.tern/.env` (or a `.env` beside `sitecore.json`), or from `tern login`.

`tern.json` wins over `user.json` for where an endpoint is, `user.json` keeps the tokens, and `allowWrite` is the stricter of the two. `tern env list` marks these endpoints `(tern.json)`.

## The `.tern` folder

Tern keeps its project files in a committed `.tern` folder at the repository root ([ADR 0014](adr/0014-the-tern-folder-at-the-repository-root.md)). Tern finds it from any folder in the repository, looking up no further than the folder with `.git`, so content can be moved between environments from anywhere:

```text
.tern/
  tern.json                       settings and shared endpoints (committed)
  content-site-a.module.json      Modules for the content commands (committed)
  media-site-a.module.json
  .env                            TERN_* credentials (gitignored)
  items/                          Item files the content commands pull (usually gitignored)
```

- **`tern.json`** holds the settings above and the [shared endpoints](#shared-endpoints).
- **`.env`** is read before a `.env` beside `sitecore.json`; a variable that is set wins over both.
- **Modules** in `.tern` are the content commands' own. Once there is one, `content sync`, `content diff` and `content transfer` use the `.tern` Modules and no longer `sitecore.json`'s, and `--module` names any one of them. Without `--module` they take every `.tern` Module, or only those that a `"modules"` list of globs in `.tern/tern.json` names, such as `["migration-media.module.json", "migration-content.module.json"]`, so a Module for a one-off job (templates, roles) runs only when named. Their Item files go to `.tern/items/<include>`, and a `--path` run's to `.tern/items/adhoc`. They take `sitecore.json`'s excluded fields and path length, when there is one, but never remove roles or users the source lacks, whatever its `removeOrphansForRoles` and `removeOrphansForUsers` say.
- **Serialization never touches them.** `ser` commands leave the `.tern` Modules out even where a glob such as `**/*.module.json` reaches them (with a warning to narrow it), and `ser pull`, `ser push` and `ser watch` refuse to run inside `.tern`, with a message naming the folder of `sitecore.json` to change to. The Sitecore CLI doesn't know `.tern`, so a broad glob does reach the folder for `dotnet sitecore`.
- **Logins** stay in `.sitecore/user.json` beside `sitecore.json`. Run from a folder with no `sitecore.json` above it, Tern uses `.sitecore/user.json` at the repository root.

## Why the defaults are what they are

Measured on 2026-10-20 against a local SitecoreAI container and a SitecoreAI CM used only for testing, with the scenarios of the benchmark suite. In both places the CM, not the client, sets the pace, so most settings change little:

- **`parallelRequests`.** Four is too few on SitecoreAI (a 1,000-item pull took 0.99 s, against 0.80 s at eight). Beyond eight, nothing changes on either CM.
- **`aliasesPerRequest`.** One item per request is 2.6 times slower than 26. From 10 to 100 the difference is noise.
- **`writeBatchSize`.** Each batch costs the server a fixed reset (template engine, index queue, GraphQL schemas), so fewer batches are faster: creating 1,000 items locally took 15.0 s in batches of 100 and 11.3 s in one batch. Batches always go one at a time, because the server fails when two run at once ([protocol notes](protocol/management-services.md)).
- **`splitThreshold`.** One subtree request makes one server thread compute every Signature in it. SitecoreAI reads the same subtree about 10% faster as parallel requests. A local container gains nothing, so a CM on `localhost`, `*.localhost` or a loopback address gets one request per subtree. The pieces merge into exactly the order one request gives, so the Plan is the same either way.
- **`fileIndex`.** Opening a file costs more than parsing it, on Windows especially. With the index, a push of 10,000 unchanged items used 0.27 s of CPU instead of 3.4 s, and `ser validate` of the same files took 0.14 s instead of 1.65 s.

HTTP/2 was tried too and made no measurable difference on either CM, so Tern stays on HTTP/1.1.

## The file index

Tern records each Item file's header, Signature, size and last write time, and reads a file again only when its size or write time changes. The index lives outside the project, under `%LOCALAPPDATA%\Tern\file-index` (`~/.local/share/Tern/file-index` elsewhere), one file per folder and set of excluded fields, so nothing appears in a repository. It is only a cache: a missing, damaged or stale index means files are read, never that a change is missed. The Official CLI's `.scindex` does the same job but can't be shared, because its Signatures don't record which fields were excluded.
