# Item files

## Format

An Item file is UTF-8 with a BOM and the platform's line ending. It starts with `---`. It is a line-based dialect, not general YAML:

```yaml
---
ID: "<guid>"
Parent: "<guid>"
Template: "<guid>"
Path: /sitecore/content/Home
BranchID: "<guid>"            # only when the item came from a branch
SharedFields:
- ID: "<guid>"
  Hint: <field name>          # only when there is one
  BlobID: "<guid>"            # only for blob fields
  Value: <value>
Languages:
- Language: en
  Fields:                     # unversioned fields, only when there are any
  - ID: …
  Versions:
  - Version: 1
    Fields:
    - ID: …
```

- **Order.** Fields are sorted by GUID, languages by ordinal name and versions by number. A duplicate keeps the first entry.
- **Values.** A value with a line break, a double quote or a backslash is written as a `|` block, indented two spaces deeper than its key, one source line per line. Otherwise a value containing any of `" : [ ] { } ! ? -` is wrapped in double quotes. Anything else is written as it is.
- **Reading.** A key runs to the first colon, and the value starts two characters after it. Lines that are blank or start with `#` are skipped between keys. Block lines are joined with CRLF on every platform. `DB` is only read straight after `Path`.

`tern ser validate` and the corpus tests (`CorpusRoundTripTests`) check that every file the Official CLI wrote is reproduced byte for byte.

## Where files go

For an Include named `inc` at `/sitecore/content/Site`, the item `/sitecore/content/Site/A/B` is stored at `<module folder>/<items path>/inc/Site/A/B.yml`, and its children go in `…/A/B/`.

- **Characters.** Leading and trailing spaces are trimmed. The characters `" < > | : * ? \ / $` and control characters become `#`. Windows device names (`CON`, `COM1` and so on) get a `#` prefix.
- **Long paths.** When the path below the Include's root is longer than `maxRelativePathLength`, the deepest ancestor that still fits is replaced by the first 16 hex characters of the SHA-256 of its path, as one folder directly under the Include. Path length counts the segment lengths plus one per segment.
- **Aliases.** A rule's `alias` replaces that subtree's prefix with `/<alias>`.

The tree is built from each file's `Path` and `Parent`, never from the folder layout.

## Signature

An item's Signature is `Base64(SHA-256(UTF-8(basis)))`. The basis is a concatenation:

1. The template ID.
2. Shared fields, ordered by GUID.
3. For each language with unversioned fields, in culture-sensitive order: the language, then its fields.
4. For each version, ordered by language and then number: the language, the number, then its fields.

Each field contributes its ID, followed by its blob ID for a blob field, or otherwise its value with every CR and LF removed. `__Lock`, `__Owner`, `__Revision`, `__Updated`, `__Updated by` and `Last run` are left out. Name and parent are not part of the Signature; moves and renames are detected separately. The unit tests hold Signatures that a CM computed, so the algorithm is pinned to the server's.
