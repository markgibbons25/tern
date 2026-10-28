# Roadmap

Each milestone is finished only when its commands pass the differential tests against the Official CLI and are no slower on any benchmark ([ADR 0003](adr/0003-drop-in-proven-against-the-official-cli.md)).

Tern is at `1.0`. Finishing a milestone does not bump the version; each release does. `tern --version` prints Tern's version.

| | |
|---|---|
| Package | `Tern.Cli`, command `tern` |
| Settings | `.tern/tern.json`, `TERN_*` ([ADR 0011](adr/0011-tern-settings-live-outside-sitecore-json.md), [ADR 0014](adr/0014-the-tern-folder-at-the-repository-root.md)) |

1. **M1: read side.**
   - Commands: `login`, `connect`, `env`; `ser info`, `explain`, `validate`, `pull`, `diff`.
   - Plans, `--json`, and the benchmark and differential-test harness.
   - New: `item get` and `item query`.
2. **M2: write side.** Push, `apply`, Watch, `ser package`.
3. **M3: the other CM commands.** `publish`, `index`, `dbcleanup`, `itemres`.
4. **M4: SitecoreAI.** The `cloud` commands, against the Deploy API.
5. **M5: beyond Management Services.**
   - Built: running SPE scripts with their output captured (`script run`), and authoring without an Administrator through the Authoring GraphQL API (`authoring query`, `item set`).
   - On-prem: tested on XP 10.3 (Management Services 5.2.129, Identity Server client credentials) for `login`, `env`, `ser info`, `validate`, `pull`, `push`, the Plan workflow and `item get`.
   - Still open on-prem: the `content` commands between two XP CMs, device login with the stock `Device` client, and XM 10.4.

## Platform compatibility

We follow the CLI line Sitecore currently supports: Management Services 5.1.25 and later, on XM/XP 10.1–10.4 and SitecoreAI ([KB1000576](https://support.sitecore.com/kb?id=kb_article_view&sysparm_article=KB1000576)). XP 10.0 is out of scope. 10.5 is added once Sitecore lists it.

## Known issues

- **Ctrl+C asks "Terminate batch job (Y/N)?" on Windows.** The .NET SDK installs a native global tool behind a `tern.cmd` launcher, and `cmd.exe` asks this after Tern has already stopped, for example when ending `ser watch`. Either answer is fine. Running `tern.exe` directly, or the standalone binary, avoids it. The fix belongs to the SDK, which plans PowerShell launchers for global tools ([dotnet/sdk#49662](https://github.com/dotnet/sdk/issues/49662)); check again once it ships.
- **`tern` isn't found in Git Bash on Windows.** The same `tern.cmd` launcher is the only file in `%USERPROFILE%\.dotnet\tools`, and Git Bash doesn't run `.cmd` files by their bare name. The package can't change the launcher. Call `tern.cmd`, or save this as `~/bin/tern`; it runs the `tern.exe` the launcher points to, so arguments skip `cmd.exe`, and it keeps working after `dotnet tool update`:

  ```sh
  #!/bin/sh
  tools="$USERPROFILE/.dotnet/tools"
  exe=$(sed -n 's/^"%~dp0\(.*\)" %\*.*$/\1/p' "$tools/tern.cmd" | tr -d '\r' | tr '\\' '/')
  exec "$tools/$exe" "$@"
  ```
