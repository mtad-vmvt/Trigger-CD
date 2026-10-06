namespace App;

public static class DeploymentEndpoints {
	public static void Map(WebApplication app, Config cfg, DeploymentManager deployments) {
		app.MapGet("/{app}/{key?}", async (string app, string? key, string? force, string? operation, string? artifactId, string? deploymentId) => {
			if (!cfg.Apps.TryGetValue(app, out var cfgApp)) return Results.NotFound();
			if (string.IsNullOrWhiteSpace(cfgApp.Key)) return Results.BadRequest(new { message = "Application authentication key is not configured." });
			if (cfgApp.Key != key) return Results.Unauthorized();

			if (operation == "status") {
				if (!Guid.TryParse(deploymentId, out var id)) return Results.BadRequest(new { message = "A valid deploymentId is required." });
				var existing = deployments.Find(cfgApp.Name!, id);
				return existing is null ? Results.NotFound() : Results.Json(existing.Status);
			}
			if (operation is not null && operation != "start") return Results.BadRequest(new { message = "Unknown operation." });
			long? requestedArtifact = null;
			if (operation == "start") {
				if (!long.TryParse(artifactId, out var id) || id <= 0) return Results.BadRequest(new { message = "A positive artifactId is required." });
				requestedArtifact = id;
			}
			else if (artifactId is not null || deploymentId is not null) {
				return Results.BadRequest(new { message = "Specify operation=start or operation=status." });
			}
			var validation = DeploymentRunner.Validate(cfgApp, operation == "start");
			if (validation is not null) return Results.BadRequest(new { message = validation });
			var job = deployments.Start(cfgApp, requestedArtifact, force == "" || string.Equals(force, "true", StringComparison.OrdinalIgnoreCase),
				cfg.WaitTime, cfg.Delay, cfg.Lock, out var conflict);
			if (job is null) {
				return operation == "start"
					? Results.Json(new { message = conflict }, statusCode: StatusCodes.Status409Conflict)
					: Results.Text(conflict);
			}
			if (operation == "start") return Results.Json(new DeploymentStatus(job.Status.DeploymentId, "pending"), statusCode: StatusCodes.Status202Accepted);
			await job.Completion;
			return job.Status.Status == "succeeded"
				? Results.Text("Ok")
				: Results.Json(new { message = job.Status.Message }, statusCode: StatusCodes.Status500InternalServerError);
		});
	}
}
