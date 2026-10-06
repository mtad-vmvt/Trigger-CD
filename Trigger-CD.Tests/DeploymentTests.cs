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
	public async Task UnhealthyApiRemainsPendingThenFailsWithoutRecordingVersion() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var health = new HttpClient(new Handler(_ => {
			entered.TrySetResult();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		}));
		var app = CreateApp();
		app.HealthCheckTimeoutSeconds = 1;
		var manager = new DeploymentManager(new DeploymentRunner(health, (_, _, _) => true));
		var job = manager.Start(app, 44, false, 0, 0, 0, out _)!;
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual("pending", job.Status.Status);
		Assert.IsFalse(File.Exists("data/api.json"));
		await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual("failed", job.Status.Status);
		Assert.AreEqual("Application did not become ready before the health check deadline.", job.Status.Message);
		Assert.IsFalse(File.Exists("data/api.json"));
	}

	[TestMethod]
	public async Task SuccessIsPublishedAndVersionRecordedOnlyAfterApiIsReady() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		using var health = new HttpClient(new Handler(async _ => {
			if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("Connection refused");
			entered.TrySetResult();
			await ready.Task;
			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		var manager = new DeploymentManager(new DeploymentRunner(health, (_, _, _) => true));
		var app = CreateApp();
		var job = manager.Start(app, 44, false, 0, 0, 0, out _)!;
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual("pending", job.Status.Status);
		Assert.IsFalse(File.Exists("data/api.json"));
		Assert.AreEqual("deployed", File.ReadAllText(Path.Combine(app.Path!, "application.txt")));
		ready.SetResult();
		await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual("succeeded", job.Status.Status);
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
	public async Task StatusIsAuthenticatedAppScopedAndUnknownJobsFail() {
		var runner = new PendingRunner();
		var manager = new DeploymentManager(runner);
		var (server, client) = await StartApi(manager);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		var start = await client.GetAsync("/api/key?operation=start&artifactId=44");
		Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode);
		var pending = await start.Content.ReadFromJsonAsync<DeploymentStatus>();
		Assert.AreEqual("pending", pending!.Status);
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.AreEqual(44L, runner.ArtifactId);
		var query = $"?operation=status&deploymentId={pending.DeploymentId}";
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/wrong" + query)).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/other/key" + query)).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/key?operation=status&deploymentId={Guid.NewGuid()}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await client.GetAsync("/api/key?operation=start&artifactId=45")).StatusCode);
		runner.Finish.SetResult(new(false, "Service start failed."));
		await manager.Find("api", pending.DeploymentId)!.Completion;
		var failed = await client.GetFromJsonAsync<DeploymentStatus>("/api/key" + query);
		Assert.AreEqual("failed", failed!.Status);
		Assert.AreEqual("Service start failed.", failed.Message);
	}

	[TestMethod]
	public async Task LegacyRequestWaitsForCompletionAndReportsFailure() {
		var runner = new PendingRunner();
		var (server, client) = await StartApi(new DeploymentManager(runner));
		await using var serverLifetime = server;
		using var clientLifetime = client;
		var request = client.GetAsync("/api/key");
		await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsFalse(request.IsCompleted);
		runner.Finish.SetResult(new(false, "Readiness check failed."));
		var response = await request;
		Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
		StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Readiness check failed.");
	}

	[TestMethod]
	public async Task VerifiedServiceRequiresHealthConfigurationAndArtifactId() {
		var runner = new PendingRunner();
		var (server, client) = await StartApi(new DeploymentManager(runner), false);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/key?operation=start&artifactId=44")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/key?operation=start")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/key?operation=start&artifactId=0")).StatusCode);
		Assert.IsFalse(runner.Entered.Task.IsCompleted);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow(" ")]
	public async Task MissingConfiguredKeyRejectsStartStatusAndLegacyRequests(string? appKey) {
		var runner = new PendingRunner();
		var (server, client) = await StartApi(new DeploymentManager(runner), appKey: appKey);
		await using var serverLifetime = server;
		using var clientLifetime = client;
		foreach (var query in new[] { "", "?operation=start&artifactId=44", $"?operation=status&deploymentId={Guid.NewGuid()}" }) {
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

	private async Task<(WebApplication, HttpClient)> StartApi(DeploymentManager manager, bool health = true, string? appKey = "key") {
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _directory, EnvironmentName = "Testing" });
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
			["Lock"] = "0", ["Delay"] = "0", ["WaitTime"] = "0",
			["Apps:0:Name"] = "api", ["Apps:0:Key"] = appKey, ["Apps:0:Path"] = _directory,
			["Apps:0:Service"] = "web_okis_dev", ["Apps:0:Repo:App"] = "api",
			["Apps:0:HealthCheckUrl"] = health ? "http://127.0.0.1:5000/api/health" : null,
			["Apps:1:Name"] = "other", ["Apps:1:Key"] = "key"
		});
		var server = builder.Build();
		DeploymentEndpoints.Map(server, new Config(server), manager);
		await server.StartAsync();
		var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		return (server, new HttpClient { BaseAddress = new Uri(address) });
	}

	private class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
	}

	private class PendingRunner : IDeploymentRunner {
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<DeploymentResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public long? ArtifactId { get; private set; }
		public Task<DeploymentResult> Run(CfgApp app, long? artifactId, bool force, int waitTime, int delay, CancellationToken cancellationToken) {
			ArtifactId = artifactId;
			Entered.TrySetResult();
			return Finish.Task;
		}
	}
}
