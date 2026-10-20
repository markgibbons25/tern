# Resource packages

`tern itemres create` writes `items.<database>.<name>.dat`, the file a SitecoreAI CM loads as read-only "items as resources". It is a protobuf message with three repeated fields:

| Field | Entry |
|---|---|
| 1 | `{1: item ID, 2: {1: ID, 2: name, 3: parent ID, 4: template ID, 5: branch ID}}` |
| 2 | `{1: item ID, 2: {1: field ID, 2: value}…}`, the shared fields |
| 3 | `{1: item ID, 2: {1: language, 2: {1: version, 2: {1: field ID, 2: value}…}…}…}` |

- **Guids** are a message of two `fixed64` fields, the low and high halves of the Guid's 16 bytes. An empty Guid field is left out.
- **Nested maps** repeat their entries inline as field 2 of the enclosing entry.
- **Unversioned fields** are version 0 and come before the versioned ones.
- **Versions without fields** are left out, with a warning.
- **Values are stored as Sitecore keeps them,** not as Item files hold them:
  - two or more line-separated GUIDs become a `|`-separated list;
  - `__Renderings` and `__Final Renderings` XML is written without indentation.

`tests/differential/compare-dat.py` compares two packages by content. On the synthetic fixture Tern's output is byte-identical to the Official CLI's. On a 2,981-item project the contents are identical and only the order of entries within a level differs.
