namespace AGProfileHub.Core;

public sealed class HubPaths
{
    public HubPaths(string? dataRoot = null)
    {
        var requested = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityProfileHub");
        DataRoot = Path.GetFullPath(requested);
        PathSafety.RejectExistingPathAncestors(DataRoot);
    }

    public string DataRoot { get; }
    public string ConfigDirectory => Path.Combine(DataRoot, "config");
    public string ProfilesFile => Path.Combine(ConfigDirectory, "profiles.json");
    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public string ProfilesRoot => Path.Combine(DataRoot, "profiles");
    public string RuntimeStateRoot => Path.Combine(DataRoot, "runtime", "state");
    public string LocksRoot => Path.Combine(DataRoot, "runtime", "locks");
    public string LogsRoot => Path.Combine(DataRoot, "logs");
    public string GlobalConfigLock => Path.Combine(LocksRoot, "config.lock");
    public string ProfileRoot(Guid id) => SafeProfilePath(id, null);
    public string UserData(Guid id) => SafeProfilePath(id, "user-data");
    public string Home(Guid id) => SafeProfilePath(id, "home");
    public string Roaming(Guid id) => SafeProfilePath(id, "roaming");
    public string Local(Guid id) => SafeProfilePath(id, "local");
    public string Temp(Guid id) => SafeProfilePath(id, "temp");
    public string RuntimeFile(Guid id) => Path.Combine(RuntimeStateRoot, $"{id:D}.json");
    public string ProfileLock(Guid id) => Path.Combine(LocksRoot, $"profile-{id:D}.lock");
    public string RuntimeLock(Guid id) => Path.Combine(LocksRoot, $"runtime-{id:D}.lock");
    public string LogsLock => Path.Combine(LocksRoot, "logs.lock");

    public void EnsureBaseLayout()
    {
        Directory.CreateDirectory(DataRoot);
        PathSafety.RejectExistingReparsePoints(DataRoot, DataRoot);
        foreach (var path in new[] { ConfigDirectory, ProfilesRoot, RuntimeStateRoot, LocksRoot, LogsRoot }) PathSafety.EnsureSafeDirectory(DataRoot, path);
    }

    public void EnsureProfileLayout(Guid id)
    {
        if (id == Guid.Empty) throw new ConfigurationException("Profile ID cannot be empty.");
        foreach (var path in new[] { ProfileRoot(id), UserData(id), Home(id), Roaming(id), Local(id), Temp(id) }) PathSafety.EnsureSafeDirectory(DataRoot, path);
    }

    private string SafeProfilePath(Guid id, string? child)
    {
        if (id == Guid.Empty) throw new ConfigurationException("Profile ID cannot be empty.");
        var root = Path.Combine(ProfilesRoot, id.ToString("D"));
        var result = child is null ? root : Path.Combine(root, child);
        PathSafety.EnsureContained(DataRoot, result);
        return result;
    }
}
