# Shared endpoints live in tern.json, under user.json

A team needs the same environments everywhere: where each CM is, and which ones may be written to. The login store, `.sitecore/user.json`, can't carry them in source control, because logins write access tokens, refresh tokens and client secrets into it (ADR 0004 shares it with the Official CLI, so Tern can't change that).

So a project's committed `tern.json` (ADR 0011) can hold an `endpoints` object, and Tern layers it under `user.json`:

- `tern.json` takes only `host`, `authority`, `audience`, `allowWrite` and `useClientCredentials`. Anything else, a client ID or secret included, is refused with an error rather than ignored, so a credential can't be committed by mistake. Credentials come from `.env` or `TERN_*` variables, or from a login, which stores them in `user.json` as before.
- Where both files name an endpoint, `tern.json` wins for host, authority, audience and `useClientCredentials`: the team's definition is the one in source control. Tokens and secrets come from `user.json`.
- `allowWrite` is the stricter of the two. An endpoint the team marks read-only, such as prod, stays read-only whatever a local login says, and anyone can still make an endpoint read-only for themselves.
- Tern never writes a `tern.json` endpoint into `user.json`. One gets an entry there only when a login or token refresh writes to it, and then only what was written, so a host change in `tern.json` is never shadowed by an old copy.

The Official CLI doesn't read `tern.json`, so it doesn't see these endpoints ([deviation D11](../deviations.md)).
