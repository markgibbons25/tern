---
name: switch-to-tern
description: Switch a Sitecore repository from the Sitecore CLI (sitecore.cli, `dotnet sitecore`) to Tern. Use when asked to switch, migrate or move a project to Tern, or to replace the Sitecore CLI.
---

# Switch a repository to Tern

Tern is a drop-in replacement for the Sitecore CLI: same `sitecore.json`, Module files, Item files, logins, commands and options. Switching is installing Tern, replacing `dotnet sitecore` with `tern` wherever it is called, then proof that nothing else changed.

Tern is the `Tern.Cli` package, installed as a global tool. Installing it gives the `tern` command.

## 1. Survey

Find every place the Sitecore CLI is installed or called:

- `.config/dotnet-tools.json`: the `sitecore.cli` entry and its version.
- `nuget.config`: the package sources. Tern comes from nuget.org; a `<clear />` that leaves only Sitecore's feed blocks it.
- Callers: search the repo for `dotnet sitecore` and `sitecore ` in scripts (`*.ps1`, `*.sh`, `*.cmd`), CI pipelines (`.github/workflows`, `azure-pipelines*.yml`, `*.yml` under build folders), Dockerfiles, `package.json` scripts and READMEs.
- CI steps that install the Sitecore CLI: `dotnet tool restore`, or `dotnet tool install sitecore.cli`.
- `sitecore.json`: its `plugins` list and Sitecore CLI version (6.x or 7.x). Tern reads both.
- Content sync and diff scripts: scripts that copy or compare content between environments with the Sitecore CLI. Signs of one: `ser pull` from one Endpoint then `ser push` to another, `ser diff` between two Endpoints, rewriting the `modules` list in `sitecore.json` (or writing a temporary module) and restoring it, logging in again with client credentials on every run, or deleting the items folder before a pull.

Done when you can list the manifest entry, the package sources, the CI install steps, every caller with its file and line, and any content sync or diff scripts. Show the user that list before changing anything.

## 2. Record the baseline

With the Sitecore CLI still installed, run and keep the output of:

```bash
git status --porcelain
dotnet sitecore ser info
dotnet sitecore ser validate
```

Done when all three outputs are saved for comparison. If the working tree has changes, tell the user, since step 5 relies on a clean `git status` to show Tern changed nothing it shouldn't.

## 3. Install Tern

```bash
dotnet tool install --global Tern.Cli
```

If the install can't find the package, add nuget.org to `nuget.config` (`<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />`) and install again.

Done when `tern --version` prints a line starting `tern` and naming the Sitecore CLI line it replaces.

## 4. Replace the callers

- In every caller from step 1, replace `dotnet sitecore` (and a bare `sitecore`) with `tern`. Arguments stay as they are.
- In CI, where the pipeline installed the Sitecore CLI, install Tern instead: `dotnet tool install --global Tern.Cli --version <version>`, pinned to the version from step 3 so builds are repeatable. Keep `dotnet tool restore` if the manifest still holds other tools.
- Remove the `sitecore.cli` entry from `.config/dotnet-tools.json` (`dotnet tool uninstall sitecore.cli`). If it was the only entry, delete the manifest and any `dotnet tool restore` step that only served it.
- `plugin init/add` calls succeed and do nothing in Tern, since every plugin's commands are built in; suggest removing them.

Done when a search for `dotnet sitecore` and `sitecore.cli` finds nothing left to change.

## 5. Prove nothing else changed

Run the step 2 commands again with `tern`:

- `tern ser info` lists the same modules, includes and paths as the baseline.
- `tern ser validate` reports what the baseline reported.
- `git status --porcelain` shows only the files step 4 edited (and `nuget.config`, if step 3 changed it).

If the user names an environment Tern may read from, also run `tern ser pull --what-if -n <environment>`. It writes nothing; its list of changes is what a real pull would bring down, the same list the Sitecore CLI would report.

Done when every check matches. A mismatch is a finding: stop, show the user both outputs, and leave the change for them to decide on.

## 6. Hand over

Tell the user:

- What changed: each edited file, the removed manifest entry, and `nuget.config` if touched.
- **Content sync and diff scripts** from step 1: they work as they are after the replace, but Tern's `content sync`, `content diff` and `content transfer` can take their place. Show the user, for each script, the Tern command that does its job, using the table in [docs/content.md](https://github.com/markgibbons25/tern/blob/main/docs/content.md#replacing-a-content-sync-script) (for example, rewriting `sitecore.json` to pick a module becomes `-m <module>`). Suggest it; replace a script only if the user asks.
- **Every developer** runs `dotnet tool install --global Tern.Cli` once; the README or setup script should say so.
- **Plugins**: `sitecore.json`'s `plugins` list and `.sitecore/package-cache` can stay; Tern ignores them.
- **Logins**: Tern reads and writes the same `.sitecore/user.json`, so existing logins carry over. CI can keep its `login --client-credentials` step, or set `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_<ENDPOINT>_CLIENT_ID` and so on) and skip it.
- **Rollback**: revert the edited files (`git checkout -- <files>`), then `dotnet tool restore`.

Leave the commit to the user unless they asked you to make it.
