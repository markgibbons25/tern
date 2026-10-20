# One item-tree seam, three projects

Tern is built around a single interface, the item tree. It has two implementations: Item files on disk, and a CM. The Plan engine compares any two item trees, so these are all the same code:

- Pull and Push (file ↔ CM)
- diffing or pushing between Endpoints (CM ↔ CM)
- Validate (file ↔ file)

The code is split into three projects:

- **`Tern.Core`** is pure: configuration rules, the Item file reader and writer, Signatures, item trees and the Plan engine. It has no network access and no clock.
- **`Tern.Remote`** holds the Management Services transport, auth and the login store, and later the Deploy API.
- **`Tern.Cli`** is a thin layer of commands and output rendering.

## Considered Options

- **A project per concern** (configuration, Item files, Signatures, planning, transport, auth). We rejected it because it adds Native AOT build overhead and turns internals into public surface. Within a project, concerns are separated by namespace instead.
