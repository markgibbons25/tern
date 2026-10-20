# Settings

Tern's own settings live outside `sitecore.json` ([ADR 0011](adr/0011-tern-settings-live-outside-sitecore-json.md)). Each one can be set in `tern.json` next to `sitecore.json`, in `~/.config/tern/settings.json`, or with its environment variable, which wins.

| Setting | Variable | Default | What it does |
|---|---|---|---|
| `aliasesPerRequest` | `TERN_ALIASES_PER_REQUEST` | 26 | Single-item reads packed into one request |
| `parallelRequests` | `TERN_PARALLEL_REQUESTS` | 8 | Read requests in flight at once, shared by every Include in the run |
| `writeBatchSize` | `TERN_WRITE_BATCH_SIZE` | 1000 | Write commands per batch; a batch also ends at about 8 MB of data |
| `splitThreshold` | `TERN_SPLIT_THRESHOLD` | 200 on a remote CM, 0 on a local one | Subtrees of at least this many items are read as several parallel requests; 0 never splits |
| `fileIndex` | `TERN_FILE_INDEX` | `true` | Keep an index of Item files so unchanged files aren't read again |
| `useKeychain` | `TERN_USE_KEYCHAIN` | `false` | Keep tokens in the OS keychain |

## Why the defaults are what they are

Measured on 2026-10-20 against a local SitecoreAI container and a SitecoreAI CM (`dev-mark`), with the scenarios of the benchmark suite. In both places the CM, not the client, sets the pace, so most settings change little:

- **`parallelRequests`.** Four is too few on SitecoreAI (a 1,000-item pull took 0.99 s, against 0.80 s at eight). Beyond eight, nothing changes on either CM.
- **`aliasesPerRequest`.** One item per request is 2.6 times slower than 26. From 10 to 100 the difference is noise.
- **`writeBatchSize`.** Each batch costs the server a fixed reset (template engine, index queue, GraphQL schemas), so fewer batches are faster: creating 1,000 items locally took 15.0 s in batches of 100 and 11.3 s in one batch. Batches always go one at a time, because the server fails when two run at once ([protocol notes](protocol/management-services.md)).
- **`splitThreshold`.** One subtree request makes one server thread compute every Signature in it. SitecoreAI reads the same subtree about 10% faster as parallel requests. A local container gains nothing, so a CM on `localhost`, `*.localhost` or a loopback address gets one request per subtree. The pieces merge into exactly the order one request gives, so the Plan is the same either way.
- **`fileIndex`.** Opening a file costs more than parsing it, on Windows especially. With the index, a push of 10,000 unchanged items used 0.27 s of CPU instead of 3.4 s, and `ser validate` of the same files took 0.14 s instead of 1.65 s.

HTTP/2 was tried too and made no measurable difference on either CM, so Tern stays on HTTP/1.1.

## The file index

Tern records each Item file's header, Signature, size and last write time, and reads a file again only when its size or write time changes. The index lives outside the project, under `%LOCALAPPDATA%\Tern\file-index` (`~/.local/share/Tern/file-index` elsewhere), one file per folder and set of excluded fields, so nothing appears in a repository. It is only a cache: a missing, damaged or stale index means files are read, never that a change is missed. The Official CLI's `.scindex` does the same job but can't be shared, because its Signatures don't record which fields were excluded.
