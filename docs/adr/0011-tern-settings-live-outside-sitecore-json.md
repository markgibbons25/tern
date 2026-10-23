# Tern's settings live outside sitecore.json

Tern has settings of its own: batch size, parallelism, keychain use and so on. They never go into `sitecore.json` or a Module file, because the Official CLI's schema for those files forbids extra properties, and adding a key there could break it. Tern reads its settings from these places, highest precedence first:

1. command-line flags
2. `TERN_*` environment variables
3. a project-level `tern.json`, which is committed: `.tern/tern.json` at the repository root (ADR 0014), or else one next to `sitecore.json`
4. `~/.config/tern/settings.json`
5. built-in defaults

The settings and their defaults are listed in [settings.md](../settings.md).
