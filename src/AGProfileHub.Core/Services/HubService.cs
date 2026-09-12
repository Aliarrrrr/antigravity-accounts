using System.Diagnostics;

namespace AGProfileHub.Core;

public sealed class HubService
{
    private readonly HubPaths _paths;
    private readonly ConfigRepository _repository;
    private readonly ProfileManager _profiles;
    private readonly ProcessSupervisor _processes;
    private readonly IAntigravityDriver _driver;
    private readonly IProfileIsolation? _isolation;
    private readonly IsolationSetup? _setup;

    internal HubService(HubPaths paths, ConfigRepository repository, ProfileManager profiles, ProcessSupervisor processes,
        IAntigravityDriver driver, IProfileIsolation? isolation, IsolationSetup? setup = null)
    {
        _paths = paths; _repository = repository; _profiles = profiles; _processes = processes; _driver = driver; _isolation = isolation; _setup = setup;
    }

    public string DataRoot => _paths.DataRoot;
    public string LogsRoot => _paths.LogsRoot;
    public string GetProfileDataRoot(Guid id) => _paths.ProfileRoot(id);
    public Task<IReadOnlyList<ProfileRegistration>> ListProfilesAsync(CancellationToken cancellationToken = default) => _profiles.ListAsync(cancellationToken);
    public Task<ProfileRegistration> GetProfileAsync(Guid id, CancellationToken cancellationToken = default) => _profiles.GetAsync(id, cancellationToken);
    public Task<ProfileRegistration> ResolveProfileAsync(string nameOrId, CancellationToken cancellationToken = default) => _profiles.ResolveAsync(nameOrId, cancellationToken);
    public async Task<ProfileRegistration> CreateProfileAsync(string name, string? workspacePath = null,
        IsolationBackendKind? backend = null, CancellationToken cancellationToken = default)
    {
        // A Profile that does not name a backend inherits the configured default,
        // so switching backends is one setting rather than a per-profile chore.
        var kind = backend ?? (await _repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).DefaultBackend;
        return await _profiles.CreateAsync(name, workspacePath, kind, cancellationToken).ConfigureAwait(false);
    }
    public Task<ProfileRegistration> RenameProfileAsync(Guid id, string name, CancellationToken cancellationToken = default) => _profiles.RenameAsync(id, name, cancellationToken);
    public Task<ProfileRegistration> SetWorkspaceAsync(Guid id, string? workspacePath, CancellationToken cancellationToken = default) => _profiles.SetWorkspaceAsync(id, workspacePath, cancellationToken);
    public async Task<ProfileStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var status = await _processes.GetStatusAsync(id, cancellationToken).ConfigureAwait(false);
        return await DecorateIsolationAsync(status, cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<ProfileStatus>> GetAllStatusesAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await _processes.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ProfileStatus>(statuses.Count);
        foreach (var status in statuses) result.Add(await DecorateIsolationAsync(status, cancellationToken).ConfigureAwait(false));
        return result;
    }
    public async Task<LaunchResult> LaunchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var box = _isolation is null ? null : await _isolation.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (box is null) return await _processes.LaunchAsync(id, cancellationToken).ConfigureAwait(false);
        var launched = await _isolation!.LaunchAsync(id, cancellationToken).ConfigureAwait(false);
        return new LaunchResult(new ProfileStatus(id, profile.Name, RuntimeState.Running, null,
            launched.StartedNewProcess ? $"Started in isolation box {launched.BoxName}." : $"Already running in isolation box {launched.BoxName}.",
            profile.WorkspacePath, launched.BoxName), launched.StartedNewProcess);
    }
    public async Task<IReadOnlyList<LaunchResult>> LaunchAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        if (profiles.Count == 0) return [];
        if (profiles.Count > 1)
        {
            if (_isolation is null)
                throw new UnsupportedCapabilityException("Launch all requires every profile to have a prepared isolation box.");
            foreach (var profile in profiles)
            {
                var status = await _isolation.GetStatusAsync(profile.Id, profile.WorkspacePath, cancellationToken).ConfigureAwait(false);
                if (status is null)
                    throw new UnsupportedCapabilityException("Launch all was not started because every profile must have a prepared isolation box.");
                if (!status.BackendAvailable || !status.BoxExists || !status.KeyringClaimed)
                    throw new UnsupportedCapabilityException($"Launch all was not started because isolation box '{status.BoxName}' is not ready.");
            }
        }
        var results = new List<LaunchResult>(profiles.Count);
        foreach (var profile in profiles) results.Add(await LaunchAsync(profile.Id, cancellationToken).ConfigureAwait(false));
        return results;
    }
    public async Task<StopResult> StopAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var box = _isolation is null ? null : await _isolation.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (box is null) return await _processes.StopAsync(id, force, cancellationToken).ConfigureAwait(false);
        var wasRunning = await _isolation!.StopAsync(id, cancellationToken).ConfigureAwait(false);
        return new StopResult(new ProfileStatus(id, profile.Name, RuntimeState.Stopped, null,
            wasRunning ? $"Terminated dedicated isolation box {box.BoxName}." : $"Isolation box {box.BoxName} was already stopped.",
            profile.WorkspacePath, box.BoxName), wasRunning, false);
    }
    public async Task<string> CreateShortcutAsync(Guid id, string cliExecutable, string? directory = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new UnsupportedCapabilityException("Desktop shortcuts require Windows.");
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return DesktopShortcut.Create(profile, _paths.DataRoot, cliExecutable,
            directory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }
    public async Task<LaunchResult> RestartAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var box = _isolation is null ? null : await _isolation.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (box is null) return await _processes.RestartAsync(id, force, cancellationToken).ConfigureAwait(false);
        var launched = await _isolation!.RestartAsync(id, cancellationToken).ConfigureAwait(false);
        return new LaunchResult(new ProfileStatus(id, profile.Name, RuntimeState.Running, null,
            $"Restarted in isolation box {launched.BoxName}.", profile.WorkspacePath, launched.BoxName), true);
    }

    public async Task<IsolationPrepareResult> PrepareIsolationAsync(Guid id, string? boxName = null, bool claimExistingFreshBox = false,
        CancellationToken cancellationToken = default)
    {
        await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (_isolation is null) throw new UnsupportedCapabilityException("No isolation backend is configured.");
        return await _isolation.PrepareAsync(id, boxName, claimExistingFreshBox, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IsolationRuntimeStatus?> GetIsolationStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return _isolation is null ? null : await _isolation.GetStatusAsync(id, profile.WorkspacePath, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<IsolationRuntimeStatus>> GetIsolationStatusesAsync(CancellationToken cancellationToken = default) =>
        _isolation is null ? Task.FromResult<IReadOnlyList<IsolationRuntimeStatus>>([]) : _isolation.StatusAsync(cancellationToken);

    /// <summary>
    /// Runs the idempotent setup chain (box, registration, keyring,
    /// direct-write) and returns the stage that was reached. This is the
    /// single entry point used by both the new-user commands and launch.
    /// </summary>
    public async Task<IsolationSetupReport> EnsureIsolationReadyAsync(Guid id, bool allowFreshBox = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (_setup is null) throw new UnsupportedCapabilityException("Isolation setup is not available in this environment.");
        return await _setup.EnsureReadyAsync(id, profile.Backend, allowFreshBox, profile.WorkspacePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IsolationStage> DetectIsolationStageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return _setup is null ? IsolationStage.BackendUnavailable
            : await _setup.DetectAsync(id, profile.Backend, profile.WorkspacePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string BoxName, IReadOnlyList<string> Paths, IReadOnlyList<string> MissingPaths)> GetWriteThroughAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (_isolation is null) throw new UnsupportedCapabilityException("No isolation backend is configured.");
        var status = await _isolation.GetStatusAsync(id, profile.WorkspacePath, cancellationToken).ConfigureAwait(false)
            ?? throw new HubException("This profile has no isolation box yet. Run iso-prepare first.", "isolation_not_prepared");
        return (status.BoxName, _isolation.WriteThroughPaths(id, profile.WorkspacePath), status.MissingWriteThroughPaths);
    }

    public Task LaunchWithWorkspaceAsync(Guid id, string workspacePath, CancellationToken cancellationToken = default) =>
        throw new UnsupportedCapabilityException("Workspace forwarding is not supported by the verified Antigravity Desktop 2.12.2 launch contract. The workspace remains profile metadata only.");

    public async Task<ProfileRegistration> RemoveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var held = await _repository.LockProfileAsync(id, cancellationToken);
        var status = await GetStatusAsync(id, cancellationToken);
        if (status.State != RuntimeState.Stopped) throw new HubException("Stop the profile and resolve any ownership warning before unregistering it.", "profile_not_stopped");
        return await _profiles.RemoveAsync(id, cancellationToken);
    }

    /// <summary>Sets the backend newly created Profiles use. Existing Profiles keep their own.</summary>
    public async Task<HubSettings> ConfigureDefaultBackendAsync(IsolationBackendKind backend, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(backend)) throw new ConfigurationException("Unknown isolation backend.", "invalid_isolation_backend");
        var current = await _repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        await _repository.WriteSettingsAsync(current with { DefaultBackend = backend }, cancellationToken).ConfigureAwait(false);
        return await _repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HubSettings> ConfigureExecutableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var executable = PathSafety.CanonicalizeExistingFile(executablePath);
        var current = await _repository.ReadSettingsAsync(cancellationToken);
        var changed = !string.Equals(current.AntigravityExecutablePath, executable, StringComparison.OrdinalIgnoreCase);
        var updated = current with
        {
            AntigravityExecutablePath = executable,
            AuthIsolationAttestedUtc = changed ? null : current.AuthIsolationAttestedUtc,
            AuthIsolationAttestedVersion = changed ? null : current.AuthIsolationAttestedVersion
        };
        await _repository.WriteSettingsAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default)
    {
        AntigravityInstallation? installation = null;
        var warnings = new List<string>();
        try { installation = await _driver.DiscoverAsync(cancellationToken); }
        catch (HubException ex) { warnings.Add($"[{ex.Code}] {ex.Message}"); }
        var settings = await _repository.ReadSettingsAsync(cancellationToken);
        IReadOnlyList<ProfileStatus> statuses;
        try { statuses = installation is null ? [] : await GetAllStatusesAsync(cancellationToken); }
        catch (HubException ex) { statuses = []; warnings.Add($"[{ex.Code}] Status reconciliation failed."); }
        try
        {
            var barrier = await _repository.ReadLaunchBarrierAsync(cancellationToken).ConfigureAwait(false);
            if (barrier is not null)
                warnings.Add("A global launch barrier is retained for a previous attempt. New launches fail closed until that process is verified or proven exited.");
        }
        catch (HubException ex)
        {
            warnings.Add($"[{ex.Code}] The global launch barrier could not be validated. New launches fail closed.");
        }
        if (installation is not null && !installation.IsKnownCompatible) warnings.Add(installation.CompatibilityDetail);
        if (installation is not null && settings.LastTestedVersion is not null && !string.Equals(settings.LastTestedVersion, installation.Version, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Installed version {installation.Version} differs from last tested version {settings.LastTestedVersion}.");
        if (installation is not null && settings.AuthIsolationAttestedVersion is not null && !string.Equals(settings.AuthIsolationAttestedVersion, installation.Version, StringComparison.OrdinalIgnoreCase))
            warnings.Add("The saved manual authentication-isolation attestation belongs to another version and is invalid.");
        warnings.Add("Profile boxes isolate application state within one Windows account; they are not a security boundary between Windows users.");
        var isolationStatuses = _isolation is null ? [] : await _isolation.StatusAsync(cancellationToken).ConfigureAwait(false);
        if (isolationStatuses.Any(status => !status.BoxExists || !status.KeyringClaimed))
            warnings.Add("At least one registered isolation mapping is not launch-ready; inspect iso-status before launch.");
        if (isolationStatuses.Any(status => !status.WriteThroughReady))
            warnings.Add("At least one isolation box is missing direct-write rules. Project/profile edits can remain only in the sandbox copy until configured.");
        if (statuses.Any(status => status.IsolationBoxName is null))
            warnings.Add("Profiles without a prepared isolation box use the legacy serial path and share the Windows user's host credential entry.");

        var preparedReady = isolationStatuses.Count > 0 && isolationStatuses.All(status => status.BackendAvailable && status.BoxExists && status.KeyringClaimed);

        var capabilities = new List<CapabilityFinding>
        {
            new("executableDiscovery", installation is null ? CapabilityState.Unknown : CapabilityState.Verified, installation is null ? "Executable was not found." : "Executable and file version were read without launching."),
            new("desktopRuntimeIsolation", installation?.IsKnownCompatible == true ? CapabilityState.Verified : CapabilityState.Unknown, "Independent user-data, process-home and Sandboxie box ownership were observed locally for Desktop 2.12.2."),
            new("visibleConcurrentWindows", preparedReady ? CapabilityState.Verified : CapabilityState.ManualVerificationRequired, "Two prepared boxes were manually accepted with separate visible windows on Desktop 2.12.2."),
            new("googleAccountIsolation", preparedReady ? CapabilityState.Verified : CapabilityState.ManualVerificationRequired, "The per-box keyring claim fixed the shared gemini:antigravity token collision; distinct concurrent accounts and restart persistence passed manual acceptance on Desktop 2.12.2."),
            new("workspaceWriteThrough", isolationStatuses.Count > 0 && isolationStatuses.All(status => status.WriteThroughReady) ? CapabilityState.Verified : CapabilityState.ManualVerificationRequired, "Every profile and workspace path must have a matching Sandboxie OpenFilePath rule so edits reach the host filesystem."),
            new("workspaceForwarding", CapabilityState.Unsupported, "Workspace is metadata; no supported Desktop forwarding contract was found."),
            new("credentialIsolation", preparedReady ? CapabilityState.Verified : CapabilityState.Experimental, "Hub writes an invalid same-name placeholder only inside a fresh isolation box; it never reads, copies, exports, or purges the real token.")
        };
        return new DoctorReport(DateTimeOffset.UtcNow, _paths.DataRoot, installation?.ExecutablePath, installation?.Version,
            installation is not null, installation?.IsKnownCompatible == true, capabilities, warnings, statuses);
    }

    public void OpenDataFolder(Guid? profileId = null) => OpenFolder(profileId is null ? _paths.DataRoot : _paths.ProfileRoot(profileId.Value));
    public void OpenLogsFolder() => OpenFolder(_paths.LogsRoot);

    private static void OpenFolder(string path)
    {
        PathSafety.RejectExistingPathAncestors(path);
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }

    private async Task<ProfileStatus> DecorateIsolationAsync(ProfileStatus status, CancellationToken cancellationToken)
    {
        if (_isolation is null) return status;
        var isolation = await _isolation.GetStatusAsync(status.ProfileId, status.WorkspacePath, cancellationToken).ConfigureAwait(false);
        if (isolation is null) return status;
        var readiness = !isolation.BackendAvailable ? "Sandboxie is unavailable" :
            !isolation.BoxExists ? "box is missing" :
            !isolation.KeyringClaimed ? "keyring is not claimed" :
            !isolation.WriteThroughReady ? $"{isolation.MissingWriteThroughPaths.Count} direct-write rule(s) missing" : "ready";
        var reconciledState = status.State == RuntimeState.Stopped && isolation.ProcessCount > 0 ? RuntimeState.Running : status.State;
        if (status.State == RuntimeState.Running && isolation.ProcessCount == 0) reconciledState = RuntimeState.Unknown;
        return status with
        {
            State = reconciledState,
            IsolationBoxName = isolation.BoxName,
            WriteThroughReady = isolation.WriteThroughReady,
            Detail = $"{status.Detail} Isolation box {isolation.BoxName}: {readiness}."
        };
    }
}

public static class HubBootstrap
{
    public static HubService CreateDefault(string? dataRoot = null)
    {
        var paths = new HubPaths(dataRoot);
        var repository = new ConfigRepository(paths);
        var profiles = new ProfileManager(repository, paths);
        var driver = new AntigravityDriver(paths, repository);
        var supervisor = new ProcessSupervisor(paths, repository, profiles, driver,
            new WindowsProcessCatalog(), new WindowsProcessLauncher(), new WindowsProcessController(), new StructuredEventLogger(paths));
        var isolation = new IsolatedProfileLauncher(paths, driver, repository);
        IElevationBridge elevation;
        try
        {
            var cli = DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory);
            elevation = new WindowsElevationBridge(cli, paths.ElevationRoot);
        }
        catch (HubException)
        {
            elevation = new UnavailableElevationBridge("The CLI executable could not be located, so elevation is unavailable.");
        }
        var setup = new IsolationSetup(isolation, elevation, kind => IsolationBackends.TryCreate(paths, kind));
        return new HubService(paths, repository, profiles, supervisor, driver, isolation, setup);
    }

    public static HubService CreateForTesting(HubPaths paths, ConfigRepository repository, ProfileManager profiles,
        IAntigravityDriver driver, IProcessCatalog catalog, IProcessLauncher launcher, IProcessController controller, IHubLogger? logger = null,
        int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null, IProfileIsolation? isolation = null, IsolationSetup? setup = null)
    {
        var supervisor = new ProcessSupervisor(paths, repository, profiles, driver, catalog, launcher, controller, logger ?? new NullHubLogger(),
            launchVerificationAttempts, launchVerificationDelay);
        return new HubService(paths, repository, profiles, supervisor, driver, isolation, setup);
    }
}
