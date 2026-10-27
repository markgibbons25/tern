# Deviations

Tern produces the same result as the Official CLI wherever the Official CLI succeeds, and differs only where the Official CLI fails ([ADR 0007](adr/0007-deviations-only-where-the-official-cli-fails.md)). Each Deviation below is backed by a test.

| # | Deviation | Official CLI | Tern | Test |
|---|---|---|---|---|
| D1 | A `\|` block value whose blank line was trimmed by an editor | Ends the block early, then fails to parse the next line | Reads the blank line as part of the block, as long as the block plainly continues after it | `ItemFileFormatTests.Deviation_D1_…` |
| D2 | Pull from an Endpoint without `allowWrite` | Refuses the pull, even though it only writes files | Pulls; `allowWrite` still guards every write to a CM | `PushRunner` refuses writes; see `SyncRunner.PullAsync` |
| D3 | An access token about to expire | Sends it, waits for a 401/403/302, then refreshes | Refreshes it before use, two minutes ahead of expiry | `TokenProvider` |
| D4 | A token refresh | Re-saves `sitecore.json` as well as `user.json`, which leaves a git diff | Writes only `user.json` | `LoginStore.Save` |
| D5 | Version handshake with Management Services | Claims its own version; rejected by a server with a higher major version | Opts out of the comparison and detects features from the schema ([ADR 0006](adr/0006-feature-detection-not-version-claims.md)) | `ManagementClient` |
| D6 | Credentials for CI and agents | None from the environment | `TERN_<ENDPOINT>_CLIENT_ID`/`_CLIENT_SECRET` (or `TERN_CLIENT_ID`/`TERN_CLIENT_SECRET`), from the environment or the `TERN_*` lines of `.tern/.env` or a `.env` beside `sitecore.json`, never written to disk | `EnvironmentCredentials` |
| D7 | `plugin add/remove/init` | Download NuGet plugins | Do nothing: the plugins' commands are built in ([ADR 0002](adr/0002-native-aot-and-executable-extensions.md)) | — |
| D9 | `ser diff` between two Endpoints that borrow their login through `ref` (for example `cloud environment connect` ones) | Fails with "You are not authenticated (missing access token)" | Uses the referenced login, as every other command does | Run against a local container and a SitecoreAI CM, connected through `ref`, on 2026-10-20 |
| D8 | `cloud environment log download` without `-o`/`--outputpath` | Fails: the output path is required | Saves the file in the current folder | `CloudCommands.SaveFile` |
| D10 | Push where the CM has an item at a serialized item's path but with another ID | Stops with "Non-unique paths cannot be serialized"; the item has to be deleted by hand before pushing again | Deletes the CM's item, then creates the serialized one, when the Include allows deletes. The delete takes the CM item's subtree with it, so source items that were below it are created again rather than moved or updated | `PlannerTests.Deletes_a_target_item_with_another_id_at_the_same_path_before_creating`, `PlannerTests.Recreates_the_children_of_a_target_item_replaced_at_the_same_path` |
| D11 | Endpoints a team shares through source control | Only `.sitecore/user.json`, which holds tokens and secrets, so each person adds every endpoint | Also reads endpoints from the committed `.tern/tern.json`, layered under `user.json`; it holds no secret, and its `allowWrite: false` can't be overridden locally ([ADR 0013](adr/0013-shared-endpoints-in-tern-json.md)) | `SharedEndpointTests` |
| D12 | Pull where the source lists an item but its `serialize` returns `data: null` (seen with a large video in the media library) | Stops the whole pull with "Unable to find item data for … flagged to be created, in source data store. This should never occur." | Writes every other item, warns with the skipped item's path and ID, and counts it as `skipped` in `--json` | `FileItemTreeTests.Deviation_D12_…` |

## Where Tern matches a known Official CLI bug

Matching these keeps results identical. They are listed so nobody "fixes" them by accident.

- A single-line value that starts with `|` is read as an empty block.
- On Linux and macOS, a role's file name contains a literal backslash (`sitecore\Author.yml`) because the role name is joined to the folder as-is.
- Item files are written with the platform's line ending, but block values are always read back joined with CRLF.
- In a resource package on Linux, a multilist joined from CRLF-separated values keeps its carriage returns.

## Differences that are not Deviations

These change how Tern works, never what it produces.

- **Request pattern.** Tern sends bigger batches and more requests in parallel, and reads the Change history where it can ([ADR 0005](adr/0005-stock-management-services-first.md)).
- **Order within a level.** Siblings may be listed, and created, in a different order. Parents always come before children, so the resulting state is the same.
- **Messages.** Progress and log lines on stderr follow the Official CLI's wording where scripts are likely to read them (`[master] Discovered N changes…`), but are not a contract. Tern adds lines of its own: the endpoint at the start, the counts by action and by module after a summary, and a closing `Finished in` line. `--json` is the contract ([CLI contract](cli-contract.md)).
