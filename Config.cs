using System.Text.Json;

namespace App;

public class Config {
	private DateTime NextReload { get; set; }
	private Dictionary<string, CfgApp> AppsCache { get; set; } = [];
	public Dictionary<string, CfgApp> Apps { get { if(NextReload<DateTime.UtcNow) Reload(); return AppsCache; }}
	public int WaitTime { get; set; }
	public int Delay { get; set; }
	public int Lock { get; set; }

	public CfgDefault Default { get; set; } = new();

	private WebApplication App { get; }
	private string GetString (string key) => App.Configuration.GetValue<string>(key)??string.Empty;
	private int GetInt (string key, int _default=0) => App.Configuration.GetValue(key, _default);	
	private bool GetBool (string key, bool _default=false) => App.Configuration.GetValue(key, _default);

	public void Reload(){
		NextReload=DateTime.UtcNow.AddSeconds(GetInt("ConfigReload",5));
		WaitTime = GetInt("WaitTime",10);
		Delay = GetInt("Delay",2);
		Lock = GetInt("Lock",300);

		App.Configuration.GetSection("Default").Bind(Default);
		var lst = new List<CfgApp>();
		App.Configuration.GetSection("Apps").Bind(lst);
		if(lst is not null){
			var ret = new Dictionary<string,CfgApp>(StringComparer.OrdinalIgnoreCase);
			foreach (var i in lst) {
				if (i.Name is not null) {
					ret[i.Name.ToLower()] = i;
					if (i.Repo is not null) {
						i.Repo.App ??= i.Name;
						i.Repo.Org ??= Default.Org;
						i.Repo.Token ??= Default.Token;
						i.ChMod ??= Default.ChMod;
						i.ChOwn ??= Default.ChOwn;
						i.Clean ??= Default.Clean;
						i.Debug ??= Default.Debug;
						i.Script ??= Default.Script;
					}
				}
			}
			AppsCache=ret;
		}
	}
	public Config(WebApplication app){
		App=app; Reload();
	}
}

public class CfgDefault {
	public string? Org { get; set; }
	public string? Token { get; set; }
	public string? ChOwn { get; set; }
	public string? ChMod { get; set; }
	public bool Clean { get; set; }
	public bool Debug { get; set; }
	public string? Script { get; set; }
}

public class CfgApp{
	public string? Name { get; set; }
	public string? Key { get; set; }
	public string? Path { get; set;}
	public GitHub? Repo { get; set; }
	public bool? Debug { get; set; }
	public DateTime Lock { get; set; }
	public DateTime Running { get; set; }
	public string? ChOwn { get; set; }
	public string? ChMod { get; set; }
	public string? Service { get; set; }
	public string? HealthCheckUrl { get; set; }
	public int HealthCheckTimeoutSeconds { get; set; } = 60;
	public int HealthCheckIntervalMilliseconds { get; set; } = 1000;
	public bool? Clean { get; set; }
	public List<string> SkipClean { get; set; } = ["appsettings.json"];
	public string? Script { get; set; }
}


public class Log(string app) {
	public long ID { get; set; } = Files.GetNextId();
	public string App { get; set; } = app;
	public void Print(string act, string msg) {
		Console.WriteLine($"Rel|{ID}|{App}|{act}: {msg.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}");
	}
	public void Print(string act, object msg) => Print(act, JsonSerializer.Serialize(msg));
}
