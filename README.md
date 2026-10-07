# Trigger-CD

The existing `GET /{app}/{key}` waits for deployment completion. It downloads and installs the artifact, checks every deployment command, verifies configured application health, and then saves the deployed version. Failures return HTTP 500; success returns `Ok`.

For an exact GitHub Actions build, call `GET /{app}/{key}?artifactId=<artifact-id>` using the upload step's artifact ID. The artifact must belong to the configured repository and match `Repo.Name`. After successful installation and readiness verification, the response is HTTP 200 with exactly `Deployed:<artifact-id>`. Callers must require that exact response; an older Trigger-CD returning plain `Ok` does not prove deployment completion.

Invalid input/configuration returns 400, an invalid key returns 401, an unknown application returns 404, and an application already deploying or in its configured cooldown returns 409. Requests using the removed `operation` or `deploymentId` parameters return 400.

## Application health

Exact-artifact requests for a configured `Service` require a health URL before deployment starts:

```json
{
  "HealthCheckUrl": "http://127.0.0.1:<actual-api-port>/api/health",
  "HealthCheckTimeoutSeconds": 60,
  "HealthCheckIntervalMilliseconds": 1000
}
```

Use the deployed instance's actual local listener, not a load balancer that could answer from another instance. HTTP failures and connection refusal are retried until the deadline; redirects are not followed. OKIS completes its startup migrations before serving `/api/health`. Legacy requests perform health verification when configured; static applications without a service or health URL complete after installation and commands.

Build and run the local regression tests:

```sh
dotnet restore Trigger-CD.sln --disable-parallel
dotnet build Trigger-CD.sln --configuration Release --no-restore -m:1
dotnet test Trigger-CD.Tests/Trigger-CD.Tests.csproj --configuration Release --no-build --no-restore
```

Install the updated Trigger-CD and configure application health before enabling exact-artifact callers. A client or proxy timeout must fail the calling workflow; the server deployment may continue, so do not automatically retry an ambiguous timeout. Keep authentication keys and GitHub tokens in server configuration, and preserve `appsettings.json` and `data` during installation.
