namespace App;

public class DeploymentGate {
	private readonly object _sync = new();
	private readonly Dictionary<string, ApplicationState> _applications = new(StringComparer.OrdinalIgnoreCase);

	public bool TryEnter(string app, int lockSeconds, out string? conflict) {
		lock (_sync) {
			if (_applications.TryGetValue(app, out var previous)) {
				if (previous.Running) { conflict = "Running"; return false; }
				if (previous.LockedUntil > DateTime.UtcNow) { conflict = "Locked"; return false; }
			}
			_applications[app] = new() { Running = true, LockedUntil = DateTime.UtcNow.AddSeconds(lockSeconds) };
			conflict = null;
			return true;
		}
	}

	public void Exit(string app) {
		lock (_sync) { _applications[app].Running = false; }
	}

	private class ApplicationState {
		public bool Running { get; set; }
		public DateTime LockedUntil { get; init; }
	}
}
