namespace App;

public static class DeploymentEndpoints {
	public static void Map(WebApplication server, Config cfg, IDeploymentRunner runner) {
		var gate = new DeploymentGate();
		server.MapGet("/{app}/{key?}", async (string app, string? key, string? force, string? artifactId, string? operation, string? deploymentId) => {
			if (!cfg.Apps.TryGetValue(app, out var cfgApp)) return Results.NotFound();
			if (string.IsNullOrWhiteSpace(cfgApp.Key)) return Results.BadRequest(new { message = "Application authentication key is not configured." });
			if (cfgApp.Key != key) return Results.Unauthorized();
			if (operation is not null || deploymentId is not null)
				return Results.BadRequest(new { message = "Deployment operations are not supported; use artifactId on the existing trigger request." });
			long? requestedArtifact = null;
			if (artifactId is not null) {
				if (!long.TryParse(artifactId, out var id) || id <= 0) return Results.BadRequest(new { message = "A positive artifactId is required." });
				requestedArtifact = id;
			}
			var validation = DeploymentRunner.Validate(cfgApp, requestedArtifact.HasValue);
			if (validation is not null) return Results.BadRequest(new { message = validation });
			if (!gate.TryEnter(cfgApp.Name!, cfg.Lock, out var conflict))
				return Results.Json(new { message = conflict }, statusCode: StatusCodes.Status409Conflict);
			try {
				var result = await runner.Run(cfgApp, requestedArtifact,
					force == "" || string.Equals(force, "true", StringComparison.OrdinalIgnoreCase),
					cfg.WaitTime, cfg.Delay, server.Lifetime.ApplicationStopping);
				return result.Succeeded
					? Results.Text(requestedArtifact.HasValue ? $"Deployed:{requestedArtifact.Value}" : "Ok")
					: Results.Json(new { message = result.Message }, statusCode: StatusCodes.Status500InternalServerError);
			}
			catch (Exception) {
				return Results.Json(new { message = "Deployment execution failed." }, statusCode: StatusCodes.Status500InternalServerError);
			}
			finally { gate.Exit(cfgApp.Name!); }
		});
	}
}
