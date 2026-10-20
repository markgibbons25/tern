# Clean-room reimplementation of the Official CLI's protocol

The Official CLI and Management Services are closed source, and Tern has to speak their protocol byte for byte. We learn that protocol by decompiling them, but decompiled code is only ever read, never copied. Everything in this repo is written in our own words and confirmed against live behaviour on CMs we own: schema introspection, plus the responses Tern itself records. No Sitecore assembly or decompiled source is ever committed or redistributed, and the Official CLI is downloaded from Sitecore's own NuGet feed whenever a test needs it. This keeps a public, MIT-licensed project clear of Sitecore's copyright while still allowing the reverse engineering that interoperability depends on.

## Consequences

- Protocol knowledge lives in `docs/protocol/`. It is written in prose and backed by committed schema introspections and redacted response recordings, and it never links to or quotes decompiled code.
- GraphQL documents and wire formats are reproduced where the server requires them exactly. They are the interface itself, not Sitecore's implementation.
- We don't capture the Official CLI's traffic. Responses come from the server, so Tern's own recordings show the same thing. A capture proxy is built only if a differential failure cannot be explained any other way.
