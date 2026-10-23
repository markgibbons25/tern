# The .tern folder at the repository root

Content migrations (copying content between environments with `content sync`, `content diff` and `content transfer`) need Modules of their own: a site's content and media, without the templates and renderings the project serializes. Teams used to keep these in a separate folder with its own `sitecore.json`, so a migration ran only from that folder, and a `ser push` run there by mistake pushed a pulled copy of prod's content as if it were the project's items.

So Tern keeps its project files in a committed `.tern` folder at the repository root:

- `.tern/tern.json` holds Tern's settings (ADR 0011) and shared endpoints (ADR 0013). A `tern.json` beside `sitecore.json` is still read when there is no `.tern/tern.json`.
- `.tern/*.module.json` are the content commands' Modules. Once there is one, the content commands use them instead of `sitecore.json`'s, with their Item files under `.tern/items`.
- `.tern/.env` holds the `TERN_*` credentials, gitignored, and is read before a `.env` beside `sitecore.json`.

Tern finds `.tern` from any folder, looking up no further than the repository root (the folder with `.git`), so a migration runs from anywhere in the repository, with or without a `sitecore.json` above it.

`.tern` is never a serialization project. The `ser` commands leave its Modules out even where a `sitecore.json` glob reaches them, and `ser pull`, `ser push` and `ser watch` refuse to run inside it, naming the folder of `sitecore.json` to change to. Logins stay in `.sitecore/user.json` beside `sitecore.json` (ADR 0004), or at the repository root when there is no `sitecore.json` above the current folder.

## Considered Options

- **A migration folder with its own `sitecore.json`, marked in its `tern.json`.** It blocks `ser` commands there, but a migration still runs only from that folder, and the folder needs a `sitecore.json` the Sitecore CLI would also treat as a project.
- **Content Modules registered in the project's `sitecore.json`.** `ser push` would push them, which is the mistake this prevents.
