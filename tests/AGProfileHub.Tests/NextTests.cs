using System.Text.Json;
using AGProfileHub.Core;

internal static class NextTests
{
    public static Task DesktopLaunchAsync() => RunDesktopProbeAsync(async (paths, id) =>
    {
        var marker = Path.Combine(paths.ProfileRoot(id), "desktop-launch-test.txt");
        var script = "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "','ready')";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var plan = new LaunchPlan(executable, "test", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            new Dictionary<string, string>(), "Harmless desktop launch marker");
        NextDesktopLauncher.Launch(paths, id, plan);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!File.Exists(marker) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
        Check(File.Exists(marker), "Desktop adapter did not execute the existing default-root wrapper.");
        Check(await File.ReadAllTextAsync(marker) == "ready", "Unexpected desktop marker.");
    });
    internal static async Task RunDesktopProbeAsync(Func<HubPaths, Guid, Task> operation)
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityProfileHubNext", "probes");
        using var owned = new OwnedTestDirectory(parent, "");
        var root = owned.Root;
        var paths = new HubPaths(root, Path.Combine(root, "runtime", "global"));
        var id = Guid.NewGuid(); paths.EnsureProfileLayout(id);
        await operation(paths, id);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Root() { var path = Path.Combine(Path.GetTempPath(), "agprofile-next-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    public static Task UpgradeAsync()
    {
        var old = new InstallationFingerprint(@"C:\vendor\Antigravity.exe", "2.19.1", new() { ["exe"] = "A", ["app.asar"] = "B", ["ls"] = "C" });
        var proof = new CompatibilityProof { Installation = old, ProbedUtc = DateTimeOffset.UtcNow, FileTokenStore = true,
            IndependentHome = true, IndependentUserData = true, ProcessesStopped = true, ProtocolRestored = true };
        Check(NextCompatibility.PermitsLaunch(proof, old), "Experimental proof should permit launch without claiming manual acceptance.");
        Check(proof.ManuallyAcceptedUtc is null, "Probe must not imply manual acceptance.");
        Check(!NextCompatibility.PermitsLaunch(proof, old with { Version = "2.20.0" }), "Version upgrade must invalidate proof.");
        Check(!NextCompatibility.PermitsLaunch(proof, old with { Components = new() { ["exe"] = "A", ["app.asar"] = "CHANGED", ["ls"] = "C" } }), "Package change must invalidate proof.");
        Check(!NextCompatibility.PermitsLaunch(proof with { StrategyRevision = 99 }, old), "Strategy upgrade must invalidate proof.");
        Check(typeof(NextHubService).Assembly.GetType("AGProfileHub.Core.HubBootstrap") is null &&
            typeof(NextHubService).Assembly.GetType("AGProfileHub.Core.ProcessLocalBackend") is null, "Legacy launch bypass shipped in production.");
        return Task.CompletedTask;
    }
    public static Task OwnershipAsync()
    {
        var time = DateTimeOffset.UtcNow;
        ProcessSnapshot Row(int id, int parent, string image, string data, int offset = 0) => new(id, parent, time.AddSeconds(offset), image,
            '"' + image + "\" \"--user-data-dir=" + data + '"', 0);
        var exe = @"C:\vendor\Antigravity.exe"; var data = @"C:\profiles\a\user-data";
        var rows = new[] { Row(100, 1, exe, data), Row(200, 1, exe, data + "-other"), Row(300, 1, @"C:\other\Antigravity.exe", data),
            Row(400, 100, @"C:\vendor\language_server.exe", "", 1), Row(500, 100, @"C:\foreign.exe", "", -30) };
        var owned = NextHubService.SelectOwned(rows, exe, data);
        Check(owned.Select(r => r.ProcessId).Order().SequenceEqual(new[] { 100, 400 }), "Path/image/parent creation guard failed.");
        Check(NextHubService.SelectOwned(rows, exe, data, time.AddSeconds(30)).Count == 0, "Old/reused roots cannot belong to a new attempt.");
        return Task.CompletedTask;
    }
    public static async Task JsoncAsync()
    {
        var root = Root(); var paths = new HubPaths(root, Path.Combine(root, "global")); paths.EnsureBaseLayout(); var id = Guid.NewGuid(); paths.EnsureProfileLayout(id);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var target = Path.Combine(paths.UserData(id), "User", "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var original = "{\n // retain comment\n \"editor.tabSize\": 2 // trailing comment\n}\n// example object }";
        await File.WriteAllTextAsync(target, original);
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "{ // JSONC\n\"editor.fontSize\":19,\"editor.tabSize\":4,\"extension.secret\":\"CANARY\",\"files.autoSave\":\"afterDelay\",}");
        var preview = await NextConfiguration.InheritAsync(paths, id, source, false);
        Check(await File.ReadAllTextAsync(target) == original, "Preview changed destination.");
        Check(preview.Conflicts.Contains("editor.tabSize") && preview.Skipped.Contains("extension.secret"), "Conflicts/secrets must be reported.");
        await NextConfiguration.InheritAsync(paths, id, source, true);
        var result = await File.ReadAllTextAsync(target);
        Check(result.Contains("retain comment") && !result.Contains("CANARY") && result.Contains("19"), "JSONC merge lost comments or copied secret.");
        Check(await File.ReadAllTextAsync(target + ".bak") == original, "Backup must preserve exact original.");
        using var parsed = JsonDocument.Parse(result, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Check(parsed.RootElement.GetProperty("editor.tabSize").GetInt32() == 2, "Conflict overwritten.");
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "{ broken");
        await TestAssert.ThrowsAsync<JsonException>(() => NextConfiguration.InheritAsync(paths, id, source, true));
        Check(await File.ReadAllTextAsync(target) == result, "Malformed input changed target.");
    }
    public static async Task LockedWriteAsync()
    {
        var root = Root(); var target = Path.Combine(root, "settings.json"); await File.WriteAllTextAsync(target, "original");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var refused = false;
            try { await NextConfiguration.WriteTextAtomicAsync(root, target, "replacement", CancellationToken.None); }
            catch (IOException) { refused = true; }
            catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows() && ex.HResult == unchecked((int)0x80070005))
            { refused = true; } // Windows ReplaceFile reports denied access for a target without FileShare.Delete.
            Check(refused, "An open target without delete sharing allowed replacement.");
        }
        Check(await File.ReadAllTextAsync(target) == "original", "Failed atomic write changed destination.");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Owned temp leaked.");
        await NextConfiguration.WriteTextAtomicAsync(root, target, "replacement", CancellationToken.None);
        Check(await File.ReadAllTextAsync(target) == "replacement", "Closing the sharing lock did not release the controlled failure.");
    }
    public static async Task BridgeAsync()
    {
        var root = Root(); var host = Path.Combine(root, "host"); Directory.CreateDirectory(Path.Combine(host, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(host, ".gitconfig"), "# fake config");
        await File.WriteAllTextAsync(Path.Combine(host, ".ssh", "sentinel"), "FAKE-NO-KEY");
        var paths = new HubPaths(Path.Combine(root, "next"), Path.Combine(root, "global")); paths.EnsureBaseLayout(); var id = Guid.NewGuid();
        var bridge = new NextBridge(paths); await bridge.ApplyAsync(id, true, host);
        Check(new DirectoryInfo(Path.Combine(paths.Home(id), ".ssh")).LinkTarget is not null, "SSH bridge missing.");
        await bridge.ApplyAsync(id, false, host);
        Check(!Directory.Exists(Path.Combine(paths.Home(id), ".ssh")) && !File.Exists(Path.Combine(paths.Home(id), ".gitconfig")), "Revocation left a bridge.");
        Check(await File.ReadAllTextAsync(Path.Combine(host, ".ssh", "sentinel")) == "FAKE-NO-KEY", "Host target changed.");
        await bridge.ApplyAsync(id, true, host);
        await File.WriteAllTextAsync(Path.Combine(paths.Home(id), ".gitconfig"), "user replacement");
        await TestAssert.ThrowsCodeAsync<HubException>(() => bridge.ApplyAsync(id, false, host), "bridge_ownership_changed");
        Check(await File.ReadAllTextAsync(Path.Combine(paths.Home(id), ".gitconfig")) == "user replacement", "Unmanaged replacement deleted.");
        Check(!Directory.Exists(Path.Combine(paths.Home(id), ".ssh")), "A Git conflict prevented revoking the still-owned SSH bridge.");
        // Interruption after manifest intent but before creation is recoverable.
        var retryId = Guid.NewGuid(); paths.EnsureProfileLayout(retryId);
        await new AtomicJsonStore(paths.DataRoot).WriteAsync(Path.Combine(paths.ProfileRoot(retryId), "bridge.json"),
            new BridgeDocument { ManagedLinks = new() { [".gitconfig"] = Path.Combine(host, ".gitconfig") } }, NextJsonContext.Default.BridgeDocument, CancellationToken.None);
        await bridge.ApplyAsync(retryId, true, host);
        Check(File.Exists(Path.Combine(paths.Home(retryId), ".gitconfig")), "Interrupted bridge did not recover.");
    }
    public static async Task MigrationAsync()
    {
        var root = Root(); var legacy = Path.Combine(root, "legacy"); var paths = new HubPaths(legacy, Path.Combine(root, "legacy-global"));
        var repository = new ConfigRepository(paths); var manager = new ProfileManager(repository, paths);
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var a = await manager.CreateAsync("A", workspace, IsolationBackendKind.ProcessLocal);
        var registry = await File.ReadAllTextAsync(paths.ProfilesFile);
        // A canary credential filename must never appear under Next.
        await File.WriteAllTextAsync(Path.Combine(paths.Home(a.Id), "jetski-standalone-oauth-token"), "FAKE-CANARY");
        var next = new NextHubService(Path.Combine(root, "next"));
        await TestAssert.ThrowsCodeAsync<HubException>(() => NextMigration.RunAsync(next, legacy, false), "migration_preview_required");
        await NextMigration.RunAsync(next, legacy, true);
        Check((await next.Profiles.ListAsync()).Count == 0, "Dry run migrated metadata.");
        await NextMigration.RunAsync(next, legacy, false); await NextMigration.RunAsync(next, legacy, false);
        var migrated = await next.Profiles.ListAsync(); Check(migrated.Count == 1 && migrated[0].Id == a.Id, "Migration did not preserve UUID/idempotence.");
        Check(!File.Exists(Path.Combine(next.Paths.Home(a.Id), "jetski-standalone-oauth-token")), "Migration copied credential.");
        Check(await File.ReadAllTextAsync(paths.ProfilesFile) == registry, "Migration changed source.");
        Check(!File.Exists(Path.Combine(next.Paths.DataRoot, "compatibility.json")), "Migration copied proof.");
    }
    public static async Task EnumerationAsync()
    {
        var hub = new NextHubService(Root(), new FailingCatalog());
        var p = await hub.Profiles.CreateAsync("A", null, IsolationBackendKind.ProcessLocal);
        var install = new InstallationFingerprint(@"C:\vendor\Antigravity.exe", "2.19.1", new());
        await new AtomicJsonStore(hub.Paths.DataRoot).WriteAsync(Path.Combine(hub.Paths.RuntimeStateRoot, "next-" + p.Id.ToString("D") + ".json"),
            new NextRuntime { ProfileId = p.Id, State = RuntimeState.Running, Installation = install }, NextJsonContext.Default.NextRuntime, CancellationToken.None);
        Check((await hub.StatusAsync(p.Id)).State == RuntimeState.Unknown, "Enumeration failure became stopped.");
        await TestAssert.ThrowsCodeAsync<HubException>(() => hub.StopAsync(p.Id, true), "process_enumeration_unavailable");
    }
    public static async Task CallbackAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new PendingCallback { ProfileId = Guid.NewGuid(), CreatedUtc = now.AddSeconds(-1) };
        var b = a with { ProfileId = Guid.NewGuid() };
        Check(NextHubService.SelectWaiter([a], now).ProfileId == a.ProfileId, "Single waiter not selected.");
        await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextHubService.SelectWaiter([a, b], now)), "callback_ambiguous");
        await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextHubService.SelectWaiter([a with { CreatedUtc = now.AddHours(-1) }], now)), "callback_ambiguous");
        await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextHubService.SelectWaiter([a with { CreatedUtc = now.AddHours(1) }], now)), "callback_ambiguous");
    }
    public static async Task CallbackDiagnosticsAsync()
    {
        var hub = new NextHubService(Root());
        const string canary = "PRIVATE-CALLBACK-CANARY";
        await TestAssert.ThrowsCodeAsync<HubException>(() => hub.RouteCallbackAsync("https://invalid.example/?code=" + canary), "deeplink_callback_invalid");
        var files = Directory.GetFiles(hub.Paths.LogsRoot, "*.jsonl");
        Check(files.Length > 0, "Rejected callback has no safe diagnostic evidence.");
        var lines = (await File.ReadAllLinesAsync(files[0])).Select(line => JsonSerializer.Deserialize<HubLogEvent>(line)!).ToArray();
        Check(lines.Any(entry => entry.Operation == "next-callback" && entry.Result == "received"), "Callback receipt missing.");
        Check(lines.Any(entry => entry.Operation == "next-callback" && entry.Result == "failed" &&
            entry.ErrorCode == "deeplink_callback_invalid" && entry.SanitizedCommandTemplate == "validate-uri"), "Failure stage/code missing.");
        Check(!string.Join('\n', await File.ReadAllLinesAsync(files[0])).Contains(canary), "Diagnostic leaked callback URI.");
    }
    public static async Task ExitRaceAsync()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow;
        var catalog = new ExitRaceCatalog(); var controller = new ExitRaceController(catalog);
        var hub = new NextHubService(root, catalog, controller);
        var profile = await hub.Profiles.CreateAsync("A", null, IsolationBackendKind.ProcessLocal);
        var image = Path.Combine(root, "Antigravity.exe"); var userData = hub.Paths.UserData(profile.Id);
        var row = new ProcessSnapshot(100, 1, now, image, '"' + image + "\" \"--user-data-dir=" + userData + '"', 1);
        catalog.Row = row;
        await new AtomicJsonStore(root).WriteAsync(Path.Combine(hub.Paths.RuntimeStateRoot, "next-" + profile.Id.ToString("D") + ".json"),
            new NextRuntime { ProfileId = profile.Id, State = RuntimeState.Running, AttemptUtc = now.AddSeconds(-1),
                Installation = new InstallationFingerprint(image, "fake", new()), Root = new ProcessIdentity(100, now, image, userData),
                Processes = [new ProcessIdentity(100, now, image, userData)], FileTokenStore = true }, NextJsonContext.Default.NextRuntime, CancellationToken.None);
        Check(await hub.StopAsync(profile.Id, true), "A transient unreadable exit snapshot must be retried, never treated as stopped.");
        Check(catalog.UnknownObserved && controller.Terminations == 1, "Exit retry either skipped unknown or re-terminated an unverified identity.");
    }
    private sealed class ExitRaceCatalog : IProcessCatalog
    {
        public ProcessSnapshot Row = null!; public bool Stopped, UnknownObserved;
        public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default)
        {
            if (!Stopped) return Task.FromResult<IReadOnlyList<ProcessSnapshot>>([Row]);
            if (!UnknownObserved) { UnknownObserved = true; return Task.FromResult<IReadOnlyList<ProcessSnapshot>>([Row with { ExecutablePath = "" }]); }
            return Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);
        }
    }
    private sealed class ExitRaceController(ExitRaceCatalog catalog) : IProcessController
    {
        public int Terminations;
        public bool TryFocus(ProcessSnapshot expected) => false;
        public bool TryCloseMainWindow(ProcessSnapshot expected) => false;
        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public bool Terminate(ProcessSnapshot expected) { Terminations++; catalog.Stopped = true; return true; }
        public ProcessLiveness ProbeLiveness(ProcessIdentity expected) => ProcessLiveness.Unknown;
    }
    private sealed class FailingCatalog : IProcessCatalog
    { public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default) => throw new HubException("unavailable", "process_enumeration_unavailable"); }
}
