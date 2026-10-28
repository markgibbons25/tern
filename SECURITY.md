# Security policy

## Supported versions

Only the latest release of Tern gets security fixes. Update with `dotnet tool update --global Tern.Cli`, or download the latest binary from [Releases](https://github.com/markgibbons25/tern/releases).

## Reporting a vulnerability

Please don't open a public issue. Report it privately through GitHub instead: on the [Security tab](https://github.com/markgibbons25/tern/security), choose **Report a vulnerability**.

Include what an attacker could do, the Tern version (`tern --version`), and the steps or files that reproduce it. You'll get a reply within a week, and a fix for a confirmed issue ships in a new release, credited to you in its advisory unless you'd rather not be named.

## What's in scope

Tern holds credentials for Sitecore environments and writes Item files and content, so these matter most:

- Leaking a client secret, access token or refresh token, for example into logs, `--json` output, `--record` files or error messages.
- Sending a token to a host other than the endpoint or identity provider it was issued for.
- Writing outside the project folder from content a CM or a package supplies (path traversal).
- Writing to an endpoint marked read-only, or carrying out a push without the user asking for one.

Issues in Sitecore itself, in the Official CLI or in a CM's configuration should go to Sitecore.

## How the repository is checked

Every push and pull request runs CodeQL code scanning, and pull requests run dependency review. Dependabot raises updates for NuGet packages and actions every week, and secret scanning is on.
