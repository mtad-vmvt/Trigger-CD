
namespace App;


public static class Files {

	public static void CleanDir(string dir, bool recursive, Log log, List<string>? except = null, string? root = null) {
		root ??= dir;
		if (!Directory.Exists(dir)) return;
		foreach (string file in Directory.GetFiles(dir)) {
			string relativePath = GetRelative(file, root);
			if (except == null || !except.Contains(relativePath)) File.Delete(file);
		}
		foreach (string subDir in Directory.GetDirectories(dir)) {
			string relativePath = GetRelative(subDir, root);
			if (except != null && except.Contains(relativePath)) continue;
			if (recursive) CleanDir(subDir, true, log, except, root);
			if (!Directory.EnumerateFileSystemEntries(subDir).Any()) {
				Directory.Delete(subDir, false);
			}
		}
	}

	public static string GetRelative(string path, string root) => Path.GetRelativePath(root, path).Replace('\\', '/');

	private static readonly string IdPath = "data/id.incr";
	private static long _currentId = -1;
	private static readonly object IdLock = new();

	public static long GetNextId() {
		lock (IdLock) {
			if (_currentId == -1) {
				if (File.Exists(IdPath))
					_ = long.TryParse(File.ReadAllText(IdPath), out _currentId);
				else _currentId = 0;
			}
			_currentId++;
			File.WriteAllText(IdPath, _currentId.ToString());
			return _currentId;
		}
	}
}

