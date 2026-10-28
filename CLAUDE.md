# Tern

## Build and test

- Tests: `dotnet test Tern.slnx`
- Native AOT exe (Windows): `dotnet publish src/Tern.Cli -c Release -r win-x64 -o artifacts/tern` writes `artifacts/tern/tern.exe` (`artifacts/` is git-ignored).
- Keep `artifacts/tern/tern.exe` up to date: republish it after every change under `src/` once the tests pass, before reporting the work done. Other repositories run that exe, so a stale one tests old behaviour.

## No client-specific wording

Tern is a public, general-purpose tool. Nothing committed here (code, comments, tests, test data, docs, commit messages) may name or hint at a client, their sites, environments, hosts, people, item paths or content, even when the work came from a client's repository. Describe the problem in generic Sitecore terms instead, and use made-up names (`Site A`, `/sitecore/content/Tenant/Site`) or Sitecore's own well-known IDs in tests. Check the diff for client wording before every commit.

## Native AOT linker on Windows

The ILCompiler targets call `vswhere.exe` from PATH to find the MSVC linker. The Visual Studio Installer folder is often not on PATH, and then the publish fails with `'vswhere.exe' is not recognized` and `link.exe ... exited with code 123`. Put it on PATH for the command before publishing:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
```
