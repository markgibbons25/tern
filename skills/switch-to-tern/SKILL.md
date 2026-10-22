---
name: switch-to-tern
description: Switch a Sitecore repository from the Sitecore CLI (sitecore.cli, `dotnet sitecore`) to Tern. Use when asked to switch, migrate or move a project to Tern, or to replace the Sitecore CLI.
---

# Switch a repository to Tern

Tern is a drop-in replacement for the Sitecore CLI: same `sitecore.json`, Module files, Item files, logins, commands and options. Switching is a package swap in the tool manifest, then proof that nothing else changed.

Pick the path before touching anything:

- **Swap** (the default): replace `sitecore.cli` with `Tern.Cli.Compat`, which installs Tern under the command name `sitecore`. Every `dotnet sitecore` script, pipeline and README keeps working untouched.
- **Side by side**, when the user wants to compare first: add `Tern.Cli` (command `tern`) and keep `sitecore.cli`. Nothing existing changes; the team runs `dotnet tern` alongside `dotnet sitecore` until they swap.

## 1. Survey

Find every place the Sitecore CLI is installed or called:

- `.config/dotnet-tools.json`: the `sitecore.cli` entry and its version.
- `nuget.config`: the package sources. Tern comes from nuget.org; a `<clear />` that leaves only Sitecore's feed blocks it.
- Callers: search the repo for `dotnet sitecore` and `sitecore ` in scripts (`*.ps1`, `*.sh`, `*.cmd`), CI pipelines (`.github/workflows`, `azure-pipelines*.yml`, `*.yml` under build folders), Dockerfiles and `package.json` scripts.
- `sitecore.json`: its `plugins` list and Sitecore CLI version (6.x or 7.x). Tern reads both.

Done when you can list the manifest entry, the package sources, and every caller with its file and line. Show the user that list and the path you'll take.

## 2. Record the baseline

Before changing the manifest, with the Sitecore CLI still installed, run and keep the output of:

```bash
git status --porcelain
dotnet sitecore ser info
dotnet sitecore ser validate
```

Done when all three outputs are saved for comparison. If the working tree has changes, tell the user, since step 4 relies on a clean `git status` to show Tern changed nothing.

## 3. Swap the package

**Swap:**

```bash
dotnet tool uninstall sitecore.cli
dotnet tool install Tern.Cli.Compat
```

**Side by side:**

```bash
dotnet tool install Tern.Cli
```

If the install can't find the package, add nuget.org to `nuget.config` (`<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />`) and install again.

Done when `dotnet tool restore` succeeds and `dotnet sitecore --version` (swap) or `dotnet tern --version` (side by side) prints a line starting `tern` and naming the Sitecore CLI line it replaces.

## 4. Prove nothing else changed

Run the step 2 commands again through Tern (`dotnet sitecore …` after a swap, `dotnet tern …` side by side):

- `ser info` lists the same modules, includes and paths as the baseline.
- `ser validate` reports what the baseline reported.
- `git status --porcelain` shows only the tool manifest (and `nuget.config`, if step 3 changed it).

If the user names an environment Tern may read from, also run `ser pull --what-if -n <environment>`. It writes nothing; its list of changes is what a real pull would bring down, the same list the Sitecore CLI would report.

Done when every check matches. A mismatch is a finding: stop, show the user both outputs, and leave the change for them to decide on.

## 5. Hand over

Tell the user:

- What changed: the manifest lines, and `nuget.config` if touched. Nothing else needs to change.
- **Plugins**: `sitecore.json`'s `plugins` list and `.sitecore/package-cache` can stay; Tern has every plugin's commands built in and ignores them. `plugin init/add` commands in scripts succeed and do nothing.
- **Logins**: Tern reads and writes the same `.sitecore/user.json`, so existing logins carry over. CI can keep its `login --client-credentials` step, or set `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_<ENDPOINT>_CLIENT_ID` and so on) and skip it.
- **Side by side only**: to swap later, replace `dotnet sitecore` with `dotnet tern` in the callers from step 1, or run step 3's swap.
- **Rollback**: revert the manifest change (`git checkout .config/dotnet-tools.json`, then `dotnet tool restore`).

Leave the commit to the user unless they asked you to make it.
