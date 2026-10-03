using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AGProfileHub.Core;

internal static class NextCallbackTests
{
    private const string Uri = "antigravity://oauth?code=PRIVATE-CALLBACK-CANARY&state=%22--user-data-dir=C:%5Cother";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Next launch and callback clear inherited rendering overrides", EnvironmentAsync),
        ("Next callback success preserves the lease and URI boundary then clears its waiter", SuccessAsync),
        ("Next callback nonzero exit preserves the pending association", NonzeroAsync),
        ("Next callback launcher failure preserves its waiter and private diagnostics", LauncherFailureAsync),
        ("Next callback timeout terminates only the identified handoff", TimeoutAsync),
        ("Next callback cancellation preserves its waiter and verified process scope", CancellationAsync),
        ("Next callback keeps protocol ownership locked through handoff and waiter commit", CallbackProtocolCommitAsync),
        ("Next callback replacement of the proven root refuses a successful handoff", RootReplacementAsync),
        ("Next callback unreadable or default handoff identity never permits termination", IdentityAsync),
        ("Next callback waiter commit failure preserves authorization evidence", WaiterCommitAsync),
        ("Next callback protocol takeover before launch prevents handoff", OwnershipAsync),
        ("Next callback rechecks its association after acquiring profile and login locks", AssociationAsync),
        ("Next callback malformed waiter scope never reaches a launcher", InvalidWaitersAsync),
        ("Next callback changed installation cannot reuse a saved compatibility proof", FingerprintAsync),
        ("Next login committed before stop cannot leave a waiting slot behind", LoginBeforeStopAsync),
        ("Next login queued behind stop refuses a stopped profile", StopBeforeLoginAsync),
        ("Next login keeps protocol ownership locked through waiting slot commit", LoginProtocolCommitAsync),
        ("Next login lock contention cancels without changing data or ownership", LoginLockCancellationAsync),
        ("Next rollback enumeration failure retains protocol and independent recovery instructions", RollbackEnumerationAsync),
        ("Next completed stop survives unavailable diagnostics and clears its waiting slot", StopDiagnosticsAsync),
        ("Next probe cleanup requires completed recovery and saved proof", ProbeCleanupPredicatesAsync),
        ("Next probe cleanup refuses non-owned and nested directory names", ProbeCleanupNamesAsync),
        ("Next probe cleanup rejects root and nested junctions while preserving host data", ProbeCleanupLinksAsync)
    ];

    private static Task EnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "agprofile-next-env-" + Guid.NewGuid().ToString("N"));
        var paths = new HubPaths(root, Path.Combine(root, "global"));
        var plan = NextHubService.BuildPlan(paths, Guid.NewGuid(), new InstallationFingerprint(@"C:\vendor\Antigravity.exe", "test", new()));
        if (!plan.Environment.TryGetValue("ELECTRON_OZONE_PLATFORM_HINT", out var value) || value != "")
            throw new InvalidOperationException("Next launch and callback inherit ELECTRON_OZONE_PLATFORM_HINT.");
        if (!NextDesktopLauncher.Wrapper(plan).Contains("set \"ELECTRON_OZONE_PLATFORM_HINT=\""))
            throw new InvalidOperationException("Desktop wrapper did not clear the rendering override.");
        return Task.CompletedTask;
    }

    private static async Task SuccessAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); var protectedComponents = 0;
        fixture.Launcher.Starting = info =>
        {
            TestAssert.Check(info.FileName == fixture.Vendor && !info.UseShellExecute && info.ArgumentList.Count == 2 &&
                info.ArgumentList[0] == "--user-data-dir=" + fixture.Hub.Paths.UserData(fixture.Profile.Id) && info.ArgumentList[1] == Uri,
                "Callback URI or profile scope was split, rewritten, or routed through a shell.");
            TestAssert.Check(info.WorkingDirectory == fixture.Hub.Paths.ProfileRoot(fixture.Profile.Id) &&
                info.Environment["HOME"] == fixture.Hub.Paths.Home(fixture.Profile.Id) &&
                !info.Environment.ContainsKey("NODE_OPTIONS") && !info.Environment.ContainsKey("ELECTRON_OZONE_PLATFORM_HINT"), "Callback inherited an unsafe environment or foreign profile home.");
            foreach (var component in fixture.Installation.Components.Keys)
            {
                var path = Path.Combine(Path.GetDirectoryName(fixture.Vendor)!, component);
                try { using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read); }
                catch (IOException) { protectedComponents++; continue; }
                throw new Exception("Handoff started without a component file lease.");
            }
        };
        var pid = await fixture.Hub.RouteCallbackAsync(Uri);
        TestAssert.Check(pid == 501 && protectedComponents == fixture.Installation.Components.Count && fixture.Waiters.Current.Waiting.Count == 0,
            "Successful callback did not preserve the fingerprint lease or consume its waiter.");
        TestAssert.Check(fixture.Controller.Terminated.Count == 0 && fixture.Process.Disposed, "Successful callback killed a process or leaked its handle.");
        using (File.Open(fixture.Vendor, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        await fixture.AssertPrivateAsync();
    }

    private static async Task NonzeroAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); fixture.Process.Code = 7;
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_failed");
        fixture.AssertPending(); TestAssert.Check(fixture.Controller.Terminated.Count == 0, "Already-exited handoff was terminated after a nonzero exit.");
        await fixture.AssertPrivateAsync();
    }

    private static async Task LauncherFailureAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync();
        fixture.Launcher.Starting = _ => throw new HubException("launch failed " + Uri, "callback_failed");
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_failed");
        fixture.AssertPending(); TestAssert.Check(fixture.Controller.Terminated.Count == 0, "Launcher failure invented a process to terminate.");
        await fixture.AssertPrivateAsync();
    }

    private static async Task TimeoutAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(TimeSpan.FromMilliseconds(100));
        fixture.Process.Waiting = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => fixture.Hub.RouteCallbackAsync(Uri));
        fixture.AssertPending(); fixture.AssertOnlyHandoffTerminated(); await fixture.AssertPrivateAsync();
    }

    private static async Task CancellationAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); using var cancelled = new CancellationTokenSource();
        fixture.Process.Waiting = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var operation = fixture.Hub.RouteCallbackAsync(Uri, cancelled.Token);
        await fixture.Process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => operation);
        fixture.AssertPending(); fixture.AssertOnlyHandoffTerminated(); await fixture.AssertPrivateAsync();
    }

    private static async Task RootReplacementAsync()
    {
        foreach (var replacePid in new[] { true, false })
        {
            using var fixture = await CallbackFixture.CreateAsync();
            fixture.Process.Waiting = _ =>
            {
                var replacement = replacePid ? fixture.Catalog.Rows[0] with { ProcessId = 303 } :
                    fixture.Catalog.Rows[0] with { StartTimeUtc = fixture.Catalog.Rows[0].StartTimeUtc.AddSeconds(2) };
                fixture.Catalog.Rows[0] = replacement; return Task.CompletedTask;
            };
            await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_failed");
            fixture.AssertPending(); TestAssert.Check(fixture.Controller.Terminated.Count == 0, "Root replacement caused termination of an unrelated root.");
        }
    }

    private static async Task CallbackProtocolCommitAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); var router = fixture.Commands.Current;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownedAtCommit = false;
        fixture.Process.Waiting = token => release.Task.WaitAsync(token);
        fixture.Waiters.BeforeWrite = (_, _) =>
        { ownedAtCommit = fixture.Commands.Current == router; return Task.CompletedTask; };
        var callback = fixture.Hub.RouteCallbackAsync(Uri);
        await fixture.Process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var restore = fixture.Hub.RestoreProtocolAsync(); var blockedThroughHandoff = false;
        try
        {
            await Task.Delay(100);
            blockedThroughHandoff = !restore.IsCompleted && fixture.Commands.Current == router;
        }
        finally { release.TrySetResult(); }
        TestAssert.Check(await callback.WaitAsync(TimeSpan.FromSeconds(3)) == 501, "Verified callback handoff did not complete.");
        TestAssert.Check(await restore.WaitAsync(TimeSpan.FromSeconds(3)), "Protocol restoration failed after callback consumed its waiter.");
        TestAssert.Check(blockedThroughHandoff && ownedAtCommit && fixture.Waiters.Current.Waiting.Count == 0,
            "Protocol restoration crossed the handoff transaction before its waiting-slot commit.");
        TestAssert.Check(fixture.Commands.Current is null && !File.Exists(fixture.ProtocolRecordPath) && fixture.Controller.Terminated.Count == 0,
            "Completed handoff/restoration retained stale ownership or terminated an unrelated process.");
        await fixture.AssertPrivateAsync();
    }

    private static async Task IdentityAsync()
    {
        foreach (var fault in new[] { "default", "invalid-operation", "win32", "zero-pid" })
        {
            using var fixture = await CallbackFixture.CreateAsync();
            if (fault == "default") fixture.Process.Started = default;
            else if (fault == "zero-pid") fixture.Process.Pid = 0;
            else fixture.Process.IdentityError = fault == "win32" ? new Win32Exception(5) : new InvalidOperationException("unreadable identity");
            await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_identity_unknown");
            fixture.AssertPending(); TestAssert.Check(fixture.Controller.Terminated.Count == 0 && fixture.Process.Disposed,
                "Unverifiable handoff identity permitted PID termination or leaked its process handle.");
        }
    }

    private static async Task WaiterCommitAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); fixture.Waiters.WriteError = new IOException("simulated waiter commit failure");
        await TestAssert.ThrowsAsync<IOException>(() => fixture.Hub.RouteCallbackAsync(Uri));
        fixture.AssertPending(); TestAssert.Check(fixture.Controller.Terminated.Count == 0, "Waiter commit failure killed an already-completed handoff.");
        await fixture.AssertPrivateAsync();
    }

    private static async Task OwnershipAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); var reads = 0;
        fixture.Commands.Reading = () => { if (++reads == 2) fixture.Commands.Current = "foreign-manager callback \"%1\""; };
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "protocol_owned_elsewhere");
        fixture.AssertPending(); TestAssert.Check(fixture.Launcher.Starts == 0 && fixture.Commands.Current == "foreign-manager callback \"%1\"", "Protocol takeover was ignored or overwritten.");
    }

    private static async Task AssociationAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); var other = Guid.NewGuid(); var reads = 0;
        fixture.Waiters.Reading = () =>
        {
            if (++reads == 2) fixture.Waiters.Current = new CallbackQueue { Waiting = [new PendingCallback {
                ProfileId = other, UserDataPath = fixture.Hub.Paths.UserData(other), CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }] };
        };
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_ambiguous");
        TestAssert.Check(fixture.Launcher.Starts == 0 && fixture.Waiters.Current.Waiting.Single().ProfileId == other, "Association race launched or consumed the replacement profile's waiter.");
    }

    private static async Task InvalidWaitersAsync()
    {
        foreach (var fault in new[] { "null-list", "null-item", "foreign-data" })
        {
            using var fixture = await CallbackFixture.CreateAsync();
            fixture.Waiters.Current = fault switch {
                "null-list" => new CallbackQueue { Waiting = null! },
                "null-item" => new CallbackQueue { Waiting = [null!] },
                _ => new CallbackQueue { Waiting = [fixture.Waiters.Current.Waiting[0] with { UserDataPath = fixture.Hub.Paths.UserData(fixture.Profile.Id) + "-other" }] }
            };
            await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "callback_ambiguous");
            TestAssert.Check(fixture.Launcher.Starts == 0 && fixture.Waiters.Writes == 0, "Malformed authorization record caused launch or overwrite.");
        }
    }

    private static async Task FingerprintAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); await File.AppendAllTextAsync(fixture.Vendor, " changed");
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RouteCallbackAsync(Uri), "compatibility_probe_required");
        fixture.AssertPending(); TestAssert.Check(fixture.Launcher.Starts == 0, "A changed installation bypassed its compatibility proof.");
    }

    private static async Task LoginBeforeStopAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); fixture.Waiters.Current = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Waiters.BeforeWrite = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var begin = fixture.Hub.BeginLoginAsync(fixture.Profile.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = fixture.Hub.StopAsync(fixture.Profile.Id);
        var blockedUntilCommit = false;
        try
        {
            await Task.Delay(100);
            blockedUntilCommit = !stop.IsCompleted && fixture.Catalog.Rows.Any(row => row.ProcessId == 101);
        }
        finally { fixture.Waiters.BeforeWrite = null; release.TrySetResult(); }
        await begin.WaitAsync(TimeSpan.FromSeconds(3));
        TestAssert.Check(await stop.WaitAsync(TimeSpan.FromSeconds(3)), "Stop did not confirm the owned process exit.");
        TestAssert.Check(blockedUntilCommit, "Stop crossed the profile lock while login was committing its slot.");
        TestAssert.Check(fixture.Waiters.Current.Waiting.Count == 0 && (await fixture.Hub.StatusAsync(fixture.Profile.Id)).State == RuntimeState.Stopped &&
            fixture.Catalog.Rows.Any(row => row.ProcessId == 202), "Concurrent login left a stale slot or changed an unrelated profile.");
    }

    private static async Task StopBeforeLoginAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); fixture.Waiters.Current = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var snapshots = 0;
        fixture.Catalog.BeforeSnapshot = async token =>
        {
            if (++snapshots == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        var stop = fixture.Hub.StopAsync(fixture.Profile.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var begin = fixture.Hub.BeginLoginAsync(fixture.Profile.Id);
        try
        {
            await Task.Delay(100);
            TestAssert.Check(!begin.IsCompleted && fixture.Waiters.Writes == 0,
                "Login crossed the profile lock while stop was checking process ownership.");
        }
        finally { release.TrySetResult(); }
        TestAssert.Check(await stop.WaitAsync(TimeSpan.FromSeconds(3)), "Stop did not complete after enumeration resumed.");
        await TestAssert.ThrowsCodeAsync<HubException>(() => begin.WaitAsync(TimeSpan.FromSeconds(3)), "login_profile_not_ready");
        TestAssert.Check(fixture.Waiters.Current.Waiting.Count == 0 && fixture.Waiters.Writes == 1 && fixture.Catalog.Rows.Any(row => row.ProcessId == 202),
            "Login accepted a stopped profile or consumed unrelated process scope.");
    }

    private static async Task LoginLockCancellationAsync()
    {
        foreach (var kind in new[] { "profile", "login", "config" })
        {
            using var fixture = await CallbackFixture.CreateAsync(); fixture.Waiters.Current = new();
            var files = new[] { fixture.Hub.Paths.ProfilesFile, fixture.Hub.Paths.SettingsFile, fixture.ProtocolRecordPath, fixture.RuntimePath };
            var before = files.ToDictionary(path => path, File.ReadAllBytes); var command = fixture.Commands.Current;
            var lockPath = kind switch { "profile" => fixture.Hub.Paths.ProfileLock(fixture.Profile.Id),
                "login" => Path.Combine(fixture.Hub.Paths.LocksRoot, "login.lock"), _ => fixture.Hub.Paths.GlobalConfigLock };
            using var cancelled = new CancellationTokenSource();
            await using (var held = await FileLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(1), CancellationToken.None))
            {
                var begin = fixture.Hub.BeginLoginAsync(fixture.Profile.Id, cancelled.Token);
                await Task.Delay(25);
                TestAssert.Check(!begin.IsCompleted, "Contended login did not yield at its " + kind + " lock.");
                cancelled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => begin.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            TestAssert.Check(fixture.Waiters.Writes == 0 && fixture.Commands.Current == command &&
                before.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))), "Cancelled login changed persisted configuration, runtime, protocol or waiting state.");
            await fixture.Hub.BeginLoginAsync(fixture.Profile.Id).WaitAsync(TimeSpan.FromSeconds(3));
            fixture.AssertPending();
        }
    }

    private static async Task LoginProtocolCommitAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); fixture.Waiters.Current = new();
        var router = fixture.Commands.Current;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownedAtCommit = false;
        fixture.Waiters.BeforeWrite = async (_, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            ownedAtCommit = fixture.Commands.Current == router;
        };
        var begin = fixture.Hub.BeginLoginAsync(fixture.Profile.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var restore = fixture.Hub.RestoreProtocolAsync(); var blockedThroughCommit = false;
        try
        {
            await Task.Delay(100);
            blockedThroughCommit = !restore.IsCompleted && fixture.Commands.Current == router;
        }
        finally { release.TrySetResult(); }
        await begin.WaitAsync(TimeSpan.FromSeconds(3));
        TestAssert.Check(await restore.WaitAsync(TimeSpan.FromSeconds(3)), "Protocol restoration failed after login committed its slot.");
        TestAssert.Check(blockedThroughCommit && ownedAtCommit, "Protocol restoration crossed login's waiting-slot transaction and committed an unowned association.");
        fixture.AssertPending();
        TestAssert.Check(fixture.Commands.Current is null && !File.Exists(fixture.ProtocolRecordPath), "Completed restoration retained a stale router registration.");
    }

    private static async Task RollbackEnumerationAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync();
        var command = fixture.Commands.Current; var record = await File.ReadAllBytesAsync(fixture.ProtocolRecordPath);
        fixture.Catalog.SnapshotError = new IOException("simulated enumeration failure");
        var failure = await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Hub.RollbackAsync(force: true), "process_enumeration_unavailable");
        var retainedRecord = await File.ReadAllBytesAsync(fixture.ProtocolRecordPath);
        TestAssert.Check(failure.Message.Contains("configure-deeplink restore", StringComparison.Ordinal) &&
            fixture.Commands.Current == command && record.SequenceEqual(retainedRecord) &&
            fixture.Controller.Terminated.Count == 0 && fixture.Catalog.Rows.Count == 2,
            "Failed rollback changed ownership, discarded recovery evidence, or guessed process liveness.");
        fixture.AssertPending();
        var runtime = await new AtomicJsonStore(fixture.Hub.Paths.DataRoot).ReadAsync(fixture.RuntimePath,
            NextJsonContext.Default.NextRuntime, () => throw new Exception("Runtime recovery record missing."), CancellationToken.None);
        TestAssert.Check(runtime.State == RuntimeState.Unknown && runtime.Root?.ProcessId == 101, "Failed stop discarded its process recovery identity.");
    }

    private static async Task StopDiagnosticsAsync()
    {
        using var fixture = await CallbackFixture.CreateAsync(); FileStream? exclusiveLog = null;
        fixture.Controller.AfterClose = () =>
        {
            var path = Path.Combine(fixture.Hub.Paths.LogsRoot, $"hub-{DateTimeOffset.UtcNow:yyyy-MM}.jsonl");
            exclusiveLog = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            TestAssert.Check(await fixture.Hub.StopAsync(fixture.Profile.Id), "Completed process close was rejected because its log was unavailable.");
            var runtime = await new AtomicJsonStore(fixture.Hub.Paths.DataRoot).ReadAsync(fixture.RuntimePath,
                NextJsonContext.Default.NextRuntime, () => throw new Exception("Stopped runtime missing."), CancellationToken.None);
            TestAssert.Check(runtime.State == RuntimeState.Stopped && runtime.Root is null && !runtime.FileTokenStore &&
                fixture.Waiters.Current.Waiting.Count == 0 && fixture.Catalog.Rows.Single().ProcessId == 202,
                "Diagnostic write failure prevented authoritative state commit or changed an unrelated process.");
        }
        finally { exclusiveLog?.Dispose(); }
    }

    private static async Task ProbeCleanupPredicatesAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = new HubPaths(owned.Root, Path.Combine(owned.Root, "global"));
        var scratch = Path.Combine(paths.DataRoot, "probes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratch, "nested")); var evidence = Path.Combine(scratch, "nested", "evidence");
        await File.WriteAllTextAsync(evidence, "recovery proof");
        foreach (var flags in new[] { (Stopped: false, Restored: true, Saved: true), (Stopped: true, Restored: false, Saved: true), (Stopped: true, Restored: true, Saved: false) })
        {
            await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextProbeCleanup.DeleteOwnedScratch(paths, scratch,
                flags.Stopped, flags.Restored, flags.Saved)), "probe_cleanup_refused");
            TestAssert.Check(await File.ReadAllTextAsync(evidence) == "recovery proof", "Incomplete probe cleanup erased diagnostic evidence.");
        }
        NextProbeCleanup.DeleteOwnedScratch(paths, scratch, true, true, true);
        TestAssert.Check(!Directory.Exists(scratch) && Directory.Exists(Path.GetDirectoryName(scratch)), "Successful probe cleanup removed its parent or retained its GUID directory.");
    }

    private static async Task ProbeCleanupNamesAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = new HubPaths(owned.Root, Path.Combine(owned.Root, "global"));
        var probes = Path.Combine(paths.DataRoot, "probes");
        foreach (var scratch in new[] { probes, Path.Combine(probes, "existing-profile"), Path.Combine(probes, Guid.NewGuid().ToString("D")),
            Path.Combine(probes, "nested", Guid.NewGuid().ToString("N")), Path.Combine(owned.Root, "foreign", Guid.NewGuid().ToString("N")) })
        {
            Directory.CreateDirectory(scratch); var evidence = Path.Combine(scratch, "evidence"); await File.WriteAllTextAsync(evidence, "preserve");
            await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextProbeCleanup.DeleteOwnedScratch(paths, scratch, true, true, true)), "probe_cleanup_refused");
            TestAssert.Check(File.Exists(evidence), "Non-owned directory was removed during probe cleanup.");
        }
    }

    private static async Task ProbeCleanupLinksAsync()
    {
        foreach (var rootLink in new[] { true, false })
        {
            using var owned = new OwnedTestDirectory(); var paths = new HubPaths(Path.Combine(owned.Root, "data"), Path.Combine(owned.Root, "global"));
            var host = Path.Combine(owned.Root, "host"); Directory.CreateDirectory(host);
            var hostFile = Path.Combine(host, "sentinel"); await File.WriteAllTextAsync(hostFile, "FAKE-HOST-DATA");
            var scratch = Path.Combine(paths.DataRoot, "probes", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootLink ? Path.GetDirectoryName(scratch)! : scratch);
            var link = rootLink ? scratch : Path.Combine(scratch, "nested"); await CreateJunctionAsync(link, host);
            try
            {
                await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => NextProbeCleanup.DeleteOwnedScratch(paths, scratch, true, true, true)), "reparse_point_rejected");
                TestAssert.Check(await File.ReadAllTextAsync(hostFile) == "FAKE-HOST-DATA" && new DirectoryInfo(link).LinkTarget is not null,
                    "Probe cleanup followed or removed a junction to host data.");
            }
            finally { Directory.Delete(link, recursive: false); }
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Exception("Could not start the owned junction helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); await stdout; await stderr; throw; }
        await stdout; var error = await stderr;
        TestAssert.Check(process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null, "Owned junction helper failed: " + error);
    }

    private sealed class CallbackFixture : IDisposable
    {
        private readonly OwnedTestDirectory _owned = new();
        internal readonly MemoryCommands Commands = new(); internal readonly MemoryWaiters Waiters = new();
        internal readonly CallbackCatalog Catalog = new(); internal readonly CallbackProcess Process = new();
        internal readonly CallbackLauncher Launcher; internal readonly CallbackController Controller;
        internal readonly NextHubService Hub;
        internal ProfileRegistration Profile = null!; internal InstallationFingerprint Installation = null!;
        internal string Vendor => Path.Combine(_owned.Root, "vendor", "Antigravity.exe");
        internal string Cli => Path.Combine(_owned.Root, "package", "agprofile-next.exe");
        internal string ProtocolRecordPath => Path.Combine(Hub.Paths.DataRoot, "runtime", "deeplink-registration.json");
        internal string RuntimePath => Path.Combine(Hub.Paths.RuntimeStateRoot, "next-" + Profile.Id.ToString("D") + ".json");
        private CallbackFixture(TimeSpan? timeout)
        {
            Launcher = new CallbackLauncher(Process); Controller = new CallbackController(Catalog);
            Hub = new NextHubService(Path.Combine(_owned.Root, "data"), Catalog, Controller, Commands,
                Path.Combine(_owned.Root, "protocol.lock"), Launcher, Waiters, timeout ?? TimeSpan.FromSeconds(5));
        }
        internal static async Task<CallbackFixture> CreateAsync(TimeSpan? timeout = null)
        {
            var fixture = new CallbackFixture(timeout);
            try
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Vendor)!, "resources", "bin"));
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Cli)!);
                foreach (var path in new[] { fixture.Vendor, Path.Combine(Path.GetDirectoryName(fixture.Vendor)!, "resources", "app.asar"),
                    Path.Combine(Path.GetDirectoryName(fixture.Vendor)!, "resources", "bin", "language_server.exe"), Path.Combine(Path.GetDirectoryName(fixture.Vendor)!, "native.dll"), fixture.Cli })
                    await File.WriteAllTextAsync(path, "test-only vendor component");
                await fixture.Hub.ConfigureExecutableAsync(fixture.Vendor); fixture.Installation = await fixture.Hub.CurrentInstallationAsync();
                fixture.Profile = await fixture.Hub.Profiles.CreateAsync("A", null, IsolationBackendKind.ProcessLocal);
                fixture.Hub.Paths.EnsureProfileLayout(fixture.Profile.Id); var start = DateTimeOffset.UtcNow.AddMinutes(-1);
                var data = fixture.Hub.Paths.UserData(fixture.Profile.Id);
                fixture.Catalog.Rows.Add(new ProcessSnapshot(101, 1, start, fixture.Vendor, '"' + fixture.Vendor + "\" \"--user-data-dir=" + data + '"', 1));
                fixture.Catalog.Rows.Add(new ProcessSnapshot(202, 1, start, fixture.Vendor, '"' + fixture.Vendor + "\" \"--user-data-dir=" + data + "-other\"", 1));
                var identity = new ProcessIdentity(101, start, fixture.Vendor, data);
                await new AtomicJsonStore(fixture.Hub.Paths.DataRoot).WriteAsync(Path.Combine(fixture.Hub.Paths.RuntimeStateRoot, "next-" + fixture.Profile.Id.ToString("D") + ".json"),
                    new NextRuntime { ProfileId = fixture.Profile.Id, State = RuntimeState.Running, Root = identity, Processes = [identity],
                        Installation = fixture.Installation, AttemptUtc = start.AddSeconds(-1), FileTokenStore = true }, NextJsonContext.Default.NextRuntime, CancellationToken.None);
                await new AtomicJsonStore(fixture.Hub.Paths.DataRoot).WriteAsync(Path.Combine(fixture.Hub.Paths.DataRoot, "compatibility.json"),
                    new CompatibilityProof { Installation = fixture.Installation, ProbedUtc = DateTimeOffset.UtcNow, FileTokenStore = true,
                        IndependentHome = true, IndependentUserData = true, ProcessesStopped = true, ProtocolRestored = true }, NextJsonContext.Default.CompatibilityProof, CancellationToken.None);
                await fixture.Hub.RegisterProtocolAsync(fixture.Cli);
                fixture.Waiters.Current = new CallbackQueue { Waiting = [new PendingCallback { ProfileId = fixture.Profile.Id,
                    UserDataPath = data, CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }] };
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal void AssertPending() => TestAssert.Check(Waiters.Current.Waiting.Count == 1 && Waiters.Current.Waiting[0].ProfileId == Profile.Id,
            "Callback failure removed or replaced the pending association.");
        internal void AssertOnlyHandoffTerminated()
        {
            TestAssert.Check(Controller.Terminated.Count == 1, "Failed handoff did not terminate exactly its verified process.");
            var row = Controller.Terminated[0];
            TestAssert.Check(row.ProcessId == 501 && row.StartTimeUtc == Process.Started && row.ExecutablePath == Vendor &&
                Catalog.Rows.Any(root => root.ProcessId == 101) && Catalog.Rows.Any(root => root.ProcessId == 202), "Handoff cleanup used an invented identity or changed an unrelated/root process.");
        }
        internal async Task AssertPrivateAsync()
        {
            foreach (var path in Directory.EnumerateFiles(Hub.Paths.DataRoot, "*", SearchOption.AllDirectories))
                TestAssert.Check(!(await File.ReadAllTextAsync(path)).Contains("PRIVATE-CALLBACK-CANARY", StringComparison.Ordinal), "Callback URI or discarded output leaked into durable data.");
        }
        public void Dispose() { Process.Dispose(); _owned.Dispose(); }
    }

    private sealed class MemoryCommands : IProtocolCommandStore
    {
        internal string? Current; internal Action? Reading;
        public string? Read() { Reading?.Invoke(); return Current; }
        public void Write(string command) => Current = command;
        public void Delete() => Current = null;
    }
    private sealed class MemoryWaiters : ICallbackWaiterStore
    {
        internal CallbackQueue Current = new(); internal Action? Reading; internal Exception? WriteError; internal int Writes;
        internal Func<CallbackQueue, CancellationToken, Task>? BeforeWrite;
        public Task<CallbackQueue> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reading?.Invoke(); return Task.FromResult(Current); }
        public async Task WriteAsync(CallbackQueue queue, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (BeforeWrite is not null) await BeforeWrite(queue, token); token.ThrowIfCancellationRequested(); if (WriteError is not null) throw WriteError; Writes++; Current = queue; }
    }
    private sealed class CallbackCatalog : IProcessCatalog
    {
        internal readonly List<ProcessSnapshot> Rows = [];
        internal Exception? SnapshotError; internal Func<CancellationToken, Task>? BeforeSnapshot;
        public async Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string executable, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (BeforeSnapshot is not null) await BeforeSnapshot(token); if (SnapshotError is not null) throw SnapshotError; return Rows.ToArray(); }
    }
    private sealed class CallbackController(CallbackCatalog catalog) : IProcessController
    {
        internal readonly List<ProcessSnapshot> Terminated = [];
        internal Action? AfterClose;
        public bool TryFocus(ProcessSnapshot expected) => false;
        public bool TryCloseMainWindow(ProcessSnapshot expected)
        { var closed = catalog.Rows.RemoveAll(row => row.ProcessId == expected.ProcessId && row.StartTimeUtc == expected.StartTimeUtc) == 1; if (closed) AfterClose?.Invoke(); return closed; }
        public bool Terminate(ProcessSnapshot expected) { Terminated.Add(expected); return true; }
        public Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken token = default) => Task.FromResult(false);
        public ProcessLiveness ProbeLiveness(ProcessIdentity expected) => ProcessLiveness.Unknown;
    }
    private sealed class CallbackLauncher(CallbackProcess process) : ICallbackProcessLauncher
    {
        internal int Starts; internal Action<ProcessStartInfo>? Starting;
        public ICallbackProcess Start(ProcessStartInfo info) { Starts++; Starting?.Invoke(info); return process; }
    }
    private sealed class CallbackProcess : ICallbackProcess
    {
        internal int Pid = 501, Code; internal DateTimeOffset Started = DateTimeOffset.UtcNow;
        internal bool Exited, Disposed; internal Exception? IdentityError; internal Func<CancellationToken, Task>? Waiting;
        internal readonly TaskCompletionSource WaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => Pid;
        public DateTimeOffset StartTimeUtc => IdentityError is null ? Started : throw IdentityError;
        public bool HasExited => Exited;
        public int ExitCode => Code;
        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(Uri + "\nprivate stdout")));
        public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(Uri + "\nprivate stderr")));
        public async Task WaitForExitAsync(CancellationToken token)
        { WaitEntered.TrySetResult(); if (Waiting is not null) await Waiting(token); token.ThrowIfCancellationRequested(); Exited = true; }
        public void Dispose() { if (Disposed) return; Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); }
    }
}
