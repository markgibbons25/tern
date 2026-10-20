# .NET 10 Native AOT, with extensions as separate executables

We build on .NET 10 LTS and publish with Native AOT. It ships as a platform-specific .NET tool, so `dotnet tool`, local tool manifests and `dnx` all work, and as standalone binaries for machines without the SDK. .NET keeps us in the ecosystem Sitecore developers already use. AOT removes most of the startup cost, which is paid on every agent call and every Watch cycle.

Native AOT cannot load assemblies at runtime, so this tool has no in-process Plugin model. Extensions work the way git's do: any executable named `<command>-<name>` on PATH becomes `<command> <name>`, in any language. The `plugins` list in `sitecore.json` is read and never loaded; the commands those Plugins provide are built into the tool.

## Considered Options

- **TypeScript on Node.** It is the team's main language and could share code with the Sitecore MCP server. We rejected it in favour of staying close to Sitecore's own runtime and getting more raw speed.
- **JIT .NET with runtime plugin loading**, as the Official CLI does. We rejected it because of the startup cost and the plugin-version pinning it forces on `sitecore.json`.

## Consequences

- Every dependency must be AOT-safe: source-generated JSON serialisation and no reflection-based YAML or GraphQL client libraries.
