---
name: switch-to-tern
description: Switch a Sitecore repository from the Sitecore CLI (sitecore.cli, `dotnet sitecore`) to Tern. Use when asked to switch, migrate or move a project to Tern, or to replace the Sitecore CLI.
license: MIT
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
- SitecoreAI Deploy: an `xmcloud.build.json` at the root means SitecoreAI (XM Cloud) Deploy builds this repository. Deploy runs `dotnet tool restore` and uses the Sitecore CLI from the manifest, with the `sitecore.json` plugins from Sitecore's feed, to turn the Item files into Items as Resources. Tern can't stand in for it there, so the manifest, the plugins and the feed stay.
- Endpoints: the environments the team logs in to. Read their names and hosts from `login` calls in scripts and CI (`--cm`, `--authority`, `--client-credentials`, `-n`), README setup steps and `.sitecore/user.json` if present (only the `endpoints` names, `host`, `authority`, `audience` and `allowWrite`; never copy anything else out of it, since it holds tokens and secrets). Note which ones are production.
- Content sync and diff scripts: scripts that copy or compare content between environments with the Sitecore CLI. Signs of one: `ser pull` from one Endpoint then `ser push` to another, `ser diff` between two Endpoints, rewriting the `modules` list in `sitecore.json` (or writing a temporary module) and restoring it, logging in again with client credentials on every run, or deleting the items folder before a pull.

Done when you can list the manifest entry, the package sources, the CI install steps, whether SitecoreAI Deploy builds the repository, the endpoints, every caller with its file and line, and any content sync or diff scripts. Show the user that list before changing anything.

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

- In every caller from step 1, replace `dotnet sitecore` (and a bare `sitecore`) with `tern`. Arguments stay as they are. Instruction prose that names a bare login command (no arguments) is the exception: on SitecoreAI, the local Docker CM included, the interactive sign-in is `tern cloud login`, so write that rather than `tern login`.
- In CI, where the pipeline installed the Sitecore CLI, install Tern instead: `dotnet tool install --global Tern.Cli --version <version>`, pinned to the version from step 3 so builds are repeatable. Keep `dotnet tool restore` if the manifest still holds other tools.
- Where SitecoreAI Deploy builds the repository, keep the `sitecore.cli` entry in `.config/dotnet-tools.json`, the `sitecore.json` plugins and Sitecore's feed in `nuget.config`: without them the next deployment can't build its Items as Resources. Remove only the local `dotnet tool restore` steps that served the Sitecore CLI, and add a line to the README or agent instructions saying why the manifest stays.
- Otherwise, remove the `sitecore.cli` entry (`dotnet tool uninstall sitecore.cli`). If it was the only entry, delete the manifest and any `dotnet tool restore` step that only served it.
- `plugin init/add` calls succeed and do nothing in Tern, since every plugin's commands are built in; suggest removing them.
- Share the endpoints from step 1 in `.tern/tern.json`, in a `.tern` folder at the repository root, so every developer and CI run gets them without adding them by hand. The Sitecore CLI ignores this folder. Mark production `"allowWrite": false`: Tern then refuses to write to it whatever a local login says. Endpoints that CI or scripts reach with client credentials get `"useClientCredentials": true`; a person signs in with `tern cloud login` on SitecoreAI (the local Docker CM included) and with `tern login` against any other identity server. For example:

  ```json
  {
    "endpoints": {
      "local": { "host": "https://xmcloudcm.localhost", "allowWrite": true },
      "dev":   { "host": "https://<dev-cm>",  "allowWrite": true,  "useClientCredentials": true },
      "prod":  { "host": "https://<prod-cm>", "allowWrite": false, "useClientCredentials": true }
    }
  }
  ```

  Each endpoint takes only `host`, `authority`, `audience`, `allowWrite` and `useClientCredentials`; Tern refuses a client ID, secret or token here, because the file is committed. Use the endpoint names the scripts already pass with `-n`, so they keep working. Leave out `authority` and `audience` where they are SitecoreAI's defaults. If the repository already has a `.tern/tern.json`, add an `endpoints` object to it rather than replacing it; an older `tern.json` beside `sitecore.json` moves into `.tern`. Credentials go in a gitignored `.tern/.env`, never in `tern.json`. Ask the user before marking an endpoint writable that you aren't sure is non-production.

Done when a search for `dotnet sitecore` and `sitecore.cli` finds nothing left to change, apart from a manifest kept for SitecoreAI Deploy, and `tern env list` shows each endpoint from step 1 marked `(tern.json)`, with production `[read-only]`.

## 5. Prove nothing else changed

Run the step 2 commands again with `tern`:

- `tern ser info` lists the same modules, includes and paths as the baseline.
- `tern ser validate` reports what the baseline reported.
- `git status --porcelain` shows only the files step 4 edited or added, `.tern/tern.json` among them (and `nuget.config`, if step 3 changed it).

If the user names an environment Tern may read from, also run `tern ser pull --what-if -n <environment>`. It writes nothing; its list of changes is what a real pull would bring down, the same list the Sitecore CLI would report.

Done when every check matches. A mismatch is a finding: stop, show the user both outputs, and leave the change for them to decide on.

## 6. Hand over

Tell the user:

- What changed: each edited file, the manifest entry (removed, or kept for SitecoreAI Deploy and why), `nuget.config` if touched, and the new `.tern/tern.json` with each endpoint and whether it allows writes.
- **Content sync and diff scripts** from step 1: they work as they are after the replace, but Tern's `content sync`, `content diff` and `content transfer` can take their place. Show the user, for each script, the Tern command that does its job, using the table in [docs/content.md](https://github.com/markgibbons25/tern/blob/main/docs/content.md#replacing-a-content-sync-script) (for example, rewriting `sitecore.json` to pick a module becomes `-m <module>`). Suggest it; replace a script only if the user asks. Where such a script keeps its own `sitecore.json` and content modules, or a list of modules to copy, suggest moving those modules into `.tern`: the content commands then run from anywhere in the repository, and `ser pull` and `ser push` never touch them. The `tern` skill's "Setting up a content migration" walks through it.
- **Every developer** runs `dotnet tool install --global Tern.Cli` once; the README or setup script should say so.
- **Plugins**: `sitecore.json`'s `plugins` list and `.sitecore/package-cache` can stay; Tern ignores them. Where SitecoreAI Deploy builds the repository, the plugins must stay, since its Sitecore CLI needs them.
- **SitecoreAI Deploy** keeps using the Sitecore CLI, so deployments and local pushes now run on different tools. The first deployment after the switch is the real test; tell the user to watch its Items as Resources step.
- **Logins**: Tern reads and writes the same `.sitecore/user.json`, so existing logins carry over; `.tern/tern.json` says where each endpoint is, `user.json` keeps the tokens. CI can keep its `login --client-credentials` step, or set `TERN_CLIENT_ID` and `TERN_CLIENT_SECRET` (or `TERN_<ENDPOINT>_CLIENT_ID` and so on) and skip it.
- **Rollback**: revert the edited files (`git checkout -- <files>`), delete `.tern` if step 4 created it, then `dotnet tool restore`.

Leave the commit to the user unless they asked you to make it.
