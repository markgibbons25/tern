# Content sync, diff and transfer

`tern content sync`, `tern content diff` and `tern content transfer` copy and compare content between two environments. Sync and diff do the job that content-sync scripts built around `ser pull`, `ser push` and `ser diff` usually do, without rewriting `sitecore.json` or logging in again on every run ([replacing such a script](#replacing-a-content-sync-script)).

Run them from the folder that holds that `sitecore.json` (or pass `-c`). `--source` and `--target` are Endpoint names, so set each environment up once:

```bash
tern login -n prod --authority https://auth.sitecorecloud.io --cm https://<prod-cm> --client-credentials true --client-id <id> --client-secret <secret> --allow-write false
tern login -n uat  --authority https://auth.sitecorecloud.io --cm https://<uat-cm>  --client-credentials true --client-id <id> --client-secret <secret> --allow-write true
```

To keep the secret off disk, leave out `--client-id` and `--client-secret` and set `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_PROD_CLIENT_ID` and so on) instead, as environment variables or in a `.env` beside `sitecore.json`. Tern reads only the `TERN_*` lines of that file, and a variable that is set wins. `--allow-write false` is what keeps an environment such as prod from ever being pushed to.

For a team, list the environments in a committed `tern.json` beside `sitecore.json` instead, so nobody logs in to each one: hosts and `allowWrite` go in `tern.json`, the client credentials in a gitignored `.env`, and a committed `"allowWrite": false` on prod can't be overridden locally ([shared endpoints](settings.md#shared-endpoints)).

## What to compare or copy

| Options | Covers |
|---|---|
| none | Every Module `sitecore.json` registers |
| `--module <name>` / `-m` | One Module, matched by file name, namespace, or the end of its file name. It need not be registered in `sitecore.json`: any `*.module.json` beside it can be named. When the name matches a content Module and its media twin (file names that differ only by `content` and `media`, such as `content-site-a` and `media-site-a`), both are used. Any other multiple match is refused as ambiguous. |
| `--path <item path>` / `-p` | One item path. `--scope` is `ItemAndDescendants` (the default), `ItemAndChildren`, `SingleItem` or `DescendantsOnly`; `--database` defaults to `master`. Item files go to `items/adhoc` beside `sitecore.json`. |
| `--skip-media` (or `--exclude-media`) | Leaves out media Modules: those whose includes all lie under `/sitecore/media library`. Not allowed with `--path`. |

Media Modules always go first, so content never arrives before the media it links to.

## `content sync`

Pulls from `--source` into the Item files, then pushes those files to `--target`.

```bash
tern content sync -s prod -d uat                         # every module
tern content sync -s prod -d uat -m site-a                # site A's content and media modules
tern content sync -s prod -d uat -m site-a --skip-media
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
tern content diff -s prod -d uat -m site-a --skip-media
tern content diff -s prod -d local -p "/sitecore/content/Site/Home/page"
tern content diff -s prod -d uat -m site-a --push        # and apply the differences to uat
```

`--push` writes to the target, and is refused unless the target's Endpoint allows writes.

## `content transfer`

Copies content between two SitecoreAI environments with Sitecore's [Content Transfer API](https://api-docs.sitecore.com/sai/content-transfer-api), then loads it with the [Item Transfer API](https://api-docs.sitecore.com/sai/item-transfer-api). This is the replacement for the Package Designer. The data goes from CM to CM in chunks, and nothing is written locally. The Sitecore CLI has no command for these APIs.

```bash
tern content transfer -s dev -d uat -m site-a                    # a content module and its media twin
tern content transfer -s dev -d uat -p "/sitecore/content/Site/Home/page" --scope SingleItem
tern content transfer -s dev -d uat -m content -w                 # list the data trees only
tern content transfer -s dev -d uat -p /sitecore/content/Site/Data --merge OverrideExistingTree --yes
```

- **What goes.** The API takes an item on its own or an item with all its descendants. Tern turns each include into the fewest such data trees that carry exactly the items it serializes: a `DescendantsOnly` include becomes one tree per child, an `Ignored` rule splits the tree around it, and a `CreateOnly` rule becomes `KeepExistingItem`.
- **`--merge`.** `OverrideExistingItem` (the default) replaces items the target has, `KeepExistingItem` leaves them, and `OverrideExistingTree` deletes each transferred tree in the target before writing, so it needs `--yes`. An item that goes on its own never uses `OverrideExistingTree`, which would also delete its descendants that the include leaves out. No strategy deletes anything outside the transferred trees, so unlike `content sync`, a transfer leaves items the source lacks where they are.
- **Steps.** Tern creates the transfer on the source, waits until it is ready, copies the chunks (in parallel, with retries), turns each chunk set into a `.raif` file on the target, and deletes the transfer from the source. It then starts loading each file into the target database and returns as soon as the target reports the file `Consumed`. At that point every transferred item can be read and edited there, which is what Sitecore's docs mean by "immediately available". `--no-consume` stops after the copy and leaves the files on the target.
- **`--wait`.** The target goes on writing the items to its database in the background: about 40–50 s queued, then roughly 0.2 s per item. If that fails, items not yet written become unavailable again and the transfer has to be run again. `--wait` waits for the load to finish (`Finished`), reports how many items were written, and deletes the file. Use it where a pipeline must know the content is in the database. Without it, each transfer deletes the files of earlier transfers whose loads have finished, as Sitecore's docs advise.
- **Parents.** An item whose parent is missing in the target transfers but does not appear in the content tree. Tern warns about each one, checking alongside the transfer so a busy target doesn't hold it up.
- **Access.** Both APIs need a login from an automation client of an Organization Admin or Owner, or an equivalent user login, such as the endpoints `cloud environment connect` makes.
- **Local containers.** A local CM can always be the source. It can be the target only once it has blob storage: the target keeps transferred files in Azure Blob Storage, which a local container doesn't have unless you set it up, and without it the first chunk fails with "The Azure Blob Storage container name is invalid". [Sitecore Content Transfer API locally with Docker](https://stockpick.nl/sitecoreai/sitecore-content-transfer-api-locally-with-docker/) sets it up with Azurite: an `azurite` service, the `Sitecore.TransferredItems.AzureBlobStorage.ConnectionString` and `ContainerName` settings on the CM, and a SAS token with full permissions. Otherwise, use `content sync` or `content diff --push` to copy into a local CM.

What was seen on SitecoreAI on 2026-10-21, copying a content module of 1,478 items (97 data trees) between two environments:

- The file was `Consumed`, and every item readable, within about 1.3 s of the load starting. A `content diff` run straight afterwards found all 1,478 items matching the source. The background load then took about 5 minutes to reach `Finished` (1,478 of 1,478 written), and a second diff still matched.
- The load's state goes `Unknown`, then `InProgress`, then `Finished`. Its ID (`consumed.<time>.<guid>`) appears only in the list of transfers. The `location` header of the call that starts the load names the file, as the API reference says, not the load as the walkthrough says.
- For about 10 s after a load starts, the target answers slowly while it makes the items available. A transfer run straight after another one waits for that, so back-to-back module transfers took 12–14 s each, against about 2 s when spaced out.
- Background loads queue on the target and run one at a time. Dozens of back-to-back transfers of a large module queue hours of work, and while it runs the target's reads fail now and then (`GraphQL error: Error trying to resolve …`). The API has no way to cancel a load, but deleting its file drops it from the queue. Tern warns when the target was still writing earlier transfers as a new one started.
- The listing endpoints take at most 50 per page.
- After one transfer, the target's load had filled in empty `DynamicPlaceholderId` values in two layouts, without changing their revision. That did not happen again in later transfers. `content diff --push` makes such items identical to the source.
- Sitecore's Marketplace SDK starts a load through an older endpoint, `items/v2/ConsumeFile?fileName=blob://<file>` on Sitecore's Edge platform, which is `/sitecore/shell/api/v2/ItemsTransfer/ConsumeFile` on the CM. It behaves the same as the documented v3 call that Tern uses.

## Performance: sync, diff --push and transfer

Measured on 2026-10-21 with `bench/content-bench.sh`, copying a content module (1,478 items) and a media module (80 items) between two SitecoreAI environments in the same region, from one Windows machine. Tern is the Native AOT build, and every figure is a median. Sync and diff ran 3 times. A plain transfer ran 3 times, 20 s apart (`GAP=20`), since back-to-back transfers wait for each other. `--wait` ran twice. "Content sync" starts with no Item files, as a script that clears its items folder first would. "In sync" means the target already matched the source.

| Scenario | `content sync` | `content diff --push` | `content transfer` | `content transfer --wait` |
|---|---|---|---|---|
| 1 item, in sync | 0.14 s | 0.09 s | 1.62 s | 59.4 s |
| 1 item, 1 change | 0.32 s | 0.28 s | 1.70 s | 48.2 s |
| Content module (1,478 items), in sync | 2.44 s (0.68 s with the files kept) | 0.43 s | 2.25 s | 306.5 s |
| Content module, 10 changes | 2.23 s | 0.78 s | 2.16 s | 323.7 s |
| Media module (80 items), in sync | 2.68 s | 0.16 s | 8.08 s | 81.8 s |
| Content + media (1,558 items), in sync | 3.47 s | 0.45 s | 9.60 s | 340.6 s |

Peak memory: sync 24–312 MB (the media blobs pass through it), diff 24–45 MB, transfer 25–107 MB. CPU time is under 1.2 s for every run.

Where a plain transfer's time goes, as medians:

| Scenario | Prepare (source) | Copy chunks | Until consumed (target) |
|---|---|---|---|
| 1 item | 0.1 s | 0.2 s | 1.3 s |
| Content module | 0.1 s | 0.3–0.4 s (5 chunks) | 1.3–1.4 s |
| Media module | 0.1 s | 6.5 s (1 chunk) | 1.3 s |
| Content + media | 0.1 s | 6.7 s (6 chunks) | 2.4 s |

What the numbers mean:

- **Sync and diff send only what differs. A transfer sends every item, every time.** With 10 changes, sync and diff wrote 10 items and the transfer wrote 1,478. A transfer is still quick, because the target takes a whole `.raif` file at once.
- **For content, a transfer is about as fast as a sync.** It took 2.2 s for the module against 2.4 s for a sync from scratch, and `content diff --push` was fastest at 0.4–0.8 s. With media, the transfer's copy takes about 6.5 s, which is slower than sync (3.5 s) or diff (0.5 s).
- **`--wait` is what costs minutes.** Nearly all of it is the target writing the items to its database in the background, about 0.2 s per item after a 40–50 s queue. Nothing in Tern or on this machine is slow: the same transfer driven with plain HTTP calls took the same time.
- **Choosing between them.** Use `content diff --push` or `content sync` to keep environments in step: they send only the differences, delete what the source lacks, and leave no background work. Use `content transfer` where Management Services or an Administrator login isn't available, for a one-off copy of a large tree, or where you want Sitecore's supported replacement for packages.

## Replacing a content-sync script

| What such a script does | Tern |
|---|---|
| Keeps a table of environments, with each CM's host and whether it may be written to | Endpoints in `.sitecore/user.json` (`tern login -n <name>`) and their `allowWrite` |
| Logs in with client credentials on every run, read from parameters or a `.env` file | `tern login` once, or `TERN_CLIENT_ID` / `TERN_CLIENT_SECRET`, which can stay in the same `.env` |
| Rewrites the `modules` list in `sitecore.json` to pick a module, then restores it | `-m <module>` |
| Writes a temporary module for one item path | `-p <path>` with `--scope` |
| Drops the media modules | `--skip-media` |
| Pulls only, so the files can be reviewed, then pushes later | `--skip-push`, then `--push-only` |
| Deletes the items folder before each pull | Not needed: the pull makes each selected include's folder match the source |
| Pushes media and content separately and retries on gateway timeouts | Built in |
| Deletes a target item that blocks a push with "Non-unique paths cannot be serialized", then pushes again | Not needed: the Plan deletes the other item at that path before creating the source's |
| Runs `ser diff --push` between two logins | `content diff --push` |

All three commands take `--json`. For sync and diff, stdout is one document with the Modules used and a `steps` array holding each pull, push or diff's own document; for transfer, it lists the data trees and each transfer's files and timings.
