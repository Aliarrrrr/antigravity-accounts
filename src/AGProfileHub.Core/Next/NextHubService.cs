using System.Diagnostics;

namespace AGProfileHub.Core;

/// <summary>The Next lifecycle. All GUI/CLI launches pass through this proof gate.</summary>
public sealed class NextHubService
{
    public const int StrategyRevision = 2;
    public HubPaths Paths { get; }
    public ConfigRepository Repository { get; }
    public ProfileManager Profiles { get; }
    public NextBridge Bridge { get; }
    private readonly IProcessCatalog _catalog;
    private readonly IProcessController _controller;
    private readonly IProtocolCommandStore _protocolCommands;
    private readonly string _protocolLockPath;
    private readonly ICallbackProcessLauncher _callbackLauncher;
    private readonly ICallbackWaiterStore _waiters;
    private readonly TimeSpan _callbackTimeout;
    private AtomicJsonStore Store => new(Paths.DataRoot);
    private string ProofPath => Path.Combine(Paths.DataRoot, "compatibility.json");
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);
    public NextHubService(string? dataRoot = null, IProcessCatalog? catalog = null, IProcessController? controller = null)
        : this(dataRoot, catalog, controller, null, null, null, null) { }
    internal NextHubService(string? dataRoot, IProcessCatalog? catalog, IProcessController? controller,
        IProtocolCommandStore? protocolCommands, string? protocolLockPath, ICallbackProcessLauncher? callbackLauncher,
        ICallbackWaiterStore? waiters, TimeSpan? callbackTimeout = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = dataRoot ?? Path.Combine(local, "AntigravityProfileHubNext");
        if (EqualPath(root, Path.Combine(local, "AntigravityProfileHub")))
            throw new ConfigurationException("Next cannot use the legacy data root.");
        Paths = new HubPaths(root, Path.Combine(root, "runtime", "global"));
        Repository = new ConfigRepository(Paths); Profiles = new ProfileManager(Repository, Paths); Bridge = new NextBridge(Paths);
        _catalog = catalog ?? new WindowsProcessCatalog(); _controller = controller ?? new WindowsProcessController();
        _protocolCommands = protocolCommands ?? new WindowsProtocolCommandStore();
        _protocolLockPath = protocolLockPath ?? NextProtocol.DefaultLockPath;
        _callbackLauncher = callbackLauncher ?? new WindowsCallbackProcessLauncher();
        _waiters = waiters ?? new AtomicCallbackWaiterStore(Paths);
        _callbackTimeout = callbackTimeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<ProfileRegistration> CreateAsync(string name, string? workspace = null, CancellationToken token = default)
    {
        var profile = await Profiles.CreateAsync(name, workspace, IsolationBackendKind.ProcessLocal, token);
        await Bridge.ApplyAsync(profile.Id, true, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), token);
        return profile;
    }
    public async Task<InstallationFingerprint> CurrentInstallationAsync(CancellationToken token = default)
    {
        var settings = await Repository.ReadSettingsAsync(token);
        var executable = NextCompatibility.DiscoveryCandidates(settings.AntigravityExecutablePath).FirstOrDefault(File.Exists)
            ?? throw new HubException("Configure the installed Antigravity executable.", "executable_not_found");
        return await NextCompatibility.FingerprintAsync(executable, token);
    }
    public async Task ConfigureExecutableAsync(string path, CancellationToken token = default)
    {
        var fingerprint = await NextCompatibility.FingerprintAsync(path, token);
        var settings = await Repository.ReadSettingsAsync(token);
        await Repository.WriteSettingsAsync(settings with { AntigravityExecutablePath = fingerprint.Executable,
            ExecutableSha256 = fingerprint.Components[Path.GetFileName(fingerprint.Executable)] }, token);
    }
    public Task<CompatibilityProof?> ReadProofAsync(CancellationToken token = default) =>
        Store.ReadAsync(ProofPath, NextJsonContext.Default.CompatibilityProof, () => null!, token)!;
    public async Task<object> CompatibilityAsync(CancellationToken token = default)
    {
        var current = await CurrentInstallationAsync(token); var proof = await ReadProofAsync(token);
        var valid = NextCompatibility.PermitsLaunch(proof, current);
        return new { installation = current, strategyRevision = StrategyRevision, experimentalLaunch = valid,
            manuallyAccepted = valid && proof!.ManuallyAcceptedUtc is not null, workspace = "unsupported: no verified forwarding interface",
            probeRequired = !valid, proof };
    }
    public async Task AcceptAsync(CancellationToken token = default)
    {
        var current = await CurrentInstallationAsync(token); var proof = await RequireProofAsync(current, token);
        await Store.WriteAsync(ProofPath, proof with { ManuallyAcceptedUtc = DateTimeOffset.UtcNow }, NextJsonContext.Default.CompatibilityProof, token);
    }
    private async Task<CompatibilityProof> RequireProofAsync(InstallationFingerprint current, CancellationToken token)
    {
        var proof = await ReadProofAsync(token);
        if (!NextCompatibility.PermitsLaunch(proof, current)) throw new HubException("Installation or strategy has no current isolation proof. Run probe; manual acceptance is separate.", "compatibility_probe_required");
        return proof!;
    }

    public static LaunchPlan BuildPlan(HubPaths paths, Guid id, InstallationFingerprint install)
    {
        var home = paths.Home(id);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home, ["USERPROFILE"] = home, ["HOMEDRIVE"] = Path.GetPathRoot(home)!.TrimEnd('\\'),
            ["HOMEPATH"] = home.Length > 2 && home[1] == ':' ? home[2..] : home,
            ["APPDATA"] = paths.Roaming(id), ["LOCALAPPDATA"] = paths.Local(id), ["TEMP"] = paths.Temp(id), ["TMP"] = paths.Temp(id),
            ["WSL_DISTRO_NAME"] = "AGPHNext-" + id.ToString("N")[..8], ["ANTIGRAVITY_WSL_DISTRO"] = "",
            ["SSH_CONNECTION"] = "", ["SSH_CLIENT"] = "", ["SSH_TTY"] = "", ["WSL_INTEROP"] = "",
            ["NODE_OPTIONS"] = "", ["ELECTRON_RUN_AS_NODE"] = "", ["ELECTRON_OZONE_PLATFORM_HINT"] = "", ["DEV_URL"] = "", ["JETSKI_LS_PORT"] = "",
            ["ANTIGRAVITY_LS_ADDRESS"] = "", ["ANTIGRAVITY_CSRF_TOKEN"] = ""
        };
        return new LaunchPlan(install.Executable, install.Version, ["--user-data-dir=" + paths.UserData(id)], environment,
            "Antigravity.exe --user-data-dir=<managed-next-profile>");
    }
    internal static bool EqualPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    public static IReadOnlyList<ProcessSnapshot> SelectOwned(IReadOnlyList<ProcessSnapshot> snapshot, string executable, string userData, DateTimeOffset? notBefore = null)
    {
        var owned = snapshot.Where(p => p.StartTimeUtc != default && EqualPath(p.ExecutablePath, executable) &&
            EqualPath(CommandLineIdentity.TryGetUserDataPath(p.CommandLine), userData) &&
            (notBefore is null || p.StartTimeUtc >= notBefore.Value.AddSeconds(-1))).ToDictionary(p => p.ProcessId);
        bool changed;
        do
        {
            changed = false;
            foreach (var row in snapshot)
                if (!owned.ContainsKey(row.ProcessId) && owned.TryGetValue(row.ParentProcessId, out var parent) &&
                    row.StartTimeUtc != default && row.StartTimeUtc >= parent.StartTimeUtc.AddSeconds(-1) && !string.IsNullOrWhiteSpace(row.ExecutablePath))
                { owned.Add(row.ProcessId, row); changed = true; }
        } while (changed);
        return owned.Values.ToList();
    }
    private async Task<IReadOnlyList<ProcessSnapshot>> OwnedAsync(Guid id, string executable, DateTimeOffset? notBefore, CancellationToken token)
    {
        try
        {
            var snapshot = await _catalog.SnapshotAsync(executable, token);
            if (snapshot.Any(row => EqualPath(CommandLineIdentity.TryGetUserDataPath(row.CommandLine), Paths.UserData(id)) &&
                (string.IsNullOrWhiteSpace(row.ExecutablePath) || row.StartTimeUtc == default)))
                throw new ProcessOwnershipException("A process claims this profile but its image or creation time cannot be verified.");
            var owned = SelectOwned(snapshot, executable, Paths.UserData(id), notBefore).ToDictionary(row => row.ProcessId);
            var state = await RuntimeAsync(id, token);
            foreach (var identity in state.Root is null ? [] : state.Processes)
            {
                if (notBefore is not null && identity.StartTimeUtc < notBefore.Value.AddSeconds(-1)) continue;
                var row = snapshot.FirstOrDefault(p => p.ProcessId == identity.ProcessId);
                if (row is not null && row.StartTimeUtc == identity.StartTimeUtc && string.IsNullOrWhiteSpace(row.ExecutablePath))
                    throw new ProcessOwnershipException("A recorded process image cannot be verified.");
                if (row is not null && SameIdentity(row, identity)) owned[row.ProcessId] = row;
            }
            return owned.Values.ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new HubException("Process enumeration failed (" + ex.GetType().Name + "); liveness is unknown and no destructive operation is permitted.", "process_enumeration_unavailable", ex); }
    }
    private string RuntimePath(Guid id) => Path.Combine(Paths.RuntimeStateRoot, "next-" + id.ToString("D") + ".json");
    private async Task<NextRuntime> RuntimeAsync(Guid id, CancellationToken token)
    {
        var state = await Store.ReadAsync(RuntimePath(id), NextJsonContext.Default.NextRuntime,
            () => new NextRuntime { ProfileId = id, State = RuntimeState.Stopped }, token);
        if (state.ProfileId != id || !Enum.IsDefined(state.State) || state.Processes is null ||
            state.Root is { } root && (!EqualPath(root.UserDataPath, Paths.UserData(id)) || state.Installation is null ||
                !EqualPath(root.ExecutablePath, state.Installation.Executable) || root.StartTimeUtc < state.AttemptUtc.AddSeconds(-1)) ||
            state.Processes.Any(p => p.ProcessId <= 0 || p.StartTimeUtc == default || !EqualPath(p.UserDataPath, Paths.UserData(id))))
            throw new ConfigurationException("Runtime scope identity is invalid; no process operation is permitted.");
        return state;
    }
    private async Task SaveRuntimeAsync(NextRuntime runtime, CancellationToken token)
    {
        await Store.WriteAsync(RuntimePath(runtime.ProfileId), runtime, NextJsonContext.Default.NextRuntime, token);
        await WriteDiagnosticAsync(new HubLogEvent(DateTimeOffset.UtcNow, "next-lifecycle", runtime.ProfileId,
            runtime.Installation?.Version, null, runtime.Root?.ProcessId, runtime.State.ToString(), null));
    }
    public async Task<ProfileStatus> StatusAsync(Guid id, CancellationToken token = default)
    {
        var profile = await Profiles.GetAsync(id, token); var state = await RuntimeAsync(id, token);
        if (state.ProfileId != id) throw new ConfigurationException("Runtime identity mismatch.");
        try
        {
            var executable = state.Installation?.Executable;
            if (executable is null)
            {
                var settings = await Repository.ReadSettingsAsync(token);
                executable = NextCompatibility.DiscoveryCandidates(settings.AntigravityExecutablePath).FirstOrDefault(File.Exists)
                    ?? throw new HubException("Installed Desktop not found; process state is unknown.", "executable_not_found");
                executable = PathSafety.CanonicalizeExistingFile(executable);
            }
            var owned = await OwnedAsync(id, executable, null, token);
            if (owned.Count == 0) return new ProfileStatus(id, profile.Name,
                state.State is RuntimeState.Starting or RuntimeState.Stopping ? RuntimeState.Unknown : state.State == RuntimeState.Failed ? RuntimeState.Failed : RuntimeState.Stopped,
                null, state.Detail, profile.WorkspacePath);
            var trusted = state.Root is not null && owned.Any(row => SameIdentity(row, state.Root)) && state.FileTokenStore;
            return new ProfileStatus(id, profile.Name, trusted ? RuntimeState.Running : RuntimeState.Unknown, state.Root,
                trusted ? "File token store verified for this process." : "Live process lacks this launch's proof; no login or workspace handoff.", profile.WorkspacePath);
        }
        catch (HubException ex) { return new ProfileStatus(id, profile.Name, RuntimeState.Unknown, null, ex.Code, profile.WorkspacePath); }
    }
    private static bool SameIdentity(ProcessSnapshot row, ProcessIdentity identity) => row.ProcessId == identity.ProcessId &&
        row.StartTimeUtc == identity.StartTimeUtc && EqualPath(row.ExecutablePath, identity.ExecutablePath);
    public async Task<IReadOnlyList<ProfileStatus>> StatusAllAsync(CancellationToken token = default)
    {
        var result = new List<ProfileStatus>(); foreach (var p in await Profiles.ListAsync(token)) result.Add(await StatusAsync(p.Id, token)); return result;
    }
    public async Task<NextRuntime> LaunchAsync(Guid id, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Paths.ProfileLock(id), OperationTimeout, token);
        await Profiles.GetAsync(id, token);
        var install = await CurrentInstallationAsync(token); await RequireProofAsync(install, token);
        var live = await OwnedAsync(id, install.Executable, null, token);
        if (live.Count > 0)
        {
            var existing = await RuntimeAsync(id, token);
            if (existing.Root is null || !live.Any(row => SameIdentity(row, existing.Root)) || !existing.FileTokenStore || !NextCompatibility.Same(existing.Installation, install))
                throw new HubException("Live profile has no current process-bound proof; stop it explicitly before retrying.", "isolation_scope_unverified");
            return existing;
        }
        await Bridge.ApplyAsync(id, (await Bridge.ReadAsync(id, token)).Enabled, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), token);
        return await LaunchCoreAsync(id, install, token);
    }
    private async Task<NextRuntime> LaunchCoreAsync(Guid id, InstallationFingerprint install, CancellationToken token)
    {
        using var lease = await NextCompatibility.HoldAsync(install, token);
        Paths.EnsureProfileLayout(id);
        var log = Path.Combine(Paths.UserData(id), "logs", "language_server.log");
        PathSafety.RejectExistingReparsePoints(Paths.DataRoot, log);
        if (File.Exists(log)) File.Move(log, log + ".previous-" + Guid.NewGuid().ToString("N"));
        var state = new NextRuntime { ProfileId = id, State = RuntimeState.Starting, AttemptUtc = DateTimeOffset.UtcNow, Installation = install };
        await SaveRuntimeAsync(state, token);
        var plan = BuildPlan(Paths, id, install);
        try
        {
            NextDesktopLauncher.Launch(Paths, id, plan);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var owned = await OwnedAsync(id, install.Executable, state.AttemptUtc, token);
                var root = owned.Where(row => EqualPath(row.ExecutablePath, install.Executable) &&
                    EqualPath(CommandLineIdentity.TryGetUserDataPath(row.CommandLine), Paths.UserData(id)))
                    .OrderBy(row => row.StartTimeUtc).FirstOrDefault();
                var server = Path.Combine(Path.GetDirectoryName(install.Executable)!, "resources", "bin", "language_server.exe");
                if (root is not null && state.Root is null)
                {
                    state = state with { Root = new ProcessIdentity(root.ProcessId, root.StartTimeUtc, root.ExecutablePath, Paths.UserData(id)),
                        Processes = owned.Select(row => new ProcessIdentity(row.ProcessId, row.StartTimeUtc, row.ExecutablePath, Paths.UserData(id))).ToList() };
                    await SaveRuntimeAsync(state, token);
                }
                if (root is not null && owned.Any(row => EqualPath(row.ExecutablePath, server)) && await ContainsMarkerAsync(log, token))
                {
                    if (!NextCompatibility.Same(install, await CurrentInstallationAsync(token))) throw new HubException("Installation changed during startup.", "installation_changed");
                    state = state with { State = RuntimeState.Running, FileTokenStore = true,
                        Root = new ProcessIdentity(root.ProcessId, root.StartTimeUtc, root.ExecutablePath, Paths.UserData(id)),
                        Processes = owned.Select(row => new ProcessIdentity(row.ProcessId, row.StartTimeUtc, row.ExecutablePath, Paths.UserData(id))).ToList() };
                    await SaveRuntimeAsync(state, token); return state;
                }
                await Task.Delay(500, token);
            }
            throw new HubException("No independent file token store proof within 120 seconds.", "isolation_scope_unverified");
        }
        catch
        {
            // This attempt's roots only; enumeration failure remains unknown, never means stopped.
            try { await StopOwnedAsync(id, install.Executable, true, state.AttemptUtc, CancellationToken.None); }
            catch (HubException) { state = state with { State = RuntimeState.Unknown }; }
            await SaveRuntimeAsync(state with { State = state.State == RuntimeState.Unknown ? RuntimeState.Unknown : RuntimeState.Failed,
                FileTokenStore = false, Detail = "Startup proof failed; inspect diagnostics before retrying." }, CancellationToken.None);
            throw;
        }
    }
    private static async Task<bool> ContainsMarkerAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 16 * 1024 * 1024) return false;
            using var reader = new StreamReader(stream);
            return (await reader.ReadToEndAsync(token)).Contains("Using file-based token storage", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }
    public async Task<bool> StopAsync(Guid id, bool force = false, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Paths.ProfileLock(id), OperationTimeout, token);
        return await StopUnderProfileLockAsync(id, force, loginHeld: false, token);
    }
    private async Task<bool> StopUnderProfileLockAsync(Guid id, bool force, bool loginHeld, CancellationToken token)
    {
        await Profiles.GetAsync(id, token);
        var state = await RuntimeAsync(id, token);
        var install = state.Installation ?? await CurrentInstallationAsync(token);
        await SaveRuntimeAsync(state with { State = RuntimeState.Stopping }, token);
        try
        {
            var stopped = await StopOwnedAsync(id, install.Executable, force, null, token);
            await SaveRuntimeAsync(state with { State = RuntimeState.Stopped, Root = null, Processes = [], FileTokenStore = false }, token);
            if (loginHeld) await RemoveLoginWaiterAsync(id, token);
            else await EndLoginAsync(id, token);
            return stopped;
        }
        catch
        { var latest = await RuntimeAsync(id, CancellationToken.None); await SaveRuntimeAsync(latest with { State = RuntimeState.Unknown, Detail = "Close incomplete; force stop is a separate explicit action." }, CancellationToken.None); throw; }
    }
    public async Task<NextRuntime> RestartAsync(Guid id, bool force = false, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Paths.ProfileLock(id), OperationTimeout, token);
        await Profiles.GetAsync(id, token);
        var state = await RuntimeAsync(id, token);
        var install = await CurrentInstallationAsync(token); await RequireProofAsync(install, token);
        await StopOwnedAsync(id, state.Installation?.Executable ?? install.Executable, force, null, token);
        await EndLoginAsync(id, token);
        await Bridge.ApplyAsync(id, (await Bridge.ReadAsync(id, token)).Enabled, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), token);
        return await LaunchCoreAsync(id, install, token);
    }
    private async Task<bool> StopOwnedAsync(Guid id, string executable, bool force, DateTimeOffset? notBefore, CancellationToken token)
    {
        var owned = await OwnedAsync(id, executable, notBefore, token);
        if (owned.Count == 0) return false;
        var state = await RuntimeAsync(id, token);
        await SaveRuntimeAsync(state with { Processes = owned.Select(row => new ProcessIdentity(row.ProcessId, row.StartTimeUtc, row.ExecutablePath, Paths.UserData(id))).ToList() }, token);
        var unconfirmedRequests = 0;
        if (force)
        {
            // Kill individually, children first; the controller rechecks image and creation time.
            foreach (var row in owned.OrderByDescending(row => row.StartTimeUtc))
                if (!_controller.Terminate(row)) unconfirmedRequests++;
        }
        else foreach (var row in owned.Where(row => row.MainWindowHandle != 0))
            if (!_controller.TryCloseMainWindow(row)) unconfirmedRequests++;
        await WriteDiagnosticAsync(new HubLogEvent(DateTimeOffset.UtcNow,
            "next-stop", id, null, force ? "force" : "close-window", null,
            "requests-sent", unconfirmedRequests == 0 ? null : "process_request_unconfirmed"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(force ? 10 : 15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { if ((await OwnedAsync(id, executable, notBefore, token)).Count == 0) return true; }
            catch (HubException ex) when (ex.Code == "process_enumeration_unavailable")
            { /* A transient exit/WMI race is still unknown; wait for a verifiable snapshot. */ }
            await Task.Delay(250, token);
        }
        var detail = unconfirmedRequests == 0 ? "" : $" {unconfirmedRequests} process requests could not be confirmed.";
        throw new HubException((force ? "Owned processes remain after force stop." : "The window did not close. Save work and use force stop explicitly if needed.") + detail, "profile_still_running");
    }

    public async Task<CompatibilityProof> ProbeAsync(CancellationToken token = default)
    {
        await using var gate = await FileLock.AcquireAsync(Path.Combine(Paths.LocksRoot, "probe.lock"), OperationTimeout, token);
        var install = await CurrentInstallationAsync(token);
        var scratch = new NextHubService(Path.Combine(Paths.DataRoot, "probes", Guid.NewGuid().ToString("N")));
        await scratch.ConfigureExecutableAsync(install.Executable, token);
        var id = (await scratch.Profiles.CreateAsync("no-login-probe", null, IsolationBackendKind.ProcessLocal, token)).Id;
        var proof = new CompatibilityProof { Installation = install };
        try
        {
            // Keep one lock order for the entire probe transaction; never reenter these gates.
            await using var profileHeld = await FileLock.AcquireAsync(scratch.Paths.ProfileLock(id), OperationTimeout, token);
            await using var loginHeld = await FileLock.AcquireAsync(Path.Combine(scratch.Paths.LocksRoot, "login.lock"), OperationTimeout, token);
            await using var protocolHeld = await FileLock.AcquireAsync(_protocolLockPath, OperationTimeout, token);
            var previous = _protocolCommands.Read();
            try
            {
                var runtime = await scratch.LaunchCoreAsync(id, install, token);
                proof = proof with { FileTokenStore = runtime.FileTokenStore,
                    IndependentHome = Directory.Exists(Path.Combine(scratch.Paths.Home(id), ".gemini")),
                    IndependentUserData = File.Exists(Path.Combine(scratch.Paths.UserData(id), "app_storage.json")) };
            }
            finally
            {
                try { await scratch.StopUnderProfileLockAsync(id, true, loginHeld: true, CancellationToken.None); proof = proof with { ProcessesStopped = true }; }
                finally { proof = proof with { ProtocolRestored = NextProtocol.RestoreProbeProtocolUnderLock(previous,
                    install.Executable, _protocolCommands, CancellationToken.None) }; }
            }
        }
        catch (Exception ex)
        {
            await WriteDiagnosticAsync(new HubLogEvent(DateTimeOffset.UtcNow, "next-probe", null,
                install.Version, Path.GetFileName(scratch.Paths.DataRoot), null, "scratch-retained",
                ex is HubException known ? known.Code : ex is OperationCanceledException ? "probe_cancelled" : "probe_failed"));
            throw;
        }
        if (!NextCompatibility.Same(install, await CurrentInstallationAsync(token))) throw new HubException("Installation changed during probe; proof discarded.", "installation_changed");
        proof = proof with { ProbedUtc = DateTimeOffset.UtcNow };
        await Store.WriteAsync(ProofPath, proof, NextJsonContext.Default.CompatibilityProof, token);
        await FinishProbeCleanupAsync(scratch.Paths.DataRoot, proof);
        return proof;
    }
    private async Task FinishProbeCleanupAsync(string scratch, CompatibilityProof proof)
    {
        string? code = null;
        try { await Task.Run(() => NextProbeCleanup.DeleteOwnedScratch(Paths, scratch,
            proof.ProcessesStopped && proof.FileTokenStore && proof.IndependentHome && proof.IndependentUserData,
            proof.ProtocolRestored, proofSaved: true)); }
        catch (Exception ex) when (ex is HubException or IOException or UnauthorizedAccessException)
        { code = ex is HubException known ? known.Code : "probe_cleanup_failed"; }
        try
        {
            await new StructuredEventLogger(Paths).WriteAsync(new HubLogEvent(DateTimeOffset.UtcNow,
                "next-probe", null, proof.Installation.Version, Path.GetFileName(scratch), null,
                code is null ? "scratch-cleaned" : "scratch-retained", code), CancellationToken.None);
        }
        catch (Exception) { /* The persisted proof and retained scratch are authoritative. */ }
    }

    public async Task BeginLoginAsync(Guid id, CancellationToken token = default)
    {
        await using var profileHeld = await FileLock.AcquireAsync(Paths.ProfileLock(id), OperationTimeout, token);
        await using var held = await FileLock.AcquireAsync(Path.Combine(Paths.LocksRoot, "login.lock"), OperationTimeout, token);
        var status = await StatusAsync(id, token);
        if (status.State != RuntimeState.Running) throw new HubException("Start and prove the profile before beginning login.", "login_profile_not_ready");
        var settings = await Repository.ReadSettingsAsync(token);
        await using var protocolHeld = await FileLock.AcquireAsync(_protocolLockPath, OperationTimeout, token);
        await NextProtocol.ReassertUnderLockAsync(Paths, settings.AntigravityExecutablePath, _protocolCommands, token);
        var queue = await ReadWaitersAsync(token);
        if (queue.Waiting.Any(p => p.ProfileId != id)) throw new HubException("Another profile is awaiting authorization. Complete or cancel it first; signed-in windows can stay parallel.", "login_serialized");
        await _waiters.WriteAsync(queue with { Waiting = [new PendingCallback { ProfileId = id, UserDataPath = Paths.UserData(id), CreatedUtc = DateTimeOffset.UtcNow }] }, token);
    }
    public async Task EndLoginAsync(Guid id, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Path.Combine(Paths.LocksRoot, "login.lock"), OperationTimeout, token);
        await RemoveLoginWaiterAsync(id, token);
    }
    private async Task RemoveLoginWaiterAsync(Guid id, CancellationToken token)
    {
        var queue = await ReadWaitersAsync(token);
        await _waiters.WriteAsync(queue with { Waiting = queue.Waiting.Where(p => p.ProfileId != id).ToList() }, token);
    }
    private async Task<CallbackQueue> ReadWaitersAsync(CancellationToken token)
    {
        var queue = await _waiters.ReadAsync(token);
        if (queue.Waiting is null || queue.Waiting.Any(p => p is null))
            throw new HubException("Authorization waiting records are invalid; no handoff occurred.", "callback_ambiguous");
        var now = DateTimeOffset.UtcNow;
        var active = queue.Waiting.Where(p => p.ProfileId != Guid.Empty && p.CreatedUtc != default &&
            p.CreatedUtc <= now && now - p.CreatedUtc < NextProtocol.MaxPendingAge).ToList();
        if (active.Any(p => !EqualPath(p.UserDataPath, Paths.UserData(p.ProfileId))))
            throw new HubException("Authorization scope does not match its profile; no handoff occurred.", "callback_ambiguous");
        return queue with { Waiting = active };
    }
    public static PendingCallback SelectWaiter(IReadOnlyList<PendingCallback> waiting, DateTimeOffset now)
    {
        var active = waiting.Where(p => p.ProfileId != Guid.Empty && p.CreatedUtc != default && p.CreatedUtc <= now &&
            now - p.CreatedUtc < NextProtocol.MaxPendingAge).ToList();
        if (active.Count != 1) throw new HubException("Callback has no unambiguous profile association; no handoff occurred.", "callback_ambiguous");
        return active[0];
    }
    public async Task<int> RouteCallbackAsync(string uri, CancellationToken token = default)
    {
        var stage = "validate-uri"; Guid? diagnosticProfile = null; int? diagnosticPid = null;
        await WriteCallbackDiagnosticAsync(stage, diagnosticProfile, diagnosticPid, "received", null);
        try
        {
        NextProtocol.ValidateCallbackUri(uri);
        stage = "check-protocol";
        if (!await IsProtocolRegisteredAsync(token)) throw new HubException("Next no longer owns the protocol; callback refused.", "protocol_owned_elsewhere");
        stage = "select-waiter";
        var selected = SelectWaiter((await ReadWaitersAsync(token)).Waiting, DateTimeOffset.UtcNow).ProfileId;
        diagnosticProfile = selected; stage = "acquire-locks";
        await using var profileHeld = await FileLock.AcquireAsync(Paths.ProfileLock(selected), OperationTimeout, token);
        await using var held = await FileLock.AcquireAsync(Path.Combine(Paths.LocksRoot, "login.lock"), OperationTimeout, token);
        var queue = await ReadWaitersAsync(token);
        var id = SelectWaiter(queue.Waiting, DateTimeOffset.UtcNow).ProfileId;
        if (id != selected) throw new HubException("Authorization association changed; no callback handoff.", "callback_ambiguous");
        stage = "check-profile";
        var status = await StatusAsync(id, token);
        if (status.State != RuntimeState.Running) throw new HubException("Waiting profile is no longer a proven live process.", "callback_profile_unknown");
        stage = "check-installation";
        var install = await CurrentInstallationAsync(token); await RequireProofAsync(install, token);
        using var lease = await NextCompatibility.HoldAsync(install, token);
        await using var protocolHeld = await FileLock.AcquireAsync(_protocolLockPath, OperationTimeout, token);
        if (!await NextProtocol.IsRegisteredForUnderLockAsync(Paths, _protocolCommands, token))
            throw new HubException("Next no longer owns the protocol; callback refused.", "protocol_owned_elsewhere");
        var plan = BuildPlan(Paths, id, install);
        var info = new ProcessStartInfo(install.Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Paths.ProfileRoot(id) };
        foreach (var arg in plan.Arguments) info.ArgumentList.Add(arg); info.ArgumentList.Add(uri);
        foreach (var pair in plan.Environment)
            if (pair.Value.Length == 0) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
        stage = "start-handoff";
        using var process = _callbackLauncher.Start(info);
        int handoffId = 0;
        DateTimeOffset? handoffStarted = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_callbackTimeout);
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        try
        {
            handoffId = process.Id;
            diagnosticPid = handoffId;
            stage = "identify-handoff";
            DateTimeOffset started;
            try { started = process.StartTimeUtc; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { throw new HubException("Callback process identity could not be verified; authorization remains pending.", "callback_identity_unknown", ex); }
            if (handoffId <= 0 || started == default)
                throw new HubException("Callback process identity could not be verified; authorization remains pending.", "callback_identity_unknown");
            handoffStarted = started;
            stdout = DrainDiscardAsync(process.StandardOutput, deadline.Token);
            stderr = DrainDiscardAsync(process.StandardError, deadline.Token);
            stage = "wait-handoff-exit";
            await process.WaitForExitAsync(deadline.Token);
            stage = "drain-streams-exit-" + process.ExitCode;
            await stdout; await stderr;
            stage = "verify-root-exit-" + process.ExitCode;
            var finalStatus = await StatusAsync(id, token);
            if (process.ExitCode != 0 || finalStatus.State != RuntimeState.Running || finalStatus.Root is not { } after || status.Root is null ||
                after.ProcessId != status.Root.ProcessId || after.StartTimeUtc != status.Root.StartTimeUtc)
                throw new HubException("The callback did not finish in the already-proven root; authorization remains pending.", "callback_failed");
        }
        catch
        {
            deadline.Cancel();
            // Never infer creation time from a newly opened PID; the held process handle is authoritative.
            if (handoffStarted is { } verifiedStart)
                try
                {
                    if (!process.HasExited) _controller.Terminate(new ProcessSnapshot(handoffId, 0, verifiedStart, install.Executable, "", 0));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            foreach (var drain in new[] { stdout, stderr })
                try { await drain; } catch (OperationCanceledException) { } catch (IOException) { }
            throw;
        }
        stage = "clear-waiter";
        await _waiters.WriteAsync(queue with { Waiting = [] }, token);
        await WriteCallbackDiagnosticAsync("completed", diagnosticProfile, diagnosticPid, "succeeded", null);
        return handoffId; // Never persist the URI or authentication result.
        }
        catch (Exception ex)
        {
            var code = ex is HubException known ? known.Code : ex is OperationCanceledException ? "callback_cancelled_or_timeout" : "callback_operation_failed";
            await WriteCallbackDiagnosticAsync(stage, diagnosticProfile, diagnosticPid, "failed", code);
            throw;
        }
    }
    private async Task WriteCallbackDiagnosticAsync(string stage, Guid? profile, int? process, string result, string? code)
    {
        // Stage names, numeric exit/PID and fixed fault codes only; never URI, argv or exception text.
        await WriteDiagnosticAsync(new HubLogEvent(DateTimeOffset.UtcNow,
            "next-callback", profile, null, stage, process, result, code));
    }
    private async Task WriteDiagnosticAsync(HubLogEvent entry)
    {
        try
        {
            await new StructuredEventLogger(Paths).WriteAsync(entry, CancellationToken.None);
        }
        catch (Exception) { /* Diagnostic failure must not change callback ownership or routing. */ }
    }
    private static async Task DrainDiscardAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
    public async Task RegisterProtocolAsync(string cli, CancellationToken token = default)
    {
        var settings = await Repository.ReadSettingsAsync(token);
        var vendor = NextCompatibility.DiscoveryCandidates(settings.AntigravityExecutablePath).FirstOrDefault(File.Exists);
        await NextProtocol.RegisterAsync(Paths, cli, vendor, _protocolCommands, _protocolLockPath, token);
    }
    public Task<bool> IsProtocolRegisteredAsync(CancellationToken token = default) =>
        NextProtocol.IsRegisteredForAsync(Paths, _protocolCommands, _protocolLockPath, token);
    public Task<bool> RestoreProtocolAsync(CancellationToken token = default) =>
        NextProtocol.RestoreAsync(Paths, _protocolCommands, _protocolLockPath, token);
    public async Task<bool> RollbackAsync(bool force = false, CancellationToken token = default)
    {
        try
        {
            foreach (var profile in await Profiles.ListAsync(token)) await StopAsync(profile.Id, force, token);
        }
        catch (Exception ex) when (ex is HubException or IOException or UnauthorizedAccessException)
        {
            throw new HubException("Rollback stopped because a profile could not be confirmed stopped. Protocol ownership is retained. After resolving the process failure, use configure-deeplink restore for independent protocol recovery.",
                ex is HubException known ? known.Code : "rollback_stop_failed", ex);
        }
        return await RestoreProtocolAsync(token);
    }
}
