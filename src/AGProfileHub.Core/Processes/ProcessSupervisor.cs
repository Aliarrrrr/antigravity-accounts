namespace AGProfileHub.Core;

public sealed class ProcessSupervisor(HubPaths paths, ConfigRepository repository, ProfileManager profiles,
    IAntigravityDriver driver, IProcessCatalog catalog, IProcessLauncher launcher, IProcessController controller, IHubLogger logger,
    int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null)
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForcedTimeout = TimeSpan.FromSeconds(5);
    private readonly ProcessOwnershipResolver ownership = new(paths, repository);

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
        var settings = await repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ExecutableAttestation.RequirePinned(settings.ExecutableSha256);
        // The legacy direct path deliberately uses the plain plan: no
        // process-local token-store hint, so direct instances keep their
        // documented shared-credential behavior.
        var plan = driver.BuildLaunchPlan(installation, id, IsolationBackendKind.Sandboxie);
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

        if (ownership.HasAnyRoot(installation.ExecutablePath, snapshots))
            throw new UnsupportedCapabilityException("Another Antigravity root process is running. The legacy direct path shares one Windows credential and is serial-only; prepare isolation boxes before launching profiles concurrently.");

        cancellationToken.ThrowIfCancellationRequested();
        var barrier = new LaunchBarrierRecord
        {
            AttemptId = Guid.NewGuid(),
            ProfileId = id,
            OriginDataRoot = paths.DataRoot,
            ExecutablePath = Path.GetFullPath(installation.ExecutablePath),
            UserDataPath = Path.GetFullPath(paths.UserData(id)),
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await repository.WriteLaunchBarrierAsync(barrier, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var started = await launcher.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(false);
            var expected = new ProcessIdentity(started.ProcessId, started.StartTimeUtc,
                installation.ExecutablePath, paths.UserData(id));
            barrier = barrier with { Root = expected };
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

        var descendants = ownership.GetDescendantsDeepestFirst(verified.Root, snapshots);
        var failedChildren = descendants.Where(descendant => !controller.Terminate(descendant)).ToList();
        if (failedChildren.Count > 0)
            throw new ProcessOwnershipException($"Could not verify termination of {failedChildren.Count} owned child process(es); root was left running.");

        var verifiedRoot = snapshots.SingleOrDefault(item => ownership.SnapshotMatches(item, verified.Root) &&
            ownership.IsRootCandidate(item, installation.ExecutablePath, verified.Root.UserDataPath, snapshots));
        if (verifiedRoot is null || !controller.Terminate(verifiedRoot))
            throw new ProcessOwnershipException("Root identity could not be revalidated at termination time.");
        var exited = await controller.WaitForExitAsync(verified.Root.ProcessId, ForcedTimeout, cancellationToken).ConfigureAwait(false);
        if (!exited) throw new HubException("Verified process did not exit after force stop.", "stop_failed");

        await repository.DeleteRuntimeAsync(id, cancellationToken).ConfigureAwait(false);
        var final = verified with { State = RuntimeState.Stopped, Root = null, Detail = "Force stopped after ownership revalidation." };
        await SafeLogAsync(new HubLogEvent(DateTimeOffset.UtcNow, "stop", id, installation.Version, null,
            verified.Root.ProcessId, "forced", null), cancellationToken).ConfigureAwait(false);
        return new StopResult(final, true, false);
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

        if (barrier.Root is null && visible.Count == 0 &&
            ownership.PathEquals(barrier.OriginDataRoot, paths.DataRoot) &&
            ownership.PathEquals(barrier.ExecutablePath, installation.ExecutablePath))
        {
            // The recorded attempt never produced a process (it failed before or
            // during creation) and nothing matching it is alive, so the stale
            // latch can be cleared instead of blocking every future launch.
            repository.DeleteLaunchBarrier();
            return;
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
