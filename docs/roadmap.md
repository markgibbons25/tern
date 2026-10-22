# Roadmap

Each milestone is finished only when its commands pass the differential tests against the Official CLI and are no slower on any benchmark ([ADR 0003](adr/0003-drop-in-proven-against-the-official-cli.md)).

Versions stay at `0.1.x`, and finishing a milestone does not bump them. Moving past `0.1` is a separate, deliberate decision. `tern --version` prints Tern's version and the Official CLI line it has been proven against, for example "drop-in for Sitecore CLI 6.0.x".

| | |
|---|---|
| Package | `Tern.Cli`, command `tern` |
| Settings | `tern.json`, `TERN_*` ([ADR 0011](adr/0011-tern-settings-live-outside-sitecore-json.md)) |

1. **M1: read side.**
   - Commands: `login`, `connect`, `env`; `ser info`, `explain`, `validate`, `pull`, `diff`.
   - Plans, `--json`, and the benchmark and differential-test harness.
   - New: `item get` and `item query`.
2. **M2: write side.** Push, `apply`, Watch, `ser package`.
3. **M3: the other CM commands.** `publish`, `index`, `dbcleanup`, `itemres`.
4. **M4: SitecoreAI.** The `cloud` commands, against the Deploy API.
5. **M5: beyond Management Services.**
   - Built: running SPE scripts with their output captured (`script run`), and authoring without an Administrator through the Authoring GraphQL API (`authoring query`, `item set`).
   - Still open: on-prem testing on XM 10.4.

## Platform compatibility

We follow the CLI line Sitecore currently supports: Management Services 5.1.25 and later, on XM/XP 10.1–10.4 and SitecoreAI ([KB1000576](https://support.sitecore.com/kb?id=kb_article_view&sysparm_article=KB1000576)). XP 10.0 is out of scope. 10.5 is added once Sitecore lists it.
