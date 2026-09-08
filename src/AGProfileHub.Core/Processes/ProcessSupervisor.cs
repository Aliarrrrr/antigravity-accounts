namespace AGProfileHub.Core;

public sealed class ProcessSupervisor(HubPaths paths, ConfigRepository repository, ProfileManager profiles,
    IAntigravityDriver driver, IProcessCatalog catalog, IProcessLauncher launcher, IProcessController controller, IHubLogger logger,
    int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null)
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForcedTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProfileStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var installation = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await catalog.SnapshotAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var status = await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
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
            var status = await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
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
        var status = await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (status.State == RuntimeState.Running)
        {
            var existing = status.Root is null ? null : snapshots.FirstOrDefault(snapshot => SnapshotMatches(snapshot, status.Root));
            if (existing is not null) controller.TryFocus(existing);
            return new LaunchResult(status with { Detail = "Already running; attempted to focus the existing window." }, false);
        }
        if (status.State == RuntimeState.Unknown)
            throw new ProcessOwnershipException(status.Detail ?? "Process ownership is unknown.");

        if (snapshots.Any(snapshot => LooksLikeAnyRoot(snapshot, installation.ExecutablePath, snapshots)))
            throw new UnsupportedCapabilityException("Another Antigravity root process is running. Manual validation found Google account isolation fails when profiles run concurrently on Desktop 2.12.2. Stop the other instance before launching this profile.");

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
        var status = await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (status.State == RuntimeState.Stopped) return new StopResult(status, false, true);
        if (status.State == RuntimeState.Unknown || status.Root is null)
            throw new ProcessOwnershipException(status.Detail ?? "Process ownership is unknown.");

        var rootSnapshot = snapshots.FirstOrDefault(snapshot => SnapshotMatches(snapshot, status.Root) &&
            IsRootCandidate(snapshot, installation.ExecutablePath, status.Root.UserDataPath, snapshots));
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
        var verified = await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
        if (verified.State != RuntimeState.Running || verified.Root is null || !SameIdentity(verified.Root, status.Root))
            throw new ProcessOwnershipException("Process identity changed before force stop; no process was terminated.");

        var descendants = DescendantsOf(verified.Root, snapshots)
            .OrderByDescending(item => Depth(item, snapshots)).ToList();
        var failedChildren = descendants.Where(descendant => !controller.Terminate(descendant)).ToList();
        if (failedChildren.Count > 0)
            throw new ProcessOwnershipException($"Could not verify termination of {failedChildren.Count} owned child process(es); root was left running.");

        var verifiedRoot = snapshots.SingleOrDefault(item => SnapshotMatches(item, verified.Root) &&
            IsRootCandidate(item, installation.ExecutablePath, verified.Root.UserDataPath, snapshots));
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
        return await ResolveAsync(profile, installation, snapshots, cancellationToken).ConfigureAwait(false);
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
                LooksLikeRoot(snapshot, barrier.ExecutablePath, barrier.UserDataPath, snapshots)).ToList()
            : snapshots.Where(snapshot => SnapshotMatches(snapshot, barrier.Root) &&
                IsRootCandidate(snapshot, barrier.ExecutablePath, barrier.UserDataPath, snapshots)).ToList();

        if (visible.Count == 1)
        {
            if (PathEquals(barrier.OriginDataRoot, paths.DataRoot) && PathEquals(barrier.ExecutablePath, installation.ExecutablePath))
            {
                await profiles.GetAsync(barrier.ProfileId, cancellationToken).ConfigureAwait(false);
                var identity = ToIdentity(visible[0], barrier.UserDataPath);
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
        if (barrier is null || barrier.ProfileId != profile.Id || !PathEquals(barrier.OriginDataRoot, paths.DataRoot)) return status;
        if (status.State == RuntimeState.Running)
            return status with { Detail = status.Detail + " A retained launch barrier will be reconciled before the next launch." };
        return status with
        {
            State = RuntimeState.Unknown,
            Root = barrier.Root,
            Detail = "A previous launch is pending or could not be conclusively verified. New launches are blocked until reconciliation."
        };
    }

    private async Task<ProfileStatus> ResolveAsync(ProfileRegistration profile, AntigravityInstallation installation,
        IReadOnlyList<ProcessSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var expectedUserData = paths.UserData(profile.Id);
        var claims = snapshots.Where(snapshot => PathEquals(snapshot.ExecutablePath, installation.ExecutablePath) &&
            PathEquals(CommandLineIdentity.TryGetUserDataPath(snapshot.CommandLine), expectedUserData)).ToList();
        var rootShapedClaims = claims.Where(snapshot => LooksLikeRoot(snapshot, installation.ExecutablePath, expectedUserData, snapshots)).ToList();
        var unverifiable = rootShapedClaims.Any(snapshot => snapshot.StartTimeUtc == DateTimeOffset.MinValue);
        var roots = rootShapedClaims.Where(snapshot => snapshot.StartTimeUtc != DateTimeOffset.MinValue).ToList();
        if (unverifiable) return NewStatus(profile, RuntimeState.Unknown, null, "A process claims this profile but its creation time could not be verified.");
        if (roots.Count > 1) return NewStatus(profile, RuntimeState.Unknown, null, "Multiple root processes claim this profile path.");
        if (roots.Count == 1)
        {
            var root = ToIdentity(roots[0], expectedUserData);
            return NewStatus(profile, RuntimeState.Running, root, "Owned root process verified from live state.");
        }

        var runtime = await repository.ReadRuntimeAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            var samePid = snapshots.FirstOrDefault(snapshot => snapshot.ProcessId == runtime.Root.ProcessId);
            if (samePid is not null && !SnapshotMatches(samePid, runtime.Root))
                return NewStatus(profile, RuntimeState.Unknown, null, "The recorded PID exists with a different creation time, executable, or profile argument.");
        }
        return NewStatus(profile, RuntimeState.Stopped, null, "No owned root process is running.");
    }

    private static bool IsRootCandidate(ProcessSnapshot snapshot, string executablePath, string userDataPath,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (!LooksLikeRoot(snapshot, executablePath, userDataPath, snapshots) || snapshot.StartTimeUtc == DateTimeOffset.MinValue) return false;
        return true;
    }

    private static bool LooksLikeRoot(ProcessSnapshot snapshot, string executablePath, string userDataPath,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (!PathEquals(snapshot.ExecutablePath, executablePath)) return false;
        var args = CommandLineIdentity.Split(snapshot.CommandLine);
        if (args.Skip(1).Any(argument => argument.Equals("--type", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))) return false;
        return !snapshots.Any(parent => parent.ProcessId == snapshot.ParentProcessId &&
            parent.ProcessId != snapshot.ProcessId && PathEquals(parent.ExecutablePath, executablePath) &&
            PathEquals(CommandLineIdentity.TryGetUserDataPath(parent.CommandLine), userDataPath));
    }

    private static bool LooksLikeAnyRoot(ProcessSnapshot snapshot, string executablePath,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (!PathEquals(snapshot.ExecutablePath, executablePath)) return false;
        var args = CommandLineIdentity.Split(snapshot.CommandLine);
        if (args.Skip(1).Any(argument => argument.Equals("--type", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))) return false;
        return !snapshots.Any(parent => parent.ProcessId == snapshot.ParentProcessId &&
            parent.ProcessId != snapshot.ProcessId && PathEquals(parent.ExecutablePath, executablePath));
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
                IsRootCandidate(snapshot, executablePath, expected.UserDataPath, snapshots) && SnapshotMatches(snapshot, expected));
            if (verified is not null) return verified;
            if (attempt + 1 < attempts) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static ProfileStatus NewStatus(ProfileRegistration profile, RuntimeState state, ProcessIdentity? root, string detail) =>
        new(profile.Id, profile.Name, state, root,
            profile.WorkspacePath is not null && !Directory.Exists(profile.WorkspacePath) ? detail + " Workspace is missing." : detail,
            profile.WorkspacePath);

    private static ProcessIdentity ToIdentity(ProcessSnapshot snapshot, string userData) =>
        new(snapshot.ProcessId, snapshot.StartTimeUtc, Path.GetFullPath(snapshot.ExecutablePath), Path.GetFullPath(userData));

    private static bool SnapshotMatches(ProcessSnapshot snapshot, ProcessIdentity identity) =>
        snapshot.ProcessId == identity.ProcessId && snapshot.StartTimeUtc != DateTimeOffset.MinValue &&
        Near(snapshot.StartTimeUtc, identity.StartTimeUtc) && PathEquals(snapshot.ExecutablePath, identity.ExecutablePath) &&
        PathEquals(CommandLineIdentity.TryGetUserDataPath(snapshot.CommandLine), identity.UserDataPath);

    private static bool SameIdentity(ProcessIdentity left, ProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartTimeUtc != DateTimeOffset.MinValue &&
        Near(left.StartTimeUtc, right.StartTimeUtc) && PathEquals(left.ExecutablePath, right.ExecutablePath) &&
        PathEquals(left.UserDataPath, right.UserDataPath);

    private static bool Near(DateTimeOffset left, DateTimeOffset right) => Math.Abs((left - right).TotalSeconds) < 1;

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static IReadOnlyList<ProcessSnapshot> DescendantsOf(ProcessIdentity root, IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var selected = new List<ProcessSnapshot>();
        var parents = new Dictionary<int, DateTimeOffset> { [root.ProcessId] = root.StartTimeUtc };
        bool added;
        do
        {
            added = false;
            foreach (var item in snapshots.Where(item => parents.TryGetValue(item.ParentProcessId, out var parentStarted) &&
                item.ProcessId != root.ProcessId && item.StartTimeUtc != DateTimeOffset.MinValue && item.StartTimeUtc >= parentStarted.AddSeconds(-1)))
            {
                if (selected.Any(existing => existing.ProcessId == item.ProcessId)) continue;
                selected.Add(item); parents[item.ProcessId] = item.StartTimeUtc; added = true;
            }
        } while (added);
        return selected;
    }

    private static int Depth(ProcessSnapshot item, IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var depth = 0; var parent = item.ParentProcessId; var visited = new HashSet<int>();
        while (parent != 0 && visited.Add(parent)) { depth++; parent = snapshots.FirstOrDefault(candidate => candidate.ProcessId == parent)?.ParentProcessId ?? 0; }
        return depth;
    }

    private async Task SafeLogAsync(HubLogEvent entry, CancellationToken cancellationToken)
    {
        try { await logger.WriteAsync(entry, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
    }
}
