using App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;

namespace TriggerCD.Tests;

[TestClass]
[DoNotParallelize]
public class DeploymentTests {
	private string _originalDirectory = null!;
	private string _directory = null!;

	[TestInitialize]
	public void Initialize() {
		_originalDirectory = Directory.GetCurrentDirectory();
		_directory = Path.Combine(Path.GetTempPath(), "trigger-cd-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_directory, "data", "files"));
		Directory.SetCurrentDirectory(_directory);
	}

	[TestCleanup]
	public void Cleanup() {
		Directory.SetCurrentDirectory(_originalDirectory);
		var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trigger-cd-tests")) + Path.DirectorySeparatorChar;
		Assert.StartsWith(expectedRoot, Path.GetFullPath(_directory));
		Directory.Delete(_directory, true);
	}

	[TestMethod]
	public async Task FailedStopCommandStopsInstallationAndDoesNotRecordVersion() {
		var commands = new List<string>();
		var healthCalls = 0;
		using var health = new HttpClient(new Handler(_ => { healthCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
		var runner = new DeploymentRunner(health, (_, args, _) => { commands.Add(args); return false; });
		var app = CreateApp();
		var result = await runner.Run(app, 44, false, 0, 0, default);
		Assert.IsFalse(result.Succeeded);
		Assert.AreEqual("Service stop failed.", result.Message);
		CollectionAssert.AreEqual(new[] { "systemctl stop web_okis_dev" }, commands);
		Assert.IsFalse(Directory.Exists(app.Path));
		Assert.IsFalse(File.Exists("data/api.json"));
		Assert.AreEqual(0, healthCalls);
	}

	[TestMethod]
	public async Task FailedScriptDoesNotStartServiceOrRecordVersion() {
		var commands = new List<string>();
		var app = CreateApp();
		app.Script = "exit 7";
		var runner = new DeploymentRunner(command: (file, args, _) => { commands.Add(args); return file != "/bin/bash"; });
		var result = await runner.Run(app, 44, false, 0, 0, default);
		Assert.IsFalse(result.Succeeded);
		Assert.AreEqual("Deployment script failed.", result.Message);
		Assert.IsFalse(commands.Contains("systemctl start web_okis_dev"));
		Assert.IsFalse(File.Exists("data/api.json"));
	}

	[TestMethod]
	public async Task UnhealthyApiFailsWithoutRecordingVersion() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var health = new HttpClient(new Handler(_ => {
			entered.TrySetResult();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		}));
		var app = CreateApp();
		app.HealthCheckTimeoutSeconds = 1;
		var deployment = new DeploymentRunner(health, (_, _, _) => true).Run(app, 44, false, 0, 0, default);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(deployment.IsCompleted);
		Assert.IsFalse(File.Exists("data/api.json"));
		var result = await deployment.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(result.Succeeded);
		Assert.AreEqual("Application did not become ready before the health check deadline.", result.Message);
		Assert.IsFalse(File.Exists("data/api.json"));
	}

	[TestMethod]
	public async Task SuccessAndVersionRecordingWaitForApiReadiness() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		using var health = new HttpClient(new Handler(async _ => {
			if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("Connection refused");
			entered.TrySetResult();
			await ready.Task;
			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		var app = CreateApp();
		var deployment = new DeploymentRunner(health, (_, _, _) => true).Run(app, 44, false, 0, 0, default);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(deployment.IsCompleted);
		Assert.IsFalse(File.Exists("data/api.json"));
		Assert.AreEqual("deployed", File.ReadAllText(Path.Combine(app.Path!, "application.txt")));
		ready.SetResult();
		Assert.IsTrue((await deployment.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
		Assert.AreEqual(44L, App.Version.Get("api").Id);
		Assert.AreEqual(2, attempts);
	}

	[TestMethod]
	public async Task ExactArtifactDoesNotUseLatestOrSkipAlreadyRecordedArtifact() {
		new App.Version { Repo = "api", Id = 99 }.Save();
		var requests = new List<string>();
		var app = CreateApp(requests);
		var artifact = await app.Repo!.Download(new Log("api"), 44);
		Assert.AreEqual(44L, artifact!.Id);
		CollectionAssert.AreEqual(new[] {
			"/repos/org/repo/actions/artifacts/44",
			"/repos/org/repo/actions/artifacts/44/zip"
		}, requests);
		Assert.AreEqual(99L, App.Version.Get("api").Id);
	}

	[TestMethod]
	public async Task WrongArtifactIdFailsWithoutLatestFallbackOrArchiveDownload() {
		var requests = new List<string>();
		var app = CreateApp(requests);
		await Assert.ThrowsAsync<DeploymentException>(() => app.Repo!.Download(new Log("api"), 55));
		CollectionAssert.AreEqual(new[] { "/repos/org/repo/actions/artifacts/55" }, requests);
		Assert.IsFalse(File.Exists("data/api.json"));
	}

	[TestMethod]
	public void ActualCommandNonzeroExitIsReportedAsFailure() {
		var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
		var arguments = OperatingSystem.IsWindows() ? "/c exit /b 7" : "-c \"exit 7\"";
		Assert.IsFalse(Extensions.ShellExec(executable, arguments, new Log("api")));
	}

	[TestMethod]
	public async Task ExactArtifactRequestWaitsThenReturnsTheCompletionMarker() {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		var request = client.GetAsync("/api/key?artifactId=44");
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(request.IsCompleted);
		Assert.AreEqual(44L, runner.ArtifactId);
		runner.Finish.SetResult(new(true));
		var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual("Deployed:44", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task FailedDeploymentReturnsHttp500WithoutASuccessMarker() {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		var request = client.GetAsync("/api/key?artifactId=44");
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		runner.Finish.SetResult(new(false, "Service start failed."));
		var response = await request;
		Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
		StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Service start failed.");
	}

	[TestMethod]
	public async Task LegacyRequestWaitsThenReturnsOk() {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner, health: false);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		var request = client.GetAsync("/api/key");
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(request.IsCompleted);
		Assert.IsNull(runner.ArtifactId);
		runner.Finish.SetResult(new(true));
		var response = await request;
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual("Ok", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task AuthenticationAndOverlapChecksSurviveConfigurationReload() {
		var runner = new PendingRunner();
		var (server, client, cfg) = await StartApi(runner);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/wrong?artifactId=44")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/missing/key?artifactId=44")).StatusCode);
		var first = client.GetAsync("/api/key?artifactId=44");
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cfg.Reload();
		var overlapping = await client.GetAsync("/API/key?artifactId=45");
		Assert.AreEqual(HttpStatusCode.Conflict, overlapping.StatusCode);
		StringAssert.Contains(await overlapping.Content.ReadAsStringAsync(), "Running");
		Assert.AreEqual(1, runner.Calls);
		runner.Finish.SetResult(new(true));
		Assert.AreEqual("Deployed:44", await (await first).Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task CooldownSurvivesConfigurationReload() {
		var runner = new PendingRunner();
		runner.Finish.SetResult(new(true));
		var (server, client, cfg) = await StartApi(runner, lockSeconds: 30);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/key?artifactId=44")).StatusCode);
		cfg.Reload();
		var blocked = await client.GetAsync("/api/key?artifactId=45");
		Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
		StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "Locked");
		Assert.AreEqual(1, runner.Calls);
	}

	[TestMethod]
	public async Task ExactServiceDeploymentRequiresHealthConfiguration() {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner, health: false);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/key?artifactId=44")).StatusCode);
		Assert.IsFalse(runner.Entered.Task.IsCompleted);
	}

	[TestMethod]
	[DataRow("artifactId=0")]
	[DataRow("artifactId=-1")]
	[DataRow("artifactId=invalid")]
	[DataRow("artifactId=")]
	[DataRow("operation=start&artifactId=44")]
	[DataRow("operation=status&deploymentId=b1b1fb08-6b6c-440c-8c4c-8c92b1d83bf9")]
	public async Task InvalidArtifactAndObsoleteOperationsDoNotDeploy(string query) {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/key?" + query)).StatusCode);
		Assert.IsFalse(runner.Entered.Task.IsCompleted);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow(" ")]
	public async Task MissingConfiguredKeyRejectsRequests(string? appKey) {
		var runner = new PendingRunner();
		var (server, client, _) = await StartApi(runner, appKey: appKey);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		foreach (var query in new[] { "", "?artifactId=44" }) {
			var response = await client.GetAsync("/api" + query);
			Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
			StringAssert.Contains(await response.Content.ReadAsStringAsync(), "authentication key is not configured");
		}
		Assert.IsFalse(runner.Entered.Task.IsCompleted);
	}

	[TestMethod]
	public void VersionRecordingAtomicallyReplacesThePreviousRecord() {
		new App.Version { Repo = "api", Id = 43 }.Save();
		new App.Version { Repo = "api", Id = 44 }.Save();
		Assert.AreEqual(44L, App.Version.Get("api").Id);
		Assert.AreEqual(0, Directory.GetFiles("data", "*.tmp").Length);
	}

	[TestMethod]
	public void FailedTemporaryWritePreservesThePreviousVersionRecord() {
		var name = new string('v', 243);
		var path = $"data/{name}.json";
		var previous = $"{{\"Repo\":\"{name}\",\"Id\":43}}";
		File.WriteAllText(path, previous);
		Assert.Throws<IOException>(() => new App.Version { Repo = name, Id = 44 }.Save());
		Assert.AreEqual(previous, File.ReadAllText(path));
		Assert.AreEqual(43L, App.Version.Get(name).Id);
		Assert.AreEqual(0, Directory.GetFiles("data", "*.tmp").Length);
	}

	[TestMethod]
	public void CleaningPreservesAnEmptyNestedDirectoryNamedInSkipClean() {
		var target = Path.Combine(_directory, "installed");
		var preserved = Path.Combine(target, "parent", "preserved");
		Directory.CreateDirectory(preserved);
		File.WriteAllText(Path.Combine(target, "parent", "obsolete.txt"), "obsolete");
		Files.CleanDir(target, true, new Log("api"), ["parent/preserved"]);
		Assert.IsTrue(Directory.Exists(preserved));
		Assert.IsFalse(File.Exists(Path.Combine(target, "parent", "obsolete.txt")));
	}

	private CfgApp CreateApp(List<string>? requests = null) {
		using var archive = new MemoryStream();
		using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true)) {
			using var writer = new StreamWriter(zip.CreateEntry("application.txt").Open());
			writer.Write("deployed");
		}
		var bytes = archive.ToArray();
		var github = new HttpClient(new Handler(request => {
			requests?.Add(request.RequestUri!.AbsolutePath);
			return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/zip", StringComparison.Ordinal)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
				: new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GHArtifact { Id = 44, Name = "dev-build", Created_at = DateTime.UtcNow }) });
		}));
		return new CfgApp {
			Name = "api", Path = Path.Combine(_directory, "installed"), Service = "web_okis_dev",
			ChOwn = "www:www", ChMod = "755", HealthCheckUrl = "http://127.0.0.1:5000/api/health",
			HealthCheckTimeoutSeconds = 5, HealthCheckIntervalMilliseconds = 10,
			Repo = new GitHub(github) { App = "api", Org = "org", Repo = "repo", Name = "dev-build" }
		};
	}

	private async Task<(WebApplication, HttpClient, Config)> StartApi(IDeploymentRunner runner, bool health = true, string? appKey = "key", int lockSeconds = 0) {
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _directory, EnvironmentName = "Testing" });
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
			["Lock"] = lockSeconds.ToString(), ["Delay"] = "0", ["WaitTime"] = "0",
			["Apps:0:Name"] = "api", ["Apps:0:Key"] = appKey, ["Apps:0:Path"] = _directory,
			["Apps:0:Service"] = "web_okis_dev", ["Apps:0:Repo:App"] = "api",
			["Apps:0:HealthCheckUrl"] = health ? "http://127.0.0.1:5000/api/health" : null
		});
		var server = builder.Build();
		var cfg = new Config(server);
		DeploymentEndpoints.Map(server, cfg, runner);
		await server.StartAsync();
		var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		return (server, new HttpClient { BaseAddress = new Uri(address) }, cfg);
	}

	private class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
	}

	private class PendingRunner : IDeploymentRunner {
		private int _calls;
		public int Calls => _calls;
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<DeploymentResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public long? ArtifactId { get; private set; }
		public Task<DeploymentResult> Run(CfgApp app, long? artifactId, bool force, int waitTime, int delay, CancellationToken cancellationToken) {
			Interlocked.Increment(ref _calls);
			ArtifactId = artifactId;
			Entered.TrySetResult();
			return Finish.Task;
		}
	}
}
