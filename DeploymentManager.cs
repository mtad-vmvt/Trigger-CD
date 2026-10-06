namespace App;

public record DeploymentStatus(Guid DeploymentId, string Status, string? Message = null);
public record DeploymentResult(bool Succeeded, string? Message = null);

public interface IDeploymentRunner {
	Task<DeploymentResult> Run(CfgApp app, long? artifactId, bool force, int waitTime, int delay, CancellationToken cancellationToken);
}

public class DeploymentJob(string app) {
	private DeploymentStatus _status = new(Guid.NewGuid(), "pending");
	public string App { get; } = app;
	public DateTime LockedUntil { get; init; }
	public DeploymentStatus Status => Volatile.Read(ref _status);
	public Task Completion { get; internal set; } = Task.CompletedTask;
	internal void Complete(DeploymentResult result) => Volatile.Write(ref _status,
		new(Status.DeploymentId, result.Succeeded ? "succeeded" : "failed", result.Message));
}

public class DeploymentManager(IDeploymentRunner runner, CancellationToken stoppingToken = default) {
	private readonly object _sync = new();
	// Keep the latest operation per application, independently of configuration reloads.
	private readonly Dictionary<string, DeploymentJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

	public DeploymentJob? Find(string app, Guid id) {
		lock (_sync) {
			return _jobs.TryGetValue(app, out var job) && job.Status.DeploymentId == id ? job : null;
		}
	}

	public DeploymentJob? Start(CfgApp app, long? artifactId, bool force, int waitTime, int delay, int lockSeconds, out string? conflict) {
		lock (_sync) {
			if (_jobs.TryGetValue(app.Name!, out var previous)) {
				if (previous.Status.Status == "pending") { conflict = "Running"; return null; }
				if (previous.LockedUntil > DateTime.UtcNow) { conflict = "Locked"; return null; }
			}
			var job = new DeploymentJob(app.Name!) { LockedUntil = DateTime.UtcNow.AddSeconds(lockSeconds) };
			_jobs[app.Name!] = job;
			job.Completion = Task.Run(() => Complete(job, app, artifactId, force, waitTime, delay));
			conflict = null;
			return job;
		}
	}

	private async Task Complete(DeploymentJob job, CfgApp app, long? artifactId, bool force, int waitTime, int delay) {
		try {
			job.Complete(await runner.Run(app, artifactId, force, waitTime, delay, stoppingToken));
		}
		catch (Exception) {
			job.Complete(new(false, "Deployment execution failed."));
		}
	}
}
