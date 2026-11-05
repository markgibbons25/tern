# Management Services

Every CM operation is a GraphQL POST to `https://<cm>/sitecore/api/management` with `Authorization: Bearer <token>` and a JSON body `{"query", "variables"}`. The account must be a Sitecore Administrator. The server echoes any request header that starts with `sitecore-graphql` back on the response.

Tern sends `sitecore-graphql-version-comparing-enabled: False`, which tells the server to skip its version check ([ADR 0006](../adr/0006-feature-detection-not-version-claims.md)). `tern env info` lists the root fields the CM offers, and that list is what feature detection uses.

A response with a non-empty `errors` array is a failure, even when the HTTP status is 200. A 302 or 404 means the endpoint is missing. A 401 or 403 means the token was refused, and Tern then refreshes it once.

## Reading items: `serialize`

```graphql
serialize(path: String!, scope: TreeScope = SINGLE_ITEM, database: String = "master", excludedFieldIds: [String]): [ItemMetadata]
```

- `path` is an item path or an item ID.
- `scope` is one of `SINGLE_ITEM`, `ITEM_AND_CHILDREN` or `ITEM_AND_DESCENDANTS`, and the result is a flat list.
- `ItemMetadata` has `id`, `parentId`, `templateId`, `path`, `hasChildren`, `dataSignature` and `data`.
- `data` is the whole item as a JSON object:

```json
{ "id": "…", "parentId": "…", "templateId": "…", "path": "/sitecore/…", "name": "…", "branchId": "00000000-…", "hasChildren": false,
  "sharedFields": [ { "fieldId": "…", "value": "…", "nameHint": "Title", "blobId": "…" } ],
  "unversionedFields": [ { "language": "en", "fields": [ … ] } ],
  "versions": [ { "language": "en", "versionNumber": 1, "fields": [ … ] } ] }
```

Field values arrive already formatted for Item files. Multilist-type fields are one ID per line, and layout XML is indented. Excluded fields are removed on the server. Blob fields carry `blobId` and the content as base64.

Many requests can be packed into one document as aliased fields (`a0: serialize(…) a1: serialize(…)`). Tern packs single-item and children requests together, and sends each subtree request on its own because one subtree can return thousands of items.

`serialize { data }` can answer `"data": null`, with no `errors`, for an item it lists. On a SitecoreAI CM in November 2026 this happened to media items with a 236 MB blob (about 315 MB of base64) when two of them shared a request: both came back `null` after 17 to 18 s, and each came back whole, after about 30 s, when asked for alone. The same item asked for alone also came back `null` once, minutes after it was uploaded, and once failed with a 502; a 120 MB item has always come back. So Tern asks again, on its own, for any item that came back `null` in a request it shared with others, and only an item still `null` alone is reported as returning no data.

## Writing items: `executeSerializationCommands`

```graphql
mutation($commands: [ItemCommand!]!, $logLevel: SerializationResultLogLevel) {
  executeSerializationCommands(commands: $commands minimumLogLevel: $logLevel) { name success messages { logLevel message eventID { id name } } }
}
```

Each `ItemCommand` is `{ itemID, parentID, database, command, data }`. `data` is a JSON-encoded string:

| command | data |
|---|---|
| `CREATE` | The whole item, in the same shape as `serialize`'s `data` |
| `UPDATE` | An array of `{command, data}`: `CHANGE_TEMPLATE` (template ID), `UPDATE` (`{fieldId, value, blobId?, language?, version?}`), `RESET_FIELD` (`{fieldId, language?, version?}`), `ADD_VERSION` and `REMOVE_VERSION` (`{language, version}`) |
| `MOVE` | The new parent ID; `parentID` is the item's current parent |
| `RENAME` | The new name |
| `RECYCLE` | `{}` |

Every batch is validated as a whole before any of it runs. After each batch the server resets the template engine, schedules link-database and index updates, and rebuilds its GraphQL schemas. That fixed per-batch cost is why Tern's batch size is a setting (`writeBatchSize`, default 1000). A batch also ends at about 8 MB of command data, so batches of large media items stay a reasonable size. On the local CM, creating 1,000 items took 15.0 s in batches of 100 and 11.3 s in one batch. Batches must go one at a time: two `executeSerializationCommands` calls at once fail inside the server (`NullReferenceException`, `ArgumentOutOfRangeException`) in most runs and can leave a batch half written, on a local CM and in the same code on SitecoreAI. When they did succeed, two or four batches at once created items 1.6 to 2.8 times faster on SitecoreAI, so the server, not the client, sets the write speed.

A large media item makes a large command: a 236 MB blob is a `CREATE` of about 315 MB, sent in its own batch. Its `data`, the item's JSON as one string, is longer than `Utf8JsonWriter` writes in one call (166,666,666 characters), so Tern writes it, and any field value that long, in segments. The escaping matters at that size. With `System.Text.Json`'s default encoder, which writes a quote as `"` and a base64 `+` as `+`, and then escapes those backslashes again in `data`, the request was 344 MB and the CM answered `OUT_OF_MEMORY` every time; escaping only what JSON needs, as the Official CLI does, made it 314.7 MB, and the CM created the item in about 20 s. A CM can also create such an item and still answer 500: the Official CLI's `ser push` of one did, and the item was there, whole, afterwards.

## Change history: `history`

```graphql
{ history { currentTimestamp } }
query($timestamp: String!) { history { currentTimestamp entries(timestamp: $timestamp) { id path database oldPath changeType } } }
```

`changeType` is one of `CREATE`, `UPDATE_OR_RENAME`, `RECYCLE` or `MOVE`. Entries are kept for one day. `tern ser watch` polls it.

## Roles and users

```graphql
query($predicates: [PredicateGraphType]) { roles(predicates: $predicates) { roleName memberOfRoles serializedItemId } }
mutation($commands: [RoleCommandGraphType]) { pushCommands(commands: $commands) { success name messages { logLevel message } } }
query($predicates: [Predicate]) { users(predicates: $predicates) { userName email comment creationDate isApproved profileProperties { name content contentType isCustomProperty } roles } }
mutation($commands: [UserCommand]) { pushUsers(commands: $commands) { success name messages { logLevel message } } }
```

- Predicates are `{domain, pattern}`.
- Role commands are `{roleData, parentRoleData?, roleCommandType}` with `ADD`, `UPDATE`, `REMOVE`, `ASSIGN` or `UNASSIGN`.
- User commands are `{userData, userCommandType}` with `ADD`, `UPDATE` or `REMOVE`.

## Jobs

Publishing, indexing, database cleanup and resource-package cleanup each start a server job and return an ID, which Tern polls every 750 ms.

- **Publishing:** `publish`, `singleItemPublish`, `publishingStatus` and `listOfTargets`.
- **Indexing:** `indexesList`, `indexesStatistic`, `rebuildIndex`, `populateSchema` and `indexingStatus`.
- **Database cleanup:** `databaseCleanup` and `databaseCleanupStatus`, with tasks such as `clean-blobs` and `rebuild-descendants`.
- **Resource-package cleanup:** `clean` and `cleanupstatus`.
- **System:** `system { sitecoreInfo managementServices { name version } }`.
