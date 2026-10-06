using System.IO.Compression;
using System.Net;

namespace App;

public class DeploymentException(string message) : Exception(message);

public class DeploymentRunner : IDeploymentRunner {
	private readonly HttpClient _health;
	private readonly Func<string, string, Log, bool> _command;

	public DeploymentRunner(HttpClient? health = null, Func<string, string, Log, bool>? command = null) {
		_health = health ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
		_command = command ?? Extensions.ShellExec;
	}

	public static string? Validate(CfgApp app, bool verified) {
		if (string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.Path) || app.Repo is null)
			return "Application deployment configuration is incomplete.";
		if (verified && !string.IsNullOrEmpty(app.Service) && string.IsNullOrWhiteSpace(app.HealthCheckUrl))
			return "HealthCheckUrl is required for service deployments.";
		if (app.HealthCheckUrl is not null) {
			if (!Uri.TryCreate(app.HealthCheckUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
				return "HealthCheckUrl must be an absolute HTTP or HTTPS URL.";
			if (app.HealthCheckTimeoutSeconds <= 0 || app.HealthCheckIntervalMilliseconds <= 0)
				return "Health check timeout and interval must be positive.";
		}
		return null;
	}

	public async Task<DeploymentResult> Run(CfgApp app, long? artifactId, bool force, int waitTime, int delay, CancellationToken cancellationToken) {
		var log = new Log(app.Name!);
		var stage = "Artifact download";
		log.Print("Trigger", "Start");
		try {
			await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
			var artifact = await app.Repo!.Download(log, artifactId, force, waitTime, cancellationToken);
			if (artifact is not null) {
				if (!string.IsNullOrEmpty(app.Service)) Execute("Service stop", "/usr/bin/sudo", $"systemctl stop {app.Service}", log);
				stage = "File installation";
				if (app.Clean ?? false) Files.CleanDir(app.Path!, true, log, app.SkipClean);
				ZipFile.ExtractToDirectory($"data/files/{app.Repo.App}.zip", app.Path!, true);
				Execute("File ownership", "/usr/bin/chown", $"-R {app.ChOwn} {app.Path}", log);
				Execute("File permissions", "/usr/bin/chmod", $"-R {app.ChMod} {app.Path}", log);
				if (!string.IsNullOrEmpty(app.Script)) Execute("Deployment script", "/bin/bash", $"-c \"{app.Script.Replace("\"", "\\\"")}\"", log);
				if (!string.IsNullOrEmpty(app.Service)) Execute("Service start", "/usr/bin/sudo", $"systemctl start {app.Service}", log);
			}
			stage = "Readiness check";
			if (app.HealthCheckUrl is not null) await WaitForHealth(app, cancellationToken);
			if (artifact is not null) {
				stage = "Deployment version recording";
				var version = Version.Get(app.Repo!.App!);
				version.Id = artifact.Id;
				version.Date = artifact.Created_at;
				version.Url = artifact.Download;
				(version.Log ??= []).Add($"{artifact.Created_at:yyyy-MM-ddTHH:mm:ssZ}|{artifact.Id}|{artifact.Size_in_bytes}");
				version.Save();
			}
			log.Print("Trigger", "Succeeded");
			return new(true);
		}
		catch (DeploymentException ex) {
			log.Print("Error", ex.Message);
			return new(false, ex.Message);
		}
		catch (OperationCanceledException) {
			log.Print("Error", "Deployment was interrupted.");
			return new(false, "Deployment was interrupted.");
		}
		catch (Exception ex) {
			log.Print("Error", new { Stage = stage, Type = ex.GetType().Name });
			return new(false, $"{stage} failed.");
		}
	}

	private void Execute(string stage, string file, string arguments, Log log) {
		if (!_command(file, arguments, log)) throw new DeploymentException($"{stage} failed.");
	}

	private async Task WaitForHealth(CfgApp app, CancellationToken cancellationToken) {
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(app.HealthCheckTimeoutSeconds));
		try {
			while (true) {
				try {
					using var response = await _health.GetAsync(app.HealthCheckUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
					if (response.StatusCode == HttpStatusCode.OK) return;
				}
				catch (HttpRequestException) { }
				await Task.Delay(app.HealthCheckIntervalMilliseconds, deadline.Token);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			throw new DeploymentException("Application did not become ready before the health check deadline.");
		}
	}
}
