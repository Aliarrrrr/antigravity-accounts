namespace AGProfileHub.Core;

public sealed class ProcessSupervisor(HubPaths paths, ConfigRepository repository, ProfileManager profiles,
    IAntigravityDriver driver, IProcessCatalog catalog, IProcessLauncher launcher, IProcessController controller, IHubLogger logger,
    ISandboxRuntime? sandbox = null, string? hubCliExecutable = null,
    int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null)
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForcedTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShimTimeout = TimeSpan.FromSeconds(20);
    private readonly ProcessOwnershipResolver ownership = new(paths, repository);
    private readonly ISandboxRuntime _sandbox = sandbox ?? new UnsupportedSandboxRuntime();
    private readonly string? _hubCliExecutable = hubCliExecutable;

    public async Task<ProfileStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var status = await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        return await ApplyLocalBarrierAsync(profile, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProfileStatus>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var registrations = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        if (registrations.Count == 0) return [];
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var result = new List<ProfileStatus>(registrations.Count);
        foreach (var profile in registrations)
        {
            var status = await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
            result.Add(await ApplyLocalBarrierAsync(profile, status, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<LaunchResult> LaunchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        await using var launchHeld = await repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var held = await repository.LockProfileAsync(id, cancellationToken).ConfigureAwait(false);
        return await LaunchCoreAsync(id, installation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LaunchResult> LaunchCoreAsync(Guid id, AntigravityInstallation installation, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var plan = driver.BuildLaunchPlan(installation, id);
        await ReconcileLaunchBarrierAsync(installation, cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var status = await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (status.State == RuntimeState.Running)
        {
            var existing = status.Root is null ? null : snapshots.FirstOrDefault(snapshot => ownership.SnapshotMatches(snapshot, status.Root));
            if (existing is not null) controller.TryFocus(existing);
            return new LaunchResult(status with { Detail = "Already running; attempted to focus the existing window." }, false);
        }
        if (status.State == RuntimeState.Unknown)
            throw new ProcessOwnershipException(status.Detail ?? "Process ownership is unknown.");

        if (profile.Backend == ProfileBackend.Direct)
        {
            // Direct backend cannot isolate the shared Windows credential entry.
            // Keep the historical fail-closed single-root gate.
            if (ownership.HasAnyRoot(installation.ExecutablePath, snapshots))
                throw new UnsupportedCapabilityException("Another Antigravity root process is running. Manual validation found Google account isolation fails when profiles run concurrently on Desktop 2.12.2 with the Direct backend. Stop the other instance, or use a Sandboxie profile.");
        }
        else if (profile.Backend == ProfileBackend.Sandboxie)
        {
            var boxId = profile.BoxId ?? throw new ConfigurationException("Sandboxie profile is missing a box id.", "missing_box_id");
            // A sandbox profile is exclusive to this profile's managed user-data
            // path. Other boxes (and other profiles) may run concurrently.
            var expectedUserData = paths.UserData(id);
            var sameProfileRoots = snapshots.Where(snapshot =>
                ownership.PathEquals(snapshot.ExecutablePath, installation.ExecutablePath) &&
                ownership.PathEquals(CommandLineIdentity.TryGetUserDataPath(snapshot.CommandLine), expectedUserData) &&
                ownership.IsRootCandidate(snapshot, installation.ExecutablePath, expectedUserData, snapshots)).ToList();
            if (sameProfileRoots.Count > 0)
                throw new UnsupportedCapabilityException($"Sandbox '{boxId}' already has a running Antigravity instance for this profile. Stop it before launching again.");
        }
        else
        {
            throw new UnsupportedCapabilityException($"Unsupported profile backend: {profile.Backend}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var barrier = new LaunchBarrierRecord
        {
            AttemptId = Guid.NewGuid(),
            ProfileId = id,
            OriginDataRoot = paths.DataRoot,
            ExecutablePath = Path.GetFullPath(installation.ExecutablePath),
            UserDataPath = Path.GetFullPath(paths.UserData(id)),
            CreatedUtc = DateTimeOffset.UtcNow,
            Backend = profile.Backend,
            BoxId = profile.BoxId
        };
        await repository.WriteLaunchBarrierAsync(barrier, CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (profile.Backend == ProfileBackend.Sandboxie)
                return await LaunchSandboxieCoreAsync(id, profile, installation, plan, cancellationToken).ConfigureAwait(false);
            return await LaunchDirectCoreAsync(id, profile, installation, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (LaunchNotStartedException ex)
        {
            try { repository.DeleteLaunchBarrier(); }
            catch (Exception clearError)
            {
                throw new LaunchStateUncertainException("The process was not created, but the global launch reservation could not be cleared. New launches remain blocked.", clearError);
            }
            await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "launch", id, installation.Version,
                plan.SanitizedCommandTemplate, null, "failed", ex.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HubException ex)
        {
            await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "launch", id, installation.Version,
                plan.SanitizedCommandTemplate, null, "failed", ex.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LaunchResult> LaunchDirectCoreAsync(Guid id, ProfileRegistration profile,
        AntigravityInstallation installation, LaunchPlan plan, CancellationToken cancellationToken)
    {
        var started = await launcher.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(false);
        var expected = new ProcessIdentity(started.ProcessId, started.StartTimeUtc,
            installation.ExecutablePath, paths.UserData(id));
        var barrier = new LaunchBarrierRecord
        {
            AttemptId = Guid.NewGuid(),
            ProfileId = id,
            OriginDataRoot = paths.DataRoot,
            ExecutablePath = Path.GetFullPath(installation.ExecutablePath),
            UserDataPath = Path.GetFullPath(paths.UserData(id)),
            CreatedUtc = DateTimeOffset.UtcNow,
            Root = expected,
            Backend = ProfileBackend.Direct
        };
        await repository.WriteLaunchBarrierAsync(barrier, CancellationToken.None).ConfigureAwait(false);
        var verified = await FindLaunchedProcessAsync(installation.ExecutablePath, expected, CancellationToken.None).ConfigureAwait(false);
        if (verified is null)
            throw new LaunchStateUncertainException("Antigravity started but its process identity is not yet visible. The global launch barrier was retained and all new launches remain blocked.");

        await repository.WriteRuntimeAsync(new RuntimeRecord { ProfileId = id, Root = expected, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        repository.DeleteLaunchBarrier();
        await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "launch", id, installation.Version,
            plan.SanitizedCommandTemplate, started.ProcessId, "started", null), CancellationToken.None).ConfigureAwait(false);
        return new LaunchResult(new ProfileStatus(id, profile.Name, RuntimeState.Running, expected,
            "Started and verified.", profile.WorkspacePath), true);
    }

    private async Task<LaunchResult> LaunchSandboxieCoreAsync(Guid id, ProfileRegistration profile,
        AntigravityInstallation installation, LaunchPlan plan, CancellationToken cancellationToken)
    {
        var boxId = profile.BoxId!;
        if (!_sandbox.IsAvailable)
            throw new UnsupportedCapabilityException(_sandbox.RuntimeDescription);

        paths.EnsureProfileLayout(id);
        var openPaths = new List<string>
        {
            paths.Home(id), paths.Roaming(id), paths.Local(id), paths.Temp(id), paths.UserData(id),
            Path.GetDirectoryName(installation.ExecutablePath) ?? installation.ExecutablePath
        };
        await _sandbox.EnsureBoxAsync(boxId, openPaths, cancellationToken).ConfigureAwait(false);

        // Claim an in-box gemini:antigravity entry on first launch only.
        // forceFresh is safe only before any successful sign-in in this box.
        var marker = await repository.ReadKeyringMarkerAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (marker is null)
            await ClaimKeyringInBoxAsync(id, boxId, plan, cancellationToken).ConfigureAwait(false);

        await _sandbox.StartInBoxAsync(boxId, installation.ExecutablePath, plan.Arguments, plan.Environment, cancellationToken).ConfigureAwait(false);

        var expectedUserData = Path.GetFullPath(paths.UserData(id));
        var identity = await DiscoverSandboxieRootAsync(installation.ExecutablePath, expectedUserData, cancellationToken).ConfigureAwait(false)
            ?? throw new LaunchStateUncertainException("Sandboxie accepted the start request but no Antigravity root process became visible. The launch barrier was retained.");

        var barrier = new LaunchBarrierRecord
        {
            AttemptId = Guid.NewGuid(),
            ProfileId = id,
            OriginDataRoot = paths.DataRoot,
            ExecutablePath = Path.GetFullPath(installation.ExecutablePath),
            UserDataPath = expectedUserData,
            CreatedUtc = DateTimeOffset.UtcNow,
            Root = identity,
            Backend = ProfileBackend.Sandboxie,
            BoxId = boxId
        };
        await repository.WriteLaunchBarrierAsync(barrier, CancellationToken.None).ConfigureAwait(false);
        await repository.WriteRuntimeAsync(new RuntimeRecord { ProfileId = id, Root = identity, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        repository.DeleteLaunchBarrier();
        await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "launch", id, installation.Version,
            $"sandbox:{boxId} {plan.SanitizedCommandTemplate}", identity.ProcessId, "started", null), CancellationToken.None).ConfigureAwait(false);
        return new LaunchResult(new ProfileStatus(id, profile.Name, RuntimeState.Running, identity,
            $"Started in sandbox '{boxId}' and verified the vendor root process.", profile.WorkspacePath), true);
    }

    private async Task ClaimKeyringInBoxAsync(Guid id, string boxId, LaunchPlan plan, CancellationToken cancellationToken)
    {
        var cli = _hubCliExecutable ?? Environment.ProcessPath
            ?? throw new HubException("The Hub CLI path is unknown; cannot run credential-shim inside the sandbox.", "cli_path_unknown");
        if (!File.Exists(cli))
            throw new HubException("The Hub CLI path does not exist; cannot run credential-shim inside the sandbox.", "cli_path_unknown");

        // Run the Hub's own credential-shim inside the box so the write lands in
        // the sandbox credential store, not on the host.
        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.OrdinalIgnoreCase);
        // Exit 11 = newly claimed, 0 = already owned. Other codes are failures.
        try
        {
            await _sandbox.RunInBoxAsync(boxId, cli, ["credential-shim", "--force", "--fresh"], environment, ShimTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HubException ex) when (ex.Code == "sandbox_command_failed")
        {
            // Re-run is not possible without capturing the exit code. Treat as failure.
            throw new HubException("credential-shim failed inside the sandbox. The profile was not launched.", "keyring_shim_failed", ex);
        }

        // Placeholder size is deliberately not a valid token; record only size.
        var after = AntigravityKeyring.DescribeLocalEntry();
        var marker = new KeyringShimMarker
        {
            ProfileId = id,
            Target = AntigravityKeyring.Target,
            ClaimedUtc = DateTimeOffset.UtcNow,
            ExitCode = 11,
            BlobSizeAfter = after.BlobSize
        };
        await repository.WriteKeyringMarkerAsync(marker, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ProcessIdentity?> DiscoverSandboxieRootAsync(string executablePath, string userDataPath, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, launchVerificationAttempts);
        var delay = launchVerificationDelay ?? TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = await catalog.SnapshotAsync(executablePath, cancellationToken).ConfigureAwait(false);
            var roots = snapshots.Where(snapshot =>
                ownership.PathEquals(snapshot.ExecutablePath, executablePath) &&
                ownership.PathEquals(CommandLineIdentity.TryGetUserDataPath(snapshot.CommandLine), userDataPath) &&
                ownership.IsRootCandidate(snapshot, executablePath, userDataPath, snapshots)).ToList();
            if (roots.Count == 1) return ownership.ToIdentity(roots[0], userDataPath);
            if (roots.Count > 1)
                throw new ProcessOwnershipException("Multiple Antigravity root processes claim this sandboxed profile.");
            if (attempt + 1 < attempts) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<StopResult> StopAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        await using var launchHeld = await repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var held = await repository.LockProfileAsync(id, cancellationToken).ConfigureAwait(false);
        return await StopCoreAsync(id, force, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StopResult> StopCoreAsync(Guid id, bool force, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var status = await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (status.State == RuntimeState.Stopped) return new StopResult(status, false, true);
        if (status.State == RuntimeState.Unknown || status.Root is null)
            throw new ProcessOwnershipException(status.Detail ?? "Process ownership is unknown.");

        var rootSnapshot = snapshots.FirstOrDefault(snapshot => ownership.SnapshotMatches(snapshot, status.Root) &&
            ownership.IsRootCandidate(snapshot, installation.ExecutablePath, status.Root.UserDataPath, snapshots));
        if (rootSnapshot is null)
            throw new ProcessOwnershipException("The running root process could not be revalidated before close.");

        var closeRequested = controller.TryCloseMainWindow(rootSnapshot);
        if (closeRequested && await controller.WaitForExitAsync(status.Root.ProcessId, GracefulTimeout, cancellationToken).ConfigureAwait(false))
        {
            await repository.DeleteRuntimeAsync(id, cancellationToken).ConfigureAwait(false);
            var stopped = status with { State = RuntimeState.Stopped, Root = null, Detail = "Stopped gracefully." };
            await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "stop", id, installation.Version, null,
                status.Root.ProcessId, "graceful", null), cancellationToken).ConfigureAwait(false);
            return new StopResult(stopped, true, true);
        }

        if (!force)
        {
            var stillRunning = status with { Detail = "Graceful close did not complete. Retry with explicit force after reviewing unsaved work." };
            return new StopResult(stillRunning, true, false);
        }

        snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var verified = await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (verified.State != RuntimeState.Running || verified.Root is null || !ownership.SameIdentity(verified.Root, status.Root))
            throw new ProcessOwnershipException("Process identity changed before force stop; no process was terminated.");

        if (profile.Backend == ProfileBackend.Sandboxie && !string.IsNullOrWhiteSpace(profile.BoxId))
        {
            // Prefer box-scoped terminate; never use a global terminate_all.
            await _sandbox.TerminateBoxAsync(profile.BoxId, cancellationToken).ConfigureAwait(false);
            var exited = await controller.WaitForExitAsync(verified.Root.ProcessId, ForcedTimeout, cancellationToken).ConfigureAwait(false);
            if (!exited)
                throw new HubException("Sandbox terminate completed but the vendor root process did not exit.", "stop_failed");
            await repository.DeleteRuntimeAsync(id, cancellationToken).ConfigureAwait(false);
            var final = verified with { State = RuntimeState.Stopped, Root = null, Detail = $"Force stopped sandbox '{profile.BoxId}'." };
            await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "stop", id, installation.Version, null,
                verified.Root.ProcessId, "forced", null), cancellationToken).ConfigureAwait(false);
            return new StopResult(final, true, false);
        }

        var descendants = ownership.GetDescendantsDeepestFirst(verified.Root, snapshots);
        var failedChildren = descendants.Where(descendant => !controller.Terminate(descendant)).ToList();
        if (failedChildren.Count > 0)
            throw new ProcessOwnershipException($"Could not verify termination of {failedChildren.Count} owned child process(es); root was left running.");

        var verifiedRoot = snapshots.SingleOrDefault(item => ownership.SnapshotMatches(item, verified.Root) &&
            ownership.IsRootCandidate(item, installation.ExecutablePath, verified.Root.UserDataPath, snapshots));
        if (verifiedRoot is null || !controller.Terminate(verifiedRoot))
            throw new ProcessOwnershipException("Root identity could not be revalidated at termination time.");
        var forceExited = await controller.WaitForExitAsync(verified.Root.ProcessId, ForcedTimeout, cancellationToken).ConfigureAwait(false);
        if (!forceExited) throw new HubException("Verified process did not exit after force stop.", "stop_failed");

        await repository.DeleteRuntimeAsync(id, cancellationToken).ConfigureAwait(false);
        var directFinal = verified with { State = RuntimeState.Stopped, Root = null, Detail = "Force stopped after ownership revalidation." };
        await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "stop", id, installation.Version, null,
            verified.Root.ProcessId, "forced", null), cancellationToken).ConfigureAwait(false);
        return new StopResult(directFinal, true, false);
    }

    public async Task<LaunchResult> RestartAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        await using var launchHeld = await repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var held = await repository.LockProfileAsync(id, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusCoreAsync(id, installation, cancellationToken).ConfigureAwait(false);
        if (status.State == RuntimeState.Running)
        {
            var stopped = await StopCoreAsync(id, force, cancellationToken).ConfigureAwait(false);
            if (stopped.Status.State != RuntimeState.Stopped)
                throw new HubException(stopped.Status.Detail ?? "Restart requires the running instance to stop first.", "graceful_close_pending");
        }
        else if (status.State == RuntimeState.Unknown)
            throw new ProcessOwnershipException(status.Detail ?? "Process ownership is unknown.");
        return await LaunchCoreAsync(id, installation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileStatus> GetStatusCoreAsync(Guid id, AntigravityInstallation installation, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        return await ownership.ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileLaunchBarrierAsync(AntigravityInstallation installation, CancellationToken cancellationToken)
    {
        var barrier = await repository.ReadLaunchBarrierAsync(cancellationToken).ConfigureAwait(false);
        if (barrier is null) return;

        IReadOnlyList<ProcessSnapshot> snapshots;
        try { snapshots = await catalog.SnapshotAsync(barrier.ExecutablePath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LaunchStateUncertainException("A previous launch is pending and its live process state could not be inspected. New launches remain blocked.", ex);
        }

        var visible = barrier.Root is null
            ? snapshots.Where(snapshot => snapshot.StartTimeUtc != DateTimeOffset.MinValue &&
                ownership.LooksLikeRoot(snapshot, barrier.ExecutablePath, barrier.UserDataPath, snapshots)).ToList()
            : snapshots.Where(snapshot => ownership.SnapshotMatches(snapshot, barrier.Root) &&
                ownership.IsRootCandidate(snapshot, barrier.ExecutablePath, barrier.UserDataPath, snapshots)).ToList();

        if (visible.Count == 1)
        {
            if (ownership.PathEquals(barrier.OriginDataRoot, paths.DataRoot) && ownership.PathEquals(barrier.ExecutablePath, installation.ExecutablePath))
            {
                await profiles.GetAsync(barrier.ProfileId, cancellationToken).ConfigureAwait(false);
                var identity = ownership.ToIdentity(visible[0], barrier.UserDataPath);
                await repository.WriteRuntimeAsync(new RuntimeRecord
                {
                    ProfileId = barrier.ProfileId,
                    Root = identity,
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None).ConfigureAwait(false);
                repository.DeleteLaunchBarrier();
                return;
            }
            throw new UnsupportedCapabilityException("Another Hub data root has a running or pending Antigravity instance. Stop it before launching from this data root.");
        }

        if (barrier.Root is not null && controller.ProbeLiveness(barrier.Root) == ProcessLiveness.NotFound)
        {
            repository.DeleteLaunchBarrier();
            return;
        }

        throw new LaunchStateUncertainException("A previous launch did not reach a conclusively verified or exited state. The global launch barrier was retained; inspect Doctor and the official application before retrying.");
    }

    private async Task<ProfileStatus> ApplyLocalBarrierAsync(ProfileRegistration profile, ProfileStatus status, CancellationToken cancellationToken)
    {
        var barrier = await repository.ReadLaunchBarrierAsync(cancellationToken).ConfigureAwait(false);
        if (barrier is null || barrier.ProfileId != profile.Id || !ownership.PathEquals(barrier.OriginDataRoot, paths.DataRoot)) return status;
        if (status.State == RuntimeState.Running)
            return status with { Detail = status.Detail + " A retained launch barrier will be reconciled before the next launch." };
        return status with
        {
            State = RuntimeState.Unknown,
            Root = barrier.Root,
            Detail = "A previous launch is pending or could not be conclusively verified. New launches are blocked until reconciliation."
        };
    }

    private async Task<ProcessSnapshot?> FindLaunchedProcessAsync(string executablePath, ProcessIdentity expected,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, launchVerificationAttempts);
        var delay = launchVerificationDelay ?? TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var snapshots = await catalog.SnapshotAsync(executablePath, cancellationToken).ConfigureAwait(false);
            var verified = snapshots.FirstOrDefault(snapshot => snapshot.ProcessId == expected.ProcessId &&
                ownership.IsRootCandidate(snapshot, executablePath, expected.UserDataPath, snapshots) && ownership.SnapshotMatches(snapshot, expected));
            if (verified is not null) return verified;
            if (attempt + 1 < attempts) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task SafeLogAsync(HubLogEvent entry, CancellationToken cancellationToken)
    {
        try { await logger.WriteAsync(entry, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
    }
}
