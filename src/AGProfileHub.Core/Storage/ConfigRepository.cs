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

    public async Task<HubSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.GlobalConfigLock, LockTimeout, cancellationToken);
        var settings = await _json.ReadAsync(_paths.SettingsFile, HubJsonContext.Default.HubSettings, () => new HubSettings(), cancellationToken);
        if (settings.SchemaVersion > SchemaVersions.Current) throw new ConfigurationException("Settings schema is invalid.");
        return settings with { SchemaVersion = SchemaVersions.Current };
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
        if (runtime.SchemaVersion > SchemaVersions.Current || runtime.ProfileId != id || runtime.Root is null || runtime.Root.ProcessId <= 0 || runtime.Root.StartTimeUtc == default ||
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
    internal Task<FileLock> LockLaunchAsync(CancellationToken cancellationToken) => FileLock.AcquireAsync(_paths.GlobalLaunchLock, LockTimeout, cancellationToken);
    internal async Task<LaunchBarrierRecord?> ReadLaunchBarrierAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.LaunchBarrierFile)) return null;
        var barrier = await _launchJson.ReadAsync(_paths.LaunchBarrierFile, HubJsonContext.Default.LaunchBarrierRecord,
            () => throw new ConfigurationException("Launch barrier is missing.", "launch_barrier_invalid"), cancellationToken).ConfigureAwait(false);
        if (barrier.SchemaVersion > SchemaVersions.Current || barrier.AttemptId == Guid.Empty || barrier.ProfileId == Guid.Empty ||
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

    internal async Task<KeyringShimMarker?> ReadKeyringMarkerAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var path = _paths.KeyringMarkerFile(profileId);
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        if (!File.Exists(path)) return null;
        var marker = await _json.ReadAsync(path, HubJsonContext.Default.KeyringShimMarker, () => throw new ConfigurationException("Keyring marker is missing."), cancellationToken);
        if (marker.SchemaVersion > SchemaVersions.Current || marker.ProfileId != profileId)
            throw new ConfigurationException("Keyring marker has an unsupported schema.", "runtime_invalid");
        return marker;
    }

    internal Task WriteKeyringMarkerAsync(KeyringShimMarker marker, CancellationToken cancellationToken)
    {
        var path = _paths.KeyringMarkerFile(marker.ProfileId);
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        return _json.WriteAsync(path, marker, HubJsonContext.Default.KeyringShimMarker, cancellationToken);
    }

    private Task<ProfilesDocument> ReadProfilesUnlockedAsync(CancellationToken cancellationToken) =>
        _json.ReadAsync(_paths.ProfilesFile, HubJsonContext.Default.ProfilesDocument, () => new ProfilesDocument(), cancellationToken);

    private static bool IsFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathFullyQualified(path) && string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void Validate(ProfilesDocument document)
    {
        if (document.SchemaVersion > SchemaVersions.Current) throw new ConfigurationException("Profiles schema is invalid.");
        if (document.Profiles is null) throw new ConfigurationException("Profiles collection is missing.", "configuration_invalid");
        // Schema v1 profiles deserialize with Backend=Direct and BoxId=null.
        if (document.Profiles.Any(p => p is null || p.Id == Guid.Empty)) throw new ConfigurationException("A profile has an invalid ID.");
        if (document.Profiles.GroupBy(p => p.Id).Any(g => g.Count() != 1)) throw new ConfigurationException("Duplicate profile IDs are not allowed.");
        if (document.Profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length is < 1 or > 80 || p.Name.Any(char.IsControl)))
            throw new ConfigurationException("Profile names must contain 1-80 printable characters.", "invalid_profile_name");
        if (document.Profiles.Any(p => p.WorkspacePath is not null && !Path.IsPathFullyQualified(p.WorkspacePath)))
            throw new ConfigurationException("Workspace paths must be absolute.", "invalid_workspace_path");
        if (document.Profiles.Any(p => p.Backend == ProfileBackend.Sandboxie && string.IsNullOrWhiteSpace(p.BoxId)))
            throw new ConfigurationException("Sandboxie profiles must have a box id.", "missing_box_id");
        if (document.Profiles.Any(p => p.Backend == ProfileBackend.Direct && !string.IsNullOrWhiteSpace(p.BoxId)))
            throw new ConfigurationException("Direct profiles must not have a box id.", "unexpected_box_id");
        if (document.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.BoxId))
            .GroupBy(p => p.BoxId!, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            throw new ConfigurationException("Duplicate sandbox box ids are not allowed.", "duplicate_box_id");
        if (document.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.BoxId)))
        {
            foreach (var boxId in document.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.BoxId)).Select(p => p.BoxId!))
            {
                try { WindowsSandboxieRuntime.ValidateBoxId(boxId); }
                catch (ConfigurationException ex) { throw new ConfigurationException(ex.Message, "invalid_box_id", ex); }
            }
        }
    }
}
