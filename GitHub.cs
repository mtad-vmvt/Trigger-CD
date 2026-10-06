using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace App;

public class GitHub {
	private static readonly HttpClient DefaultClient = CreateClient();
	private readonly HttpClient _client;
	public string? App { get; set; }
	public string? Org { get; set; }
	public string? Repo { get; set; }
	public string? Name { get; set; }
	public string? Token { get; set; }

	public GitHub() : this(DefaultClient) { }
	public GitHub(HttpClient client) { _client = client; }

	private static HttpClient CreateClient() {
		var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
		client.DefaultRequestHeaders.Add("User-Agent", "A1-CD/0.2");
		return client;
	}

	public async Task<GHArtifact?> Download(Log log, long? artifactId = null, bool force = false, int retry = 0, CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(Org) || string.IsNullOrWhiteSpace(Repo) || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(App))
			throw new DeploymentException("GitHub artifact configuration is incomplete.");
		for (var attempt = 0; ; attempt++) {
			var address = $"https://api.github.com/repos/{Org}/{Repo}/actions/artifacts";
			address += artifactId.HasValue ? $"/{artifactId.Value}" : $"?name={Uri.EscapeDataString(Name)}&per_page=1";
			using var request = Request(address);
			using var response = await _client.SendAsync(request, cancellationToken);
			if (!response.IsSuccessStatusCode) throw new DeploymentException($"Artifact metadata request failed (HTTP {(int)response.StatusCode}).");
			var artifact = artifactId.HasValue
				? await response.Content.ReadFromJsonAsync<GHArtifact>(cancellationToken)
				: (await response.Content.ReadFromJsonAsync<GHArtifacts>(cancellationToken))?.Artifacts?.FirstOrDefault();
			if (artifact is null) throw new DeploymentException("No deployment artifact was found.");
			if ((artifactId.HasValue && artifact.Id != artifactId.Value) || artifact.Name != Name)
				throw new DeploymentException("Artifact does not match the requested deployment.");
			if (artifact.Expired) throw new DeploymentException("Deployment artifact has expired.");
			if (!artifactId.HasValue && !force && Version.Get(App).Id >= artifact.Id) {
				if (attempt >= retry) return null;
				await Task.Delay(1000, cancellationToken);
				continue;
			}
			using var download = Request($"https://api.github.com/repos/{Org}/{Repo}/actions/artifacts/{artifact.Id}/zip");
			using var archive = await _client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			if (!archive.IsSuccessStatusCode) throw new DeploymentException($"Artifact download failed (HTTP {(int)archive.StatusCode}).");
			await using (var file = new FileStream($"data/files/{App}.zip", FileMode.Create)) {
				await archive.Content.CopyToAsync(file, cancellationToken);
			}
			log.Print("Release", $"{artifact.Id} ({artifact.Size_in_bytes / 1024:0.##}KB)");
			return artifact;
		}
	}

	private HttpRequestMessage Request(string address) {
		var request = new HttpRequestMessage(HttpMethod.Get, address);
		if (Token is not null) request.Headers.Authorization = new("token", Token);
		return request;
	}
}

public class Version {
	public string? Repo { get; set; }
	public long Id { get; set; }
	public DateTime Date { get; set; }
	public string? Url { get; set; }
	public List<string>? Log { get; set; }

	public static Version Get(string repo) {
		var ret = Extensions.ReadJsonFile<Version>($"data/{repo}.json");
		return ret?.Repo is null ? new() { Repo = repo, Log = [] } : ret;
	}
	public void Save() => Extensions.SaveJsonFile($"data/{Repo}.json", this);
}

public static class Extensions {
	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
	public static T? ReadJsonFile<T>(string file) => File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : default;
	public static void SaveJsonFile<T>(string file, T obj) {
		var content = JsonSerializer.Serialize(obj, JsonOpts);
		var temporary = $"{file}.{Guid.NewGuid():N}.tmp";
		try {
			File.WriteAllText(temporary, content);
			File.Move(temporary, file, true);
		}
		finally {
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	public static bool ShellExec(string file, string arg, Log log) {
		try {
			using var process = new Process {
				StartInfo = new() { FileName = file, Arguments = arg, UseShellExecute = false, RedirectStandardError = true }
			};
			process.Start();
			_ = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode != 0) {
				log.Print("Error", new { Error = "Execute script", File = file, process.ExitCode });
				return false;
			}
			return true;
		}
		catch (Exception ex) {
			log.Print("Error", new { Error = "Execute script", File = file, Type = ex.GetType().Name });
			return false;
		}
	}
}

public class GHArtifacts {
	[JsonPropertyName("total_count")] public int Count { get; set; }
	public List<GHArtifact>? Artifacts { get; set; }
}

public class GHArtifact {
	public long Id { get; set; }
	public string? Node_Id { get; set; }
	public string? Name { get; set; }
	public long Size_in_bytes { get; set; }
	public string? Url { get; set; }
	[JsonPropertyName("archive_download_url")] public string? Download { get; set; }
	public bool Expired { get; set; }
	public DateTime Created_at { get; set; }
	public DateTime Updated_at { get; set; }
	public DateTime Expires_at { get; set; }
}
