# .NET Native AOT, with extensions as separate executables

We build on the current .NET release, .NET 11, and publish with Native AOT. Since .NET 9 a standard-term release is supported for 24 months, so .NET 11's support ends with .NET 10 LTS's, in November 2028, and moving to each new release costs no support. AOT binaries carry their runtime, so users need no particular .NET installed to run Tern. It ships as a platform-specific .NET tool, so `dotnet tool`, local tool manifests and `dnx` all work, and as standalone binaries for machines without the SDK. .NET keeps us in the ecosystem Sitecore developers already use. AOT removes most of the startup cost, which is paid on every agent call and every Watch cycle.

Native AOT cannot load assemblies at runtime, so this tool has no in-process Plugin model. Extensions work the way git's do: any executable named `<command>-<name>` on PATH becomes `<command> <name>`, in any language. The `plugins` list in `sitecore.json` is read and never loaded; the commands those Plugins provide are built into the tool.

## Considered Options

- **TypeScript on Node.** It is the team's main language and could share code with the Sitecore MCP server. We rejected it in favour of staying close to Sitecore's own runtime and getting more raw speed.
- **JIT .NET with runtime plugin loading**, as the Official CLI does. We rejected it because of the startup cost and the plugin-version pinning it forces on `sitecore.json`.

## Consequences

- Every dependency must be AOT-safe: source-generated JSON serialisation and no reflection-based YAML or GraphQL client libraries.
