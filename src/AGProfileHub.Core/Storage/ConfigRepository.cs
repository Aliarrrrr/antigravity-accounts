namespace AGProfileHub.Core;

public sealed class ConfigRepository
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly HubPaths _paths;
    private readonly AtomicJsonStore _json;

    public ConfigRepository(HubPaths paths) { _paths = paths; _paths.EnsureBaseLayout(); _json = new AtomicJsonStore(paths.DataRoot); }

    public async Task<ProfilesDocument> ReadProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var document = await ReadProfilesUnlockedAsync(cancellationToken);
        Validate(document);
        return document;
    }

    public async Task<T> UpdateProfilesAsync<T>(Func<ProfilesDocument, (ProfilesDocument Document, T Result)> update, CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var current = await ReadProfilesUnlockedAsync(cancellationToken);
        Validate(current);
        var (next, result) = update(current);
        Validate(next);
        await _json.WriteAsync(_paths.ProfilesFile, next, HubJsonContext.Default.ProfilesDocument, cancellationToken);
        return result;
    }

    public async Task<HubSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var settings = await _json.ReadAsync(_paths.SettingsFile, HubJsonContext.Default.HubSettings, () => new HubSettings(), cancellationToken);
        if (settings.SchemaVersion != SchemaVersions.Current) throw new ConfigurationException("Settings schema is invalid.");
        return settings;
    }

    public async Task WriteSettingsAsync(HubSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.SchemaVersion != SchemaVersions.Current) throw new ConfigurationException("Settings schema is invalid.");
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        await _json.WriteAsync(_paths.SettingsFile, settings, HubJsonContext.Default.HubSettings, cancellationToken);
    }

    internal async Task<RuntimeRecord?> ReadRuntimeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var held = await FileLock.AcquireAsync(_paths.RuntimeLock(id), LockTimeout, cancellationToken);
        var path = _paths.RuntimeFile(id);
        if (!File.Exists(path)) return null;
        var runtime = await _json.ReadAsync(path, HubJsonContext.Default.RuntimeRecord, () => throw new ConfigurationException("Runtime record is missing."), cancellationToken);
        if (runtime.SchemaVersion != SchemaVersions.Current || runtime.ProfileId != id || runtime.Root is null || runtime.Root.ProcessId <= 0 || runtime.Root.StartTimeUtc == default ||
            string.IsNullOrWhiteSpace(runtime.Root.ExecutablePath) || string.IsNullOrWhiteSpace(runtime.Root.UserDataPath))
            throw new ConfigurationException("Runtime record has an unsupported schema or invalid identity fields.", "runtime_invalid");
        return runtime;
    }
    internal async Task WriteRuntimeAsync(RuntimeRecord runtime, CancellationToken cancellationToken)
    {
        await using var held = await FileLock.AcquireAsync(_paths.RuntimeLock(runtime.ProfileId), LockTimeout, cancellationToken);
        await _json.WriteAsync(_paths.RuntimeFile(runtime.ProfileId), runtime, HubJsonContext.Default.RuntimeRecord, cancellationToken);
    }
    internal async Task DeleteRuntimeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var held = await FileLock.AcquireAsync(_paths.RuntimeLock(id), LockTimeout, cancellationToken);
        var path = _paths.RuntimeFile(id);
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        if (File.Exists(path)) File.Delete(path);
    }
    internal Task<FileLock> LockProfileAsync(Guid id, CancellationToken cancellationToken) => FileLock.AcquireAsync(_paths.ProfileLock(id), LockTimeout, cancellationToken);
    private Task<ProfilesDocument> ReadProfilesUnlockedAsync(CancellationToken cancellationToken) => _json.ReadAsync(_paths.ProfilesFile, HubJsonContext.Default.ProfilesDocument, () => new ProfilesDocument(), cancellationToken);

    private static void Validate(ProfilesDocument document)
    {
        if (document.SchemaVersion != SchemaVersions.Current) throw new ConfigurationException("Profiles schema is invalid.");
        if (document.Profiles is null) throw new ConfigurationException("Profiles collection is missing.", "configuration_invalid");
        if (document.Profiles.Any(p => p is null || p.Id == Guid.Empty)) throw new ConfigurationException("A profile has an invalid ID.");
        if (document.Profiles.GroupBy(p => p.Id).Any(g => g.Count() != 1)) throw new ConfigurationException("Duplicate profile IDs are not allowed.");
        if (document.Profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length is < 1 or > 80 || p.Name.Any(char.IsControl)))
            throw new ConfigurationException("Profile names must contain 1-80 printable characters.", "invalid_profile_name");
        if (document.Profiles.Any(p => p.WorkspacePath is not null && !Path.IsPathFullyQualified(p.WorkspacePath)))
            throw new ConfigurationException("Workspace paths must be absolute.", "invalid_workspace_path");
    }
}
