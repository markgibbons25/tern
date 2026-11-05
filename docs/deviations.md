# Deviations

A Deviation is a case where Tern does something different from the Official CLI. Tern only does this where the Official CLI fails. Wherever the Official CLI succeeds, Tern gives the same result ([ADR 0007](adr/0007-deviations-only-where-the-official-cli-fails.md)).

Each Deviation below says when it applies, what the Official CLI does, what Tern does, and which test or check shows it.

## Summary

| # | When | Tern |
|---|---|---|
| [D1](#d1-a-block-value-with-a-trimmed-blank-line) | A `\|` block value lost a blank line to an editor | Still reads the whole block |
| [D2](#d2-pull-from-an-endpoint-without-allowwrite) | Pull from an Endpoint without `allowWrite` | Pulls |
| [D3](#d3-an-access-token-about-to-expire) | An access token is about to expire | Refreshes it first |
| [D4](#d4-a-token-refresh) | A token is refreshed | Leaves `sitecore.json` alone |
| [D5](#d5-version-handshake-with-management-services) | Management Services has a newer major version | Still connects |
| [D6](#d6-credentials-for-ci-and-agents) | CI or an agent needs credentials | Reads them from the environment |
| [D7](#d7-plugin-addremoveinit) | `plugin add/remove/init` | Does nothing |
| [D8](#d8-cloud-environment-log-download-without-an-output-path) | `cloud environment log download` without `-o` | Saves to the current folder |
| [D9](#d9-ser-diff-between-endpoints-that-use-ref) | `ser diff` between Endpoints that use `ref` | Works |
| [D10](#d10-push-where-the-cm-has-another-item-at-the-same-path) | Push where the CM has another item at the same path | Replaces it |
| [D11](#d11-endpoints-shared-through-source-control) | A team wants to share Endpoints in git | Reads `.tern/tern.json` |
| [D12](#d12-pull-where-the-source-returns-no-data-for-an-item) | Pull where the source returns no data for an item | Retries, then skips only that item |
| [D13](#d13-pull-where-an-item-moved-away-and-another-took-its-path) | Pull where an item moved away and another took its path | Keeps both |
| [D14](#d14-push-where-the-source-returns-no-data-for-an-item) | Push where the source returns no data for an item | Skips only that item and its children |

## D1. A block value with a trimmed blank line

**When:** a `|` block value in an item file contains a blank line, and an editor trimmed that line's spaces.

**Official CLI:** ends the block at the blank line, then fails to parse the next line.

**Tern:** reads the blank line as part of the block, as long as the block plainly continues after it.

**Test:** `ItemFileFormatTests.Deviation_D1_…`

## D2. Pull from an Endpoint without `allowWrite`

**When:** you pull from an Endpoint that doesn't have `allowWrite` set.

**Official CLI:** refuses the pull, even though a pull only writes local files.

**Tern:** pulls. `allowWrite` still guards every write to a CM.

**Test:** `PushRunner` refuses writes; see `SyncRunner.PullAsync`.

## D3. An access token about to expire

**When:** the access token is close to expiring.

**Official CLI:** sends it anyway, waits for a 401, 403 or 302, then refreshes.

**Tern:** refreshes it before use, two minutes before it expires.

**Test:** `TokenProvider`

## D4. A token refresh

**When:** a token is refreshed.

**Official CLI:** saves `sitecore.json` again as well as `user.json`, which leaves a git diff.

**Tern:** writes only `user.json`.

**Test:** `LoginStore.Save`

## D5. Version handshake with Management Services

**When:** Tern connects to Management Services.

**Official CLI:** sends its own version. A server with a higher major version rejects it.

**Tern:** opts out of the version comparison and works out which features are available from the schema ([ADR 0006](adr/0006-feature-detection-not-version-claims.md)).

**Test:** `ManagementClient`

## D6. Credentials for CI and agents

**When:** a build server or an agent needs to log in without an interactive login.

**Official CLI:** can't read credentials from the environment.

**Tern:** reads `TERN_<ENDPOINT>_CLIENT_ID` and `TERN_<ENDPOINT>_CLIENT_SECRET` (or `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` for every Endpoint). It looks in the environment first, then in the `TERN_*` lines of `.tern/.env`, then in a `.env` file beside `sitecore.json`. It never writes them to disk.

**Test:** `EnvironmentCredentials`

## D7. `plugin add/remove/init`

**When:** you run `plugin add`, `plugin remove` or `plugin init`.

**Official CLI:** downloads NuGet plugins.

**Tern:** does nothing, because the plugins' commands are already built in ([ADR 0002](adr/0002-native-aot-and-executable-extensions.md)).

**Test:** none needed.

## D8. `cloud environment log download` without an output path

**When:** you run `cloud environment log download` without `-o`/`--outputpath`.

**Official CLI:** fails, because it requires the output path.

**Tern:** saves the file in the current folder.

**Test:** `CloudCommands.SaveFile`

## D9. `ser diff` between Endpoints that use `ref`

**When:** you run `ser diff` between two Endpoints that borrow their login from another Endpoint through `ref`, as the ones `cloud environment connect` creates do.

**Official CLI:** fails with "You are not authenticated (missing access token)".

**Tern:** uses the referenced login, as every other command does.

**Check:** run by hand against a local container and a SitecoreAI CM, both connected through `ref`, on 2026-10-20.

## D10. Push where the CM has another item at the same path

**When:** you push, and the CM already has an item at a serialized item's path, but with a different ID.

**Official CLI:** stops with "Non-unique paths cannot be serialized". You have to delete the CM's item by hand and push again.

**Tern:** if the Include allows deletes, deletes the CM's item and then creates the serialized one. Deleting the CM's item also deletes everything under it, so any serialized items that were under it are created again, rather than moved or updated.

**Tests:** `PlannerTests.Deletes_a_target_item_with_another_id_at_the_same_path_before_creating`, `PlannerTests.Recreates_the_children_of_a_target_item_replaced_at_the_same_path`

## D11. Endpoints shared through source control

**When:** a team wants everyone to have the same Endpoints.

**Official CLI:** keeps Endpoints only in `.sitecore/user.json`. That file holds tokens and secrets, so it can't be committed, and each person has to add every Endpoint themselves.

**Tern:** also reads Endpoints from `.tern/tern.json`, which is committed and holds no secrets. `user.json` is layered on top of it, but an `allowWrite: false` in `tern.json` can't be overridden locally ([ADR 0013](adr/0013-shared-endpoints-in-tern-json.md)).

**Test:** `SharedEndpointTests`

## D12. Pull where the source returns no data for an item

**When:** you pull, and the source lists an item but its `serialize` returns `data: null`. Management Services can do this for a very large media item.

**Official CLI:** stops the whole pull with "Unable to find item data for … flagged to be created, in source data store. This should never occur." and writes nothing.

**Tern:** asks for that item again on its own, which is often enough to get it back in full. If it is still `null`, Tern leaves out only that item: it writes every other item, warns with the item's path and ID, and counts it as `skipped` in `--json`.

**Tests:** `FileItemTreeTests.Deviation_D12_…`, `SerializeDataRetryTests`

## D13. Pull where an item moved away and another took its path

**When:** you pull, and since the last pull one item was moved or renamed away from a path, and a different item was created at that same path.

**Official CLI:** writes the new item's file first, over the moved item's file, then moves both to the new path, where the last one written wins. The pull reports success, but one item is lost: usually the moved item (the next pull then fails validation with `OrphanParent`), or the new item if the moved item also changed. The maintainer considers this a defect in the Official CLI and reported it to Sitecore years ago.

**Tern:** moves or renames the item first, then creates the new item at the old path, so both end up where they belong. A move still waits until its new parent has been created.

**Tests:** `FileItemTreeTests.Deviation_D13_…`, `PlannerTests.A_create_where_an_item_moved_away_waits_for_the_move`, `PlannerTests.A_create_where_an_item_was_renamed_away_waits_for_the_rename`

## D14. Push where the source returns no data for an item

**When:** you push to a CM, and the source lists an item but returns no data for it, even when Tern asks for that item alone.

**Official CLI:** stops with the same error as in a pull (D12).

**Tern:** sends every other item and warns with the skipped item's path, ID and reason. It also skips everything under an item it could not create, and counts all of them as `skipped` in `--json`.

**Test:** `CmApplierSkipTests.Deviation_D14_…`

## Where Tern copies a known Official CLI bug

Tern copies these on purpose, so its results stay identical to the Official CLI's. They are listed here so nobody "fixes" them by accident.

- A single-line value that starts with `|` is read as an empty block.
- On Linux and macOS, a role's file name contains a literal backslash (`sitecore\Author.yml`), because the role name is joined to the folder as it is.
- Item files are written with the platform's line ending, but block values are always read back joined with CRLF.
- In a resource package on Linux, a multilist joined from CRLF-separated values keeps its carriage returns.

## Differences that are not Deviations

These change how Tern does its work, never what it produces.

- **Requests.** Tern sends bigger batches and more requests in parallel, and reads the Change history where it can ([ADR 0005](adr/0005-stock-management-services-first.md)).
- **Order of siblings.** Items at the same level may be listed and created in a different order. Parents still always come before their children, so the end result is the same.
- **Messages.** Progress and log lines on stderr use the Official CLI's wording where scripts are likely to read them (`[master] Discovered N changes…`), but they are not a contract. Tern also adds lines of its own: the Endpoint at the start, counts by action and by module after the summary, and a final `Finished in` line. Only `--json` is a contract ([CLI contract](cli-contract.md)).
