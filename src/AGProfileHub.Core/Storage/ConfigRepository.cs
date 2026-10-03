namespace AGProfileHub.Core;

public sealed class ConfigRepository
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly HubPaths _paths;
    private readonly AtomicJsonStore _json;
    private readonly AtomicJsonStore _launchJson;

    public ConfigRepository(HubPaths paths)
    {
        _paths = paths;
        _paths.EnsureBaseLayout();
        _json = new AtomicJsonStore(paths.DataRoot);
        _launchJson = new AtomicJsonStore(paths.GlobalLaunchStateRoot);
    }

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

    // Preparation, persistence, reconciliation and compensation share one config lock.
    // A thrown save may already have committed: never infer absence from the exception.
    internal async Task<ProfileRegistration> CreateProfileAsync(ProfileRegistration profile,
        Action prepare, Action<Exception> compensate, CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var current = await ReadProfilesUnlockedAsync(cancellationToken);
        Validate(current);
        if (current.Profiles.Any(p => string.Equals(p.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new HubException($"A profile named '{profile.Name}' already exists. Profile names must be unique.", "profile_name_conflict");
        var next = current with { Profiles = [.. current.Profiles, profile] };
        Validate(next);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            prepare();
            await _json.WriteAsync(_paths.ProfilesFile, next, HubJsonContext.Default.ProfilesDocument, cancellationToken);
            return profile;
        }
        catch (Exception primary)
        {
            await ReconcileFailedCreationAsync(profile.Id, primary, compensate);
            throw;
        }
    }

    // Called only while GlobalConfigLock is held. Deliberately ignores cancellation.
    private async Task ReconcileFailedCreationAsync(Guid id, Exception primary, Action<Exception> compensate)
    {
        bool registered;
        try
        {
            PathSafety.RejectExistingReparsePoints(_paths.DataRoot, _paths.ProfilesFile);
            var exists = true;
            try
            {
                if ((File.GetAttributes(_paths.ProfilesFile) & FileAttributes.Directory) != 0)
                    throw new IOException("Profile registry is a directory.");
            }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            registered = false;
            if (exists)
            {
                var actual = await _json.ReadAsync(_paths.ProfilesFile, HubJsonContext.Default.ProfilesDocument,
                    () => throw new IOException("Profile registry disappeared during reconciliation."), CancellationToken.None);
                Validate(actual);
                registered = actual.Profiles.Any(p => p.Id == id);
            }
        }
        catch (Exception reconciliation)
        {
            primary.Data["ProfileCreationOutcome"] = "Unknown";
            primary.Data["ProfileCreationReconciliationFailure"] = reconciliation;
            return;
        }
        primary.Data["ProfileCreationOutcome"] = registered ? "Registered" : "NotRegistered";
        if (!registered) compensate(primary);
    }

    public async Task<HubSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var settings = await _json.ReadAsync(_paths.SettingsFile, HubJsonContext.Default.HubSettings, () => new HubSettings(), cancellationToken);
        if (settings.SchemaVersion != SchemaVersions.Current) throw new ConfigurationException("Settings schema is invalid.");
        if (!Enum.IsDefined(settings.DefaultBackend))
            throw new ConfigurationException("Settings use an unknown isolation backend.", "invalid_isolation_backend");
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
    internal Task<FileLock> LockLaunchAsync(CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        FileLock.AcquireAsync(_paths.GlobalLaunchLock, timeout ?? LockTimeout, cancellationToken);
    internal async Task<LaunchBarrierRecord?> ReadLaunchBarrierAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.LaunchBarrierFile)) return null;
        var barrier = await _launchJson.ReadAsync(_paths.LaunchBarrierFile, HubJsonContext.Default.LaunchBarrierRecord,
            () => throw new ConfigurationException("Launch barrier is missing.", "launch_barrier_invalid"), cancellationToken).ConfigureAwait(false);
        if (barrier.SchemaVersion != SchemaVersions.Current || barrier.AttemptId == Guid.Empty || barrier.ProfileId == Guid.Empty ||
            barrier.CreatedUtc == default || !IsFullPath(barrier.OriginDataRoot) || !IsFullPath(barrier.ExecutablePath) || !IsFullPath(barrier.UserDataPath) ||
            barrier.Root is not null && (barrier.Root.ProcessId <= 0 || barrier.Root.StartTimeUtc == default ||
                !IsFullPath(barrier.Root.ExecutablePath) || !IsFullPath(barrier.Root.UserDataPath) ||
                !string.Equals(barrier.Root.ExecutablePath, barrier.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(barrier.Root.UserDataPath, barrier.UserDataPath, StringComparison.OrdinalIgnoreCase)))
            throw new ConfigurationException("Launch barrier has invalid identity fields.", "launch_barrier_invalid");
        return barrier;
    }
    internal Task WriteLaunchBarrierAsync(LaunchBarrierRecord barrier, CancellationToken cancellationToken) =>
        _launchJson.WriteAsync(_paths.LaunchBarrierFile, barrier, HubJsonContext.Default.LaunchBarrierRecord, cancellationToken);
    internal void DeleteLaunchBarrier()
    {
        PathSafety.RejectExistingReparsePoints(_paths.GlobalLaunchStateRoot, _paths.LaunchBarrierFile);
        if (File.Exists(_paths.LaunchBarrierFile)) File.Delete(_paths.LaunchBarrierFile);
    }
    private Task<ProfilesDocument> ReadProfilesUnlockedAsync(CancellationToken cancellationToken) => _json.ReadAsync(_paths.ProfilesFile, HubJsonContext.Default.ProfilesDocument, () => new ProfilesDocument(), cancellationToken);

    private static bool IsFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathFullyQualified(path) && string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void Validate(ProfilesDocument document)
    {
        if (document.SchemaVersion != SchemaVersions.Current) throw new ConfigurationException("Profiles schema is invalid.");
        if (document.Profiles is null) throw new ConfigurationException("Profiles collection is missing.", "configuration_invalid");
        if (document.Profiles.Any(p => p is null || p.Id == Guid.Empty)) throw new ConfigurationException("A profile has an invalid ID.");
        if (document.Profiles.Any(p => p is not null && !Enum.IsDefined(p.Backend)))
            throw new ConfigurationException("A profile uses an unknown isolation backend.", "invalid_isolation_backend");
        if (document.Profiles.GroupBy(p => p.Id).Any(g => g.Count() != 1)) throw new ConfigurationException("Duplicate profile IDs are not allowed.");
        if (document.Profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length is < 1 or > 80 || p.Name.Any(char.IsControl)))
            throw new ConfigurationException("Profile names must contain 1-80 printable characters.", "invalid_profile_name");
        if (document.Profiles.Any(p => p.WorkspacePath is not null && !Path.IsPathFullyQualified(p.WorkspacePath)))
            throw new ConfigurationException("Workspace paths must be absolute.", "invalid_workspace_path");
    }
}
