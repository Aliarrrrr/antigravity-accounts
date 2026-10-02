namespace AGProfileHub.Core;

internal sealed class ProcessOwnershipResolver(HubPaths paths, ConfigRepository repository)
{
    internal async Task<ProfileStatus> ResolveAsync(ProfileRegistration profile, AntigravityInstallation installation,
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

    internal bool HasAnyRoot(string executablePath, IReadOnlyList<ProcessSnapshot> snapshots) =>
        snapshots.Any(snapshot => LooksLikeAnyRoot(snapshot, executablePath, snapshots));

    internal bool IsRootCandidate(ProcessSnapshot snapshot, string executablePath, string userDataPath,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (!LooksLikeRoot(snapshot, executablePath, userDataPath, snapshots) || snapshot.StartTimeUtc == DateTimeOffset.MinValue) return false;
        return true;
    }

    internal bool LooksLikeRoot(ProcessSnapshot snapshot, string executablePath, string userDataPath,
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

    internal bool SnapshotMatches(ProcessSnapshot snapshot, ProcessIdentity identity) =>
        snapshot.ProcessId == identity.ProcessId && snapshot.StartTimeUtc != DateTimeOffset.MinValue &&
        Near(snapshot.StartTimeUtc, identity.StartTimeUtc) && PathEquals(snapshot.ExecutablePath, identity.ExecutablePath) &&
        PathEquals(CommandLineIdentity.TryGetUserDataPath(snapshot.CommandLine), identity.UserDataPath);

    internal bool SameIdentity(ProcessIdentity left, ProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartTimeUtc != DateTimeOffset.MinValue &&
        Near(left.StartTimeUtc, right.StartTimeUtc) && PathEquals(left.ExecutablePath, right.ExecutablePath) &&
        PathEquals(left.UserDataPath, right.UserDataPath);

    internal bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal ProcessIdentity ToIdentity(ProcessSnapshot snapshot, string userData) =>
        new(snapshot.ProcessId, snapshot.StartTimeUtc, Path.GetFullPath(snapshot.ExecutablePath), Path.GetFullPath(userData));

    internal IReadOnlyList<ProcessSnapshot> GetDescendantsDeepestFirst(ProcessIdentity root, IReadOnlyList<ProcessSnapshot> snapshots) =>
        DescendantsOf(root, snapshots).OrderByDescending(item => Depth(item, snapshots)).ToList();

    private static ProfileStatus NewStatus(ProfileRegistration profile, RuntimeState state, ProcessIdentity? root, string detail) =>
        new(profile.Id, profile.Name, state, root,
            profile.WorkspacePath is not null && !Directory.Exists(profile.WorkspacePath) ? detail + " Workspace is missing." : detail,
            profile.WorkspacePath);

    private static bool Near(DateTimeOffset left, DateTimeOffset right) => Math.Abs((left - right).TotalSeconds) < 1;

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

    private bool LooksLikeAnyRoot(ProcessSnapshot snapshot, string executablePath,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (!PathEquals(snapshot.ExecutablePath, executablePath)) return false;
        var args = CommandLineIdentity.Split(snapshot.CommandLine);
        if (args.Skip(1).Any(argument => argument.Equals("--type", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))) return false;
        return !snapshots.Any(parent => parent.ProcessId == snapshot.ParentProcessId &&
            parent.ProcessId != snapshot.ProcessId && PathEquals(parent.ExecutablePath, executablePath));
    }
}
