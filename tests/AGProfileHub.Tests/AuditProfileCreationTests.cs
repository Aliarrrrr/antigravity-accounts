using AGProfileHub.Core;

internal static class AuditProfileCreationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Audit profile creation duplicates preserve registry and existing contents", DuplicatesAsync),
        ("Audit profile creation concurrent normalized names create one layout", ConcurrentAsync),
        ("Audit profile creation cancellation waiting for lock leaves no layout", WaitingAsync),
        ("Audit profile creation held registration failure cleans empty layout", SaveFailureAsync),
        ("Audit profile creation partial preparation removes owned empty paths", PartialAsync),
        ("Audit profile creation preexisting root is never adopted", ExistingAsync),
        ("Audit profile creation cancellation after preparation compensates without cancellation", CancelPreparedAsync),
        ("Audit profile creation compensation preserves files and unknown children", ContentsAsync),
        ("Audit profile creation reconciliation preserves registered and uncertain layouts", ReconcileAsync),
        ("Audit profile creation compensation rejects replaced junction", JunctionAsync),
        ("Audit profile creation child file blocks preparation without deleting contents", BlockedChildAsync),
        ("Audit profile creation invalid input leaves registration and layout unchanged", InvalidAsync),
        ("Audit profile creation long local paths retain managed compatibility", LongPathAsync),
        ("Audit profile creation native Windows prefix conversion preserves path semantics", PrefixAsync)
    ];
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static HubPaths Paths(string root) => new(Path.Combine(root, "data"), Path.Combine(root, "launch"));
    private static async Task DuplicatesAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root);
        var repository = new ConfigRepository(paths); var manager = new ProfileManager(repository, paths);
        var first = await manager.CreateAsync("Alpha", null, IsolationBackendKind.ProcessLocal);
        var sentinel = Path.Combine(paths.Home(first.Id), "sentinel"); await File.WriteAllTextAsync(sentinel, "keep");
        await repository.UpdateProfilesAsync(d => (d with { Profiles = [first with { Name = " Alpha " }] }, true));
        var before = await File.ReadAllTextAsync(paths.ProfilesFile);
        foreach (var name in new[] { "Alpha", "alpha", " ALPHA " })
        {
            try { await manager.CreateAsync(name, null, IsolationBackendKind.ProcessLocal); throw new Exception("Duplicate accepted."); }
            catch (HubException ex) { Check(ex.Code == "profile_name_conflict", "Wrong duplicate error."); }
        }
        Check(Directory.GetDirectories(paths.ProfilesRoot).Length == 1, "Duplicate leaked layout.");
        Check(await File.ReadAllTextAsync(paths.ProfilesFile) == before && await File.ReadAllTextAsync(sentinel) == "keep", "Existing state changed.");
    }
    private static async Task ConcurrentAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var manager = new ProfileManager(new ConfigRepository(paths), paths);
            try { await manager.CreateAsync(i % 2 == 0 ? " Same " : "same", null, IsolationBackendKind.ProcessLocal); return true; }
            catch (HubException ex) when (ex.Code == "profile_name_conflict") { return false; }
        }));
        Check(results.Count(x => x) == 1 && (await new ConfigRepository(paths).ReadProfilesAsync()).Profiles.Count == 1, "Concurrent registration mismatch.");
        Check(Directory.GetDirectories(paths.ProfilesRoot).Length == 1, "Concurrent loser leaked layout.");
    }
    private static async Task WaitingAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root);
        var manager = new ProfileManager(new ConfigRepository(paths), paths);
        await using var held = await FileLock.AcquireAsync(paths.GlobalConfigLock, TimeSpan.FromSeconds(1), default);
        using var cancel = new CancellationTokenSource(100);
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => manager.CreateAsync("waiting", null, IsolationBackendKind.ProcessLocal, cancel.Token));
        Check(!Directory.EnumerateFileSystemEntries(paths.ProfilesRoot).Any() && !File.Exists(paths.ProfilesFile), "Waiting cancellation changed state.");
    }
    private static async Task SaveFailureAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root);
        var repository = new ConfigRepository(paths); var manager = new ProfileManager(repository, paths);
        await repository.UpdateProfilesAsync(d => (d, true)); var original = await File.ReadAllTextAsync(paths.ProfilesFile);
        using (var reader = new FileStream(paths.ProfilesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            await TestAssert.ThrowsAsync<ConfigurationException>(() => manager.CreateAsync("failure", null, IsolationBackendKind.ProcessLocal));
        Check(!Directory.EnumerateFileSystemEntries(paths.ProfilesRoot).Any(), "Failed save leaked layout.");
        Check(await File.ReadAllTextAsync(paths.ProfilesFile) == original, "Failed save altered registration.");
    }
    private static ProfileRegistration Candidate() => new(Guid.NewGuid(), "candidate", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Backend = IsolationBackendKind.ProcessLocal };
    private static async Task PartialAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        foreach (var count in new[] { 1, 3 })
        {
            var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id);
            var failure = new IOException("controlled preparation failure");
            try
            {
                await repository.CreateProfileAsync(profile, () =>
                {
                    layout.CreateDirectory(paths.ProfileRoot(profile.Id));
                    if (count > 1) { layout.CreateDirectory(paths.UserData(profile.Id)); layout.CreateDirectory(paths.Home(profile.Id)); }
                    throw failure;
                }, ex =>
                {
                    try
                    {
                        using var probe = new FileStream(paths.GlobalConfigLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        throw new Exception("Config lock released before compensation.");
                    }
                    catch (IOException) { }
                    layout.Compensate(ex);
                });
                throw new Exception("Preparation succeeded.");
            }
            catch (IOException ex) { Check(ReferenceEquals(ex, failure), "Primary failure replaced."); }
            Check(!Directory.Exists(paths.ProfileRoot(profile.Id)), "Partial layout leaked.");
        }
        Check((await repository.ReadProfilesAsync()).Profiles.Count == 0, "Partial registration leaked.");
    }
    private static async Task ExistingAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); paths.EnsureProfileLayout(profile.Id);
        var sentinel = Path.Combine(paths.Home(profile.Id), "sentinel"); await File.WriteAllTextAsync(sentinel, "keep");
        var layout = new ProfileCreationLayout(paths, profile.Id);
        await TestAssert.ThrowsAsync<IOException>(() => repository.CreateProfileAsync(profile, layout.Prepare, layout.Compensate));
        Check(await File.ReadAllTextAsync(sentinel) == "keep" && Directory.GetDirectories(paths.ProfileRoot(profile.Id)).Length == 5, "Existing layout changed.");
        Check((await repository.ReadProfilesAsync()).Profiles.Count == 0, "Existing root registered after failure.");
    }
    private static async Task CancelPreparedAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id); using var cancel = new CancellationTokenSource();
        try
        {
            await repository.CreateProfileAsync(profile, () => { layout.Prepare(); cancel.Cancel(); }, layout.Compensate, cancel.Token);
            throw new Exception("Cancellation succeeded.");
        }
        catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancel.Token, "Cancellation identity changed."); }
        Check(!Directory.Exists(paths.ProfileRoot(profile.Id)) && (await repository.ReadProfilesAsync()).Profiles.Count == 0, "Cancellation failed to compensate.");
    }
    private static async Task ContentsAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id);
        var sentinel = Path.Combine(paths.Home(profile.Id), "sentinel"); var unknown = Path.Combine(paths.ProfileRoot(profile.Id), "unknown");
        var failure = new IOException("primary"); var prior = new IOException("prior cleanup"); failure.Data["CleanupFailures"] = prior;
        try
        {
            await repository.CreateProfileAsync(profile, () =>
            {
                layout.Prepare(); File.WriteAllText(sentinel, "keep"); Directory.CreateDirectory(unknown); throw failure;
            }, layout.Compensate);
        }
        catch (IOException ex) { Check(ReferenceEquals(ex, failure), "Compensation masked primary."); }
        Check(await File.ReadAllTextAsync(sentinel) == "keep" && Directory.Exists(unknown), "Non-owned contents removed.");
        Check(failure.Data["CleanupFailures"] is AggregateException errors && errors.InnerExceptions.Contains(prior), "Cleanup causes lost.");
        Check(!Directory.Exists(paths.Temp(profile.Id)) && (await repository.ReadProfilesAsync()).Profiles.Count == 0, "Safe cleanup or registration mismatch.");
    }
    private static async Task ReconcileAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id);
        // Exercise the production transaction's post-commit exception boundary using the real store.
        var failure = new IOException("after commit");
        try
        {
            await repository.CreateProfileAsync(profile, () =>
            {
                layout.Prepare();
                new AtomicJsonStore(paths.DataRoot).WriteAsync(paths.ProfilesFile, new ProfilesDocument { Profiles = [profile] },
                    HubJsonContext.Default.ProfilesDocument, default).GetAwaiter().GetResult();
                throw failure;
            }, layout.Compensate);
        }
        catch (IOException ex) { Check(ReferenceEquals(ex, failure), "Postcommit primary lost."); }
        Check((string?)failure.Data["ProfileCreationOutcome"] == "Registered" && Directory.GetDirectories(paths.ProfileRoot(profile.Id)).Length == 5, "Registered layout removed.");
        var uncertain = Candidate() with { Name = "uncertain" }; var uncertainLayout = new ProfileCreationLayout(paths, uncertain.Id); var unreadable = new IOException("primary before reconciliation");
        try
        {
            await repository.CreateProfileAsync(uncertain, () =>
            {
                uncertainLayout.Prepare(); File.WriteAllText(paths.ProfilesFile, "invalid JSON"); throw unreadable;
            }, uncertainLayout.Compensate);
        }
        catch (IOException ex) { Check(ReferenceEquals(ex, unreadable), "Uncertain primary lost."); }
        Check((string?)unreadable.Data["ProfileCreationOutcome"] == "Unknown" && unreadable.Data["ProfileCreationReconciliationFailure"] is Exception &&
            Directory.GetDirectories(paths.ProfileRoot(uncertain.Id)).Length == 5, "Uncertain layout removed or uncertainty hidden.");
    }
    private static async Task JunctionAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id); layout.Prepare();
        var target = Path.Combine(owned.Root, "target"); Directory.CreateDirectory(target); var sentinel = Path.Combine(target, "sentinel");
        await File.WriteAllTextAsync(sentinel, "keep"); var link = paths.Home(profile.Id); Directory.Delete(link);
        var script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Check(process.ExitCode == 0, "Junction setup failed.");
        try
        {
            var primary = new IOException("controlled failure"); layout.Compensate(primary);
            Check(await File.ReadAllTextAsync(sentinel) == "keep" && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Compensation touched junction target.");
            Check(primary.Data["CleanupFailures"] is AggregateException, "Unsafe cleanup not reported.");
        }
        finally { Directory.Delete(link); }
    }

    private static async Task BlockedChildAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths);
        var profile = Candidate(); var layout = new ProfileCreationLayout(paths, profile.Id); var blocker = paths.Home(profile.Id);
        try
        {
            await repository.CreateProfileAsync(profile, () =>
            {
                layout.CreateDirectory(paths.ProfileRoot(profile.Id)); layout.CreateDirectory(paths.UserData(profile.Id));
                File.WriteAllText(blocker, "keep"); layout.CreateDirectory(blocker);
            }, layout.Compensate);
            throw new Exception("File blocker was accepted.");
        }
        catch (IOException ex) { Check(ex.InnerException is System.ComponentModel.Win32Exception && ex.Data["CleanupFailures"] is AggregateException, "Native or cleanup error lost."); }
        Check(await File.ReadAllTextAsync(blocker) == "keep" && !Directory.Exists(paths.UserData(profile.Id)), "Blocker removed or owned empty child leaked.");
        Check((await repository.ReadProfilesAsync()).Profiles.Count == 0, "Blocked preparation registered.");
    }
    private static async Task InvalidAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = Paths(owned.Root); var repository = new ConfigRepository(paths); var manager = new ProfileManager(repository, paths);
        await TestAssert.ThrowsAsync<HubException>(() => manager.CreateAsync(" ", null, IsolationBackendKind.ProcessLocal));
        await TestAssert.ThrowsAsync<HubException>(() => manager.CreateAsync("valid", Path.Combine(owned.Root, "missing"), IsolationBackendKind.ProcessLocal));
        await TestAssert.ThrowsAsync<ConfigurationException>(() => manager.CreateAsync("valid", null, (IsolationBackendKind)999));
        Check(!Directory.EnumerateFileSystemEntries(paths.ProfilesRoot).Any() && !File.Exists(paths.ProfilesFile), "Invalid input changed state.");
    }

    private static async Task LongPathAsync()
    {
        using var owned = new OwnedTestDirectory(); var root = owned.Root;
        while (root.Length < 280) root = Path.Combine(root, "controlled-long-segment");
        var paths = Paths(root); var repository = new ConfigRepository(paths); var manager = new ProfileManager(repository, paths);
        var profile = await manager.CreateAsync("long-path", null, IsolationBackendKind.ProcessLocal);
        Check(Directory.GetDirectories(paths.ProfileRoot(profile.Id)).Length == 5 &&
            (await repository.ReadProfilesAsync()).Profiles.Single().Id == profile.Id, "Long-path layout or registration incomplete.");
    }

    private static Task PrefixAsync()
    {
        // Pure conversion checks: no network access or filesystem calls for UNC paths.
        foreach (var (input, expected) in new[]
        {
            (@"C:\owned\profile", @"\\?\C:\owned\profile"),
            (@"\\server\share\owned\profile", @"\\?\UNC\server\share\owned\profile"),
            (@"\\?\C:\owned\profile", @"\\?\C:\owned\profile"),
            (@"\\?\UNC\server\share\profile", @"\\?\UNC\server\share\profile"),
            (@"\\.\C:\owned\profile", @"\\.\C:\owned\profile")
        }) Check(ProfileCreationLayout.WindowsDirectoryPath(input) == expected, "Native prefix conversion changed path semantics.");
        return Task.CompletedTask;
    }

}
