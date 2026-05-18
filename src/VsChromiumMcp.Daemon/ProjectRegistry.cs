using System.Text.Json;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp.Daemon;

internal sealed class ProjectRegistry {
    private readonly object _lock = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> All() {
        lock (_lock) return _paths.ToArray();
    }

    public bool Contains(string fullPath) {
        var n = Normalize(fullPath);
        lock (_lock) return _paths.Contains(n);
    }

    public bool Add(string fullPath) {
        var n = Normalize(fullPath);
        bool added;
        lock (_lock) added = _paths.Add(n);
        if (added) Save();
        return added;
    }

    public bool Remove(string fullPath) {
        var n = Normalize(fullPath);
        bool removed;
        lock (_lock) removed = _paths.Remove(n);
        if (removed) Save();
        return removed;
    }

    private void Save() {
        try {
            Paths.EnsureCreated();
            string[] snapshot;
            lock (_lock) snapshot = _paths.ToArray();
            File.WriteAllText(Paths.ProjectsFile, JsonSerializer.Serialize(snapshot, PipeJson.Options));
        } catch (Exception ex) {
            Log.Error(ex, "registry: save failed");
        }
    }

    public void Load() {
        try {
            if (!File.Exists(Paths.ProjectsFile)) return;
            var json = File.ReadAllText(Paths.ProjectsFile);
            var arr = JsonSerializer.Deserialize<string[]>(json, PipeJson.Options);
            if (arr == null) return;
            lock (_lock) {
                _paths.Clear();
                foreach (var p in arr) _paths.Add(Normalize(p));
            }
            Log.Info($"registry: loaded {arr.Length} projects from {Paths.ProjectsFile}");
        } catch (Exception ex) {
            Log.Error(ex, "registry: load failed");
        }
    }

    private static string Normalize(string p) {
        return Path.GetFullPath(p).TrimEnd('\\', '/');
    }
}
