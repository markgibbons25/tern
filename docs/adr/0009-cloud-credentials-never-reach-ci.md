# Cloud credentials never reach CI

The SitecoreAI environments Tern is tested against are not owned by the project, so their credentials never become GitHub secrets.

- **GitHub Actions** runs unit tests, golden-file tests and the Native AOT build on Windows, Linux and macOS.
- **Differential and integration tests** run on a developer machine, against a Local CM and a SitecoreAI test environment. They read credentials from a gitignored `.env.local`.
- **Every test that writes** checks the target against a host allowlist before sending any request. On a cloud CM, writes are confined to a scratch root.

Revisit this if the project gets a SitecoreAI environment of its own, or a self-hosted runner that can keep the credentials.
