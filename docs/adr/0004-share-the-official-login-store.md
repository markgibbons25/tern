# Share the Official CLI's login store

We read and write the Official CLI's `.sitecore/user.json` in its own layout, and keep any fields we don't recognise exactly as we found them. We do not keep a separate store. Existing Endpoints therefore carry over unchanged, `-n dev` means the same thing in both tools, and a team can run the two side by side while they switch. The risk is that one tool could break the other's file. Two things keep that in check: we preserve unknown fields, and differential tests check that the Official CLI still works after this tool has written the file.

## Consequences

- Client secrets are stored in plain text in `user.json` by default, exactly as the Official CLI stores them. That is no worse than today, and it is what lets the two tools share the file.
- Storing secrets in the OS keychain is an opt-in. The Official CLI cannot refresh an Endpoint stored that way.
- Credentials can also come from environment variables, for CI and agents. Those are never written to disk.
