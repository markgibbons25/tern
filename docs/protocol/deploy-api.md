# SitecoreAI Deploy and Monitoring APIs

The `cloud` commands talk to two SitecoreAI services, never to a CM's Management Services. What follows was confirmed on 2026-10-20 against a SitecoreAI organisation with read-only calls, comparing Tern's output with the Official CLI 6.0.23 (XMCloud plugin 1.1.120) run on the same login. Write calls were not sent to SitecoreAI; their request shapes are covered by `tests/Tern.Core.Tests/Cloud`.

## Login

- `cloud login` uses the device flow (or client credentials) against `https://auth.sitecorecloud.io/` with audience `https://api.sitecorecloud.io` and the public client `Chi8EwfFnEejksk3Sed9hlalGiM9B2v7`.
- The login is stored as the endpoint `XmCloud`. The Official CLI's serializer camel-cases endpoint names and variable names, so the file holds `xmCloud` and `variables.xmCloudMonitoringHost`. Names are matched ignoring case.
- `host` is the Deploy API. When the access token carries `https://auth.sitecorecloud.io/claims/org_id`, `refreshTokenParameters` is `{"organization_id": <org>}`, and refreshes send it.
- `cloud environment connect` adds an endpoint named after the environment: `ref: "XmCloud"`, `allowWrite`, `host: https://<cm host>/` (always with the trailing slash), and an empty `variables` object.
- `cloud logout` removes `XmCloud` and every endpoint whose `ref` names it. The logout page is `<authority>v2/logout`.

## Transport

- Every call sends `Authorization: Bearer <token>` from the `XmCloud` endpoint. The CM health check sends the same token to the CM.
- A network error, timeout, 401 or 429 is retried three times after 2, 4 and 8 seconds. Tern gets a new token before retrying a 401.
- Create calls answer with a `Location` header. The ID of what was created is its last segment.
- Errors are RFC 7807 problem documents (`title`, `detail`, `instance`). A 400 may carry `errors: {field: [messages]}`. A 409 on a deployment names the running deployment in `instance`.
- `GET /health/components` on the Deploy API returns `{status, entries: {name: {status, description}}}`. The Official CLI checks it alongside every command and prints the entries when it is not `Healthy`.

## Deploy API (`https://xmclouddeploy-api.sitecorecloud.io`)

| Command | Request |
|---|---|
| `project list` / `info` | `GET /api/projects/v1`, `GET /api/projects/v1/{id}` |
| `project create` / `update` / `delete` | `POST /api/projects/v1 {name}`, `PUT /api/projects/v1/{id} {name}`, `DELETE /api/projects/v1/{id}` |
| `environment list` / `info` | `GET /api/projects/v2/{projectId}/environments`, `GET /api/environments/v2/{id}` |
| `environment create` | `POST /api/projects/v2/{projectId}/environments {name, tenantType, type}` |
| `editinghost create` | the same, with `type: "eh"`, the CM's tenant type and `EditingHostEnvironmentDetails: {CmEnvironmentId}` |
| `environment update` / `delete` | `PUT /api/environments/v1/{id} {name}`, `DELETE /api/environments/v1/{id}` |
| `environment restart` | `POST /api/environments/v1/{id}/restart`, then `GET` the same path until `restartStatus` leaves 1 (in progress) |
| `environment promote` | `POST /api/environments/v2/{id}/promote/{deploymentId} {environmentId, deploymentId}` |
| `environment health` | `GET https://<cm host>/healthz/ready`, whose body is the status text |
| `environment variable list` | `GET /api/environments/v1/{id}/variables` |
| `environment variable upsert` / `delete` | `POST /api/environments/v1/{id}/variables/{name} {secret, value, target}`, `DELETE` the same path |
| `deployment create` | `POST /api/environments/v2/{envId}/deployments {environmentId}` |
| upload | `POST /api/deployments/v2/{id}/source` with the zip as `application/octet-stream` |
| `deployment start` / `cancel` | `POST /api/deployments/v1/{id}/deploy`, `POST /api/deployments/v1/{id}/cancel` |
| `deployment info` / `list` / `watch` | `GET /api/deployments/v3/{id}` (polled every 10 s by `watch`), `GET /api/environments/v2/{envId}/deployments` |
| `organization info` / `health` | `GET /api/organizations/v4/{orgId}`, `GET /api/organizations/v4/{orgId}/health` |
| `organization license` | `GET /api/organizations/v1/{orgId}/license`, saved as `license.xml` next to `sitecore.json` |

- `tenantType` is sent as a number: 0 for non-production, 1 for production. `type` is `null`, `"cm"` or `"eh"`.
- Task statuses are numbers: 0 NotStarted, 1 InProgress, 2 Complete, 3 Failed, 4 Skipped. An environment's `tenantType` comes back as `prod` or `nonprod`.
- Responses carry more fields than the Official CLI shows. Its `--json` output keeps only the fields it models, and Tern's payloads keep the same ones.

## Monitoring API (`https://xmcloud-monitoring-api.sitecorecloud.io`)

| Command | Request |
|---|---|
| `environment log list` | `GET /api/logs/v1/{envId}?latest=True\|False`. The order of the files changes from call to call. |
| `environment log view` / `download` | `GET /api/logs/v2/{envId}/{file}?download=False\|True` |
| `deployment log` | `GET /api/Deployments/v1/{deploymentId}`, expected to answer with a `logs` list of `logTime`, `logLevel`, `logMessage` and `stage` (not yet confirmed live; fields are matched ignoring case) |

The booleans in these query strings are written as .NET prints them, `True` and `False`.

## Output

With `--json`, Tern prints the Official CLI's own payload, unwrapped: the same root (array, object, string or `null` for commands that return nothing), the same field names and casing, including the PascalCase of `organization info` and `log list`, and the same value types, such as numeric deployment statuses but a named `provisioningStatus` for an environment. Commands the Official CLI gives no `--json` (`login`, `logout`, `connect`, `disconnect`, `variable upsert`/`delete`, `log download`/`view`, `deployment cancel`) print a small bare object of Tern's own. For the read commands, human output matches the Official CLI's tables line for line, with dates in the current culture. The messages of the write commands are Tern's own wording.

## Source upload

The Official CLI zips the working folder by asking libgit2 for its status, and runs `git init` (then deletes `.git`) when the folder is not a repository. Tern never runs git. It reads every `.gitignore` at or below the folder, `.git/info/exclude`, the user's global ignore file (`core.excludesFile`, or `~/.config/git/ignore`) and, at a repository root, the index (so tracked files are kept even when a rule matches them), and zips what they do not ignore, with `/`-separated entry names.
