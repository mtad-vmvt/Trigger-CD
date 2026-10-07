using App;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(true);
var app = builder.Build();

var cfg = new Config(app);

if (!Directory.Exists("data")) { Directory.CreateDirectory("data"); }
if (!Directory.Exists("data/files")) { Directory.CreateDirectory("data/files"); }

app.MapGet("/{app}/{key?}", async (HttpContext ctx, string app, string? key, string? force=null, string? artifactId=null) => {
	long? requestedArtifact = null;
	if (artifactId is not null) {
		if (!long.TryParse(artifactId, out var id) || id <= 0) { ctx.Response.StatusCode = 400; return; }
		requestedArtifact = id;
	}
	CfgApp cfgApp;
	lock (cfg) {
		if (!cfg.Apps.TryGetValue(app.ToLower(), out cfgApp!)) { ctx.Response.StatusCode = 404; return; }
		if (string.IsNullOrWhiteSpace(cfgApp.Key)) { ctx.Response.StatusCode = 400; return; }
		if (cfgApp.Key != key) { ctx.Response.StatusCode = 401; return; }
		if (cfgApp.Path is null || cfgApp.Repo is null ||
			(requestedArtifact.HasValue && string.IsNullOrWhiteSpace(cfgApp.HealthCheckUrl)) ||
			(cfgApp.HealthCheckUrl is not null && (cfgApp.HealthCheckTimeoutSeconds <= 0 || cfgApp.HealthCheckIntervalMilliseconds <= 0))) {
			ctx.Response.StatusCode = 400;
			return;
		}
		if (cfgApp.Running > DateTime.UtcNow || cfgApp.Lock > DateTime.UtcNow) { ctx.Response.StatusCode = 409; return; }
		cfgApp.Running = DateTime.MaxValue;
		cfgApp.Lock = DateTime.UtcNow.AddSeconds(cfg.Lock);
	}
	try {
		var log = new Log(cfgApp.Name!);
		var rp = cfgApp.Repo;
		log.Print("Trigger", "Start");
		await Task.Delay(cfg.Delay * 1000);
		if (await rp.Download(cfgApp.Path, log, (force == "" || force?.ToLower() == "true"), cfg.WaitTime, cfgApp.Debug ?? false, requestedArtifact)) {
			if (!string.IsNullOrEmpty(cfgApp.Service)) RunCommand("Service stop", "/usr/bin/sudo", $"systemctl stop {cfgApp.Service}", log);
			if (cfgApp.Clean??false) Files.CleanDir(cfgApp.Path, true, log, cfgApp.SkipClean);
			ZipFile.ExtractToDirectory($"data/files/{rp.App}.zip", cfgApp.Path, true);
			RunCommand("File ownership", "/usr/bin/chown", $"-R {cfgApp.ChOwn} {cfgApp.Path}", log);
			RunCommand("File permissions", "/usr/bin/chmod", $"-R {cfgApp.ChMod} {cfgApp.Path}", log);
			if (!string.IsNullOrEmpty(cfgApp.Script)) RunCommand("Deployment script", "/bin/bash", $"-c \"{cfgApp.Script.Replace("\"", "\\\"")}\"", log);
			if (!string.IsNullOrEmpty(cfgApp.Service)) RunCommand("Service start", "/usr/bin/sudo", $"systemctl start {cfgApp.Service}", log);
		}
		if (cfgApp.HealthCheckUrl is not null) await WaitForHealth(cfgApp);
		if (rp.PendingArtifact is { } artifact) {
			var version = App.Version.Get(rp.App!);
			version.Id = artifact.Id; version.Date = artifact.Created_at; version.Url = artifact.Download;
			(version.Log ??= []).Add($"{artifact.Created_at:yyyy-MM-ddTHH:mm:ssZ}|{artifact.Id}|{artifact.Size_in_bytes}");
			version.Save();
		}
		log.Print("Trigger", "Done");
		await ctx.Response.WriteAsync(requestedArtifact.HasValue ? $"Deployed:{requestedArtifact.Value}" : "Ok");
	}
	catch (Exception ex) {
		Console.Error.WriteLine($"Deployment failed for {app}: {ex.Message}");
		ctx.Response.StatusCode = 500;
		await ctx.Response.WriteAsync("Deployment failed.");
	}
	finally { lock (cfg) { cfgApp.Running = DateTime.UtcNow; } }
});

app.Run();

static void RunCommand(string stage, string file, string arguments, Log log) {
	if (!Extensions.ShellExec(file, arguments, log)) throw new InvalidOperationException($"{stage} failed.");
}

static async Task WaitForHealth(CfgApp cfgApp) {
	using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
	using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(cfgApp.HealthCheckTimeoutSeconds));
	while (true) {
		try {
			using var response = await client.GetAsync(cfgApp.HealthCheckUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
			if (response.StatusCode == System.Net.HttpStatusCode.OK) return;
		}
		catch (HttpRequestException) { }
		await Task.Delay(cfgApp.HealthCheckIntervalMilliseconds, deadline.Token);
	}
}
