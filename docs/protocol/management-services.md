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

Every batch is validated as a whole before any of it runs. After each batch the server resets the template engine, schedules link-database and index updates, and rebuilds its GraphQL schemas. That fixed per-batch cost is why Tern's batch size is a setting (`writeBatchSize`, default 100).

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
