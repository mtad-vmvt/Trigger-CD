# Trigger-CD

Trigger-CD downloads a GitHub Actions artifact, installs its files, runs the configured deployment commands, and verifies application readiness before recording a successful version.

## Verified deployment protocol

The existing application name and key in `/{app}/{key}` authenticate both operations. Keep trigger keys and GitHub tokens in the server configuration; do not commit them.

1. Request `GET /{app}/{key}?operation=start&artifactId=<artifact-id>`. Use the `artifact-id` output of the originating `actions/upload-artifact` step. The artifact must belong to the configured repository and match `Repo.Name`. This operation never selects the latest artifact or skips an explicitly requested artifact because of an older version record.
2. HTTP 202 returns `{ "deploymentId": "<guid>", "status": "pending" }`.
3. Poll `GET /{app}/{key}?operation=status&deploymentId=<guid>`. HTTP 200 returns `status` equal to `pending`, `succeeded`, or `failed`. A failed result also includes a safe `message`. Fail the calling workflow on `failed`, any unexpected/non-success response, or its own polling deadline.

Invalid inputs/configuration return HTTP 400, invalid keys return 401, unknown applications/deployments return 404, and an application already running or locked returns 409. Status is scoped to the authenticated application. The latest operation per application is held in memory: restarting Trigger-CD or replacing that operation makes the previous ID unknown, rather than claiming success.

`succeeded` means the selected artifact was downloaded and installed, every configured command succeeded, the configured health endpoint returned HTTP 200, and the deployed version was saved. Static applications without a service or health URL complete after installation and commands. File, command, download, and readiness failures stop the operation and preserve the previous deployed version record.

## Application readiness configuration

For verified deployments with a `Service`, add these fields to that application's existing entry in the server's `Apps` configuration:

```json
{
  "HealthCheckUrl": "http://127.0.0.1:<actual-api-port>/api/health",
  "HealthCheckTimeoutSeconds": 60,
  "HealthCheckIntervalMilliseconds": 1000
}
```

Replace the port with the application's actual local listener. The local URL must address the deployed instance, avoiding a load balancer that could answer from another instance. An absent service health URL fails before verified deployment starts. HTTP failures and connection refusal are retried until the readiness deadline. Redirects are not followed and TLS verification is not disabled.

For OKIS, `/api/health` is the application health endpoint. `/external-api/System/health` also requires an API key and is a different endpoint. OKIS completes both startup migration calls before it serves `/api/health`; a migration startup failure therefore cannot pass this check.

The legacy request without `operation` now waits for deployment completion and returns literal `Ok` only on success. Command/download/install failures return HTTP 500. It uses the previous latest-artifact selection and performs health checking when a health URL is configured. Use the verified protocol for Actions deployments so long deployments do not depend on a single proxy HTTP timeout.

## Build, test, and installation

```sh
dotnet restore Trigger-CD.sln --disable-parallel
dotnet build Trigger-CD.sln --configuration Release --no-restore -m:1
dotnet test Trigger-CD.Tests/Trigger-CD.Tests.csproj --configuration Release --no-build --no-restore
```

CI publishes the self-contained Linux executable as `trigger-cd-linux-x64`. Install that reviewed artifact externally, preserving the server's `appsettings.json`, `data` directory, and service configuration. Deployment of Trigger-CD itself is not automated: using its ordinary service deployment flow to stop `trigger_cd.service` would stop the process before it can finish its own installation.

Deploy the updated Trigger-CD and configure the local health URL before using a workflow that requires the new protocol. Older handlers return plain `Ok`; callers must reject that response when they require a deployment ID.
