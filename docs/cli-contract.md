# CLI contract

These rules apply to every command. Scripts and agents can rely on them.

- **Streams.** stdout carries results only. Progress, logs and warnings go to stderr.
- **`--json`.** Produces exactly one JSON document per command on stdout, with a `schemaVersion`. A Plan's JSON form is part of this contract ([ADR 0008](adr/0008-plans-with-signature-preconditions.md)).
- **Exit codes.** Documented and kept stable. They start from the Official CLI's own codes, so existing scripts keep working.
- **Prompts.** Never shown without a TTY. A destructive action needs `--yes` or a reviewed Plan.
- **Telemetry.** None, ever.
- **Compatibility.** Command names, options and short flags match the Official CLI ([ADR 0003](adr/0003-drop-in-proven-against-the-official-cli.md)). New options are additions only.
