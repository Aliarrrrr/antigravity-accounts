using System.Diagnostics;

namespace AGProfileHub.Core;

public sealed class HubService
{
    private readonly HubPaths _paths;
    private readonly ConfigRepository _repository;
    private readonly ProfileManager _profiles;
    private readonly ProcessSupervisor _processes;
    private readonly IAntigravityDriver _driver;

    internal HubService(HubPaths paths, ConfigRepository repository, ProfileManager profiles, ProcessSupervisor processes, IAntigravityDriver driver)
    {
        _paths = paths; _repository = repository; _profiles = profiles; _processes = processes; _driver = driver;
    }

    public string DataRoot => _paths.DataRoot;
    public string LogsRoot => _paths.LogsRoot;
    public string GetProfileDataRoot(Guid id) => _paths.ProfileRoot(id);
    public Task<IReadOnlyList<ProfileRegistration>> ListProfilesAsync(CancellationToken cancellationToken = default) => _profiles.ListAsync(cancellationToken);
    public Task<ProfileRegistration> GetProfileAsync(Guid id, CancellationToken cancellationToken = default) => _profiles.GetAsync(id, cancellationToken);
    public Task<ProfileRegistration> CreateProfileAsync(string name, string? workspacePath = null, CancellationToken cancellationToken = default) => _profiles.CreateAsync(name, workspacePath, cancellationToken);
    public Task<ProfileRegistration> RenameProfileAsync(Guid id, string name, CancellationToken cancellationToken = default) => _profiles.RenameAsync(id, name, cancellationToken);
    public Task<ProfileRegistration> SetWorkspaceAsync(Guid id, string? workspacePath, CancellationToken cancellationToken = default) => _profiles.SetWorkspaceAsync(id, workspacePath, cancellationToken);
    public Task<ProfileStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) => _processes.GetStatusAsync(id, cancellationToken);
    public Task<IReadOnlyList<ProfileStatus>> GetAllStatusesAsync(CancellationToken cancellationToken = default) => _processes.GetAllAsync(cancellationToken);
    public Task<LaunchResult> LaunchAsync(Guid id, CancellationToken cancellationToken = default) => _processes.LaunchAsync(id, cancellationToken);
    public async Task<IReadOnlyList<LaunchResult>> LaunchAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        if (profiles.Count > 1)
            throw new UnsupportedCapabilityException("Launch all is disabled because this Desktop version cannot safely run Google-account profiles concurrently.");
        return profiles.Count == 0 ? [] : [await _processes.LaunchAsync(profiles[0].Id, cancellationToken).ConfigureAwait(false)];
    }
    public Task<StopResult> StopAsync(Guid id, bool force = false, CancellationToken cancellationToken = default) => _processes.StopAsync(id, force, cancellationToken);
    public async Task<string> CreateShortcutAsync(Guid id, string cliExecutable, string? directory = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new UnsupportedCapabilityException("Desktop shortcuts require Windows.");
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return DesktopShortcut.Create(profile, _paths.DataRoot, cliExecutable,
            directory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }
    public Task<LaunchResult> RestartAsync(Guid id, bool force = false, CancellationToken cancellationToken = default) => _processes.RestartAsync(id, force, cancellationToken);

    public Task LaunchWithWorkspaceAsync(Guid id, string workspacePath, CancellationToken cancellationToken = default) =>
        throw new UnsupportedCapabilityException("Workspace forwarding is not supported by the verified Antigravity Desktop 2.12.2 launch contract. The workspace remains profile metadata only.");

    public async Task<ProfileRegistration> RemoveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var held = await _repository.LockProfileAsync(id, cancellationToken);
        var status = await _processes.GetStatusAsync(id, cancellationToken);
        if (status.State != RuntimeState.Stopped) throw new HubException("Stop the profile and resolve any ownership warning before unregistering it.", "profile_not_stopped");
        return await _profiles.RemoveAsync(id, cancellationToken);
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
        try { statuses = installation is null ? [] : await _processes.GetAllAsync(cancellationToken); }
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
        warnings.Add("Synthetic HOME isolates file paths but is not an OS security boundary and can hide existing Git/SSH/tool configuration.");
        warnings.Add("Manual validation found that concurrent profiles can resolve to the same later-signed-in Google account. Do not use this Desktop version for separate Google accounts.");
        warnings.Add("The shared antigravity:// protocol registration can make OAuth callback routing ambiguous while multiple instances run.");

        var capabilities = new List<CapabilityFinding>
        {
            new("executableDiscovery", installation is null ? CapabilityState.Unknown : CapabilityState.Verified, installation is null ? "Executable was not found." : "Executable and file version were read without launching."),
            new("desktopRuntimeIsolation", installation?.IsKnownCompatible == true ? CapabilityState.Experimental : CapabilityState.Unknown, "Independent user-data and process-home roots were observed locally for Desktop 2.12.2."),
            new("visibleConcurrentWindows", CapabilityState.Unknown, "A prior disposable probe observed first-run windows; run the local probe again to attest the current installation."),
            new("googleAccountIsolation", CapabilityState.Incompatible, "Manual validation showed concurrent profiles resolved to the Google account signed in later. Directory isolation does not isolate Google accounts on Desktop 2.12.2."),
            new("workspaceForwarding", CapabilityState.Unsupported, "Workspace is metadata; no supported Desktop forwarding contract was found."),
            new("credentialOperations", CapabilityState.Unsupported, "Hub never reads, copies, exports, or purges credentials.")
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
        return new HubService(paths, repository, profiles, supervisor, driver);
    }

    public static HubService CreateForTesting(HubPaths paths, ConfigRepository repository, ProfileManager profiles,
        IAntigravityDriver driver, IProcessCatalog catalog, IProcessLauncher launcher, IProcessController controller, IHubLogger? logger = null,
        int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null)
    {
        var supervisor = new ProcessSupervisor(paths, repository, profiles, driver, catalog, launcher, controller, logger ?? new NullHubLogger(),
            launchVerificationAttempts, launchVerificationDelay);
        return new HubService(paths, repository, profiles, supervisor, driver);
    }
}
