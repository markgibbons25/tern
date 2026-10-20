# Protocol notes

How Tern talks to a CM, written from live behaviour on CMs we own: schema introspection and responses recorded with `tern --record <dir>` ([ADR 0001](../adr/0001-clean-room-protocol.md)). Nothing here is copied from Sitecore's code.

- [Management Services](management-services.md): the GraphQL endpoint, item reads and writes, the Change history, roles and users, and the job commands.
- [Item files](item-files.md): the on-disk format, the path rules and the Signature.
- [Resource packages](resource-packages.md): the `items.<db>.<name>.dat` format.
- [Deploy and Monitoring APIs](deploy-api.md): the SitecoreAI services behind the `cloud` commands, their login and the source upload.

To capture fresh evidence from a CM:

```bash
tern --record ./capture env info
```

Each request becomes a numbered text file with tokens, secrets and blob contents removed.
