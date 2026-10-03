using System.Diagnostics;
using System.Text;
using AGProfileHub.Core;

internal static class BoundaryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases => RunnerCases.Concat(SchemaCases).Concat(PathCases).Concat(NextCases);
    private static IEnumerable<(string Name, Func<Task> Run)> RunnerCases =>
    [
        ("Runner excludes desktop integration unless explicitly enabled", RunnerDefaultAsync),
        ("Runner opts into desktop integration", RunnerSystemAsync),
        ("Runner filters case names without running unmatched cases", RunnerFilterAsync),
        ("Runner rejects malformed arguments before executing cases", RunnerArgumentsAsync),
        ("Runner retains failing exit status while continuing selected cases", RunnerFailureAsync),
        ("Runner cleans its owned default-root probe after an operation fails", DesktopProbeCleanupAsync)
    ];

    private static IEnumerable<(string Name, Func<Task> Run)> SchemaCases
    {
        get
        {
            foreach (var fixture in new (string Name, string Json, string Code)[] {
                ("a missing schema", "{}", "schema_missing"),
                ("a non-object root", "[]", "schema_missing"),
                ("a null root", "null", "schema_missing"),
                ("a string schema", "{\"schemaVersion\":\"1\"}", "schema_missing"),
                ("a fractional schema", "{\"schemaVersion\":1.5}", "schema_missing"),
                ("an overflowing schema", "{\"schemaVersion\":2147483648}", "schema_missing"),
                ("zero schema", "{\"schemaVersion\":0}", "schema_invalid"),
                ("negative schema", "{\"schemaVersion\":-1}", "schema_invalid"),
                ("future schema", "{\"schemaVersion\":2}", "future_schema"),
                ("broken JSON", "{broken 雪", "configuration_corrupt"),
                ("a mismatched document shape", "{\"schemaVersion\":1,\"profiles\":7}", "configuration_corrupt")
            })
                yield return ($"Storage rejects {fixture.Name} without replacing data or backup", () => SchemaRejectsAsync(fixture.Json, fixture.Code));
            yield return ("Storage accepts current schema without calling its missing-file factory", SchemaValidAsync);
            yield return ("Storage returns a missing-file default without creating a file or parent", SchemaMissingFileAsync);
        }
    }

    private static IEnumerable<(string Name, Func<Task> Run)> PathCases =>
    [
        ("Path containment refuses traversal and sibling-prefix collisions", ContainmentAsync),
        ("Path rejects a junction used as the managed root", RootJunctionAsync),
        ("Path refuses writes and executables beneath a junction ancestor", AncestorJunctionAsync),
        ("Path refuses a dangling junction without recreating its target", DanglingJunctionAsync),
        ("Storage rejects a junction at the backup path without replacing data", BackupJunctionAsync),
        ("Path cleanup refuses a replaced owned root and preserves its junction target", CleanupRejectsJunctionAsync)
    ];

    private static IEnumerable<(string Name, Func<Task> Run)> NextCases =>
    [
        ("Next waiters reject empty and invalid associations at expiry boundaries", WaiterBoundariesAsync),
        ("Next waiters select the only valid profile among invalid records", WaiterMixedAsync),
        ("Next callback URI validation rejects injection and preserves the size boundary", CallbackUriAsync),
        ("Next router canonicalization refuses foreign executables and tampered arguments", RouterCommandAsync),
        ("Next bridge preserves an unmanaged file with a precise conflict code", BridgeConflictAsync),
        ("Next bridge rejects quoted or control-character host paths before mutation", BridgeTargetAsync)
    ];

    private static async Task SchemaRejectsAsync(string json, string code)
    {
        using var owned = new OwnedTestDirectory();
        var path = Path.Combine(owned.Root, "profiles.json"); var original = Encoding.UTF8.GetBytes(json);
        byte[] backup = [0, 255, 13, 10, 42];
        await File.WriteAllBytesAsync(path, original); await File.WriteAllBytesAsync(path + ".bak", backup);
        var defaultUsed = false;
        await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => new AtomicJsonStore(owned.Root).ReadAsync(path,
            HubJsonContext.Default.ProfilesDocument, () => { defaultUsed = true; return new ProfilesDocument(); }, CancellationToken.None), code);
        TestAssert.Check(!defaultUsed, "A corrupt existing file was treated as absent.");
        var remaining = await File.ReadAllBytesAsync(path);
        var remainingBackup = await File.ReadAllBytesAsync(path + ".bak");
        TestAssert.Check(original.SequenceEqual(remaining), "Rejected read replaced the original document.");
        TestAssert.Check(backup.SequenceEqual(remainingBackup), "Rejected read changed a prior backup.");
        TestAssert.Check(!Directory.EnumerateFiles(owned.Root, "*.tmp").Any(), "Rejected read created temporary output.");
    }

    private static async Task SchemaValidAsync()
    {
        using var owned = new OwnedTestDirectory(); var path = Path.Combine(owned.Root, "profiles.json");
        var original = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"profiles\":[]}");
        await File.WriteAllBytesAsync(path, original);
        var result = await new AtomicJsonStore(owned.Root).ReadAsync(path, HubJsonContext.Default.ProfilesDocument,
            () => throw new Exception("Existing valid configuration fell back to an empty default."), CancellationToken.None);
        TestAssert.Check(result.SchemaVersion == 1 && result.Profiles.Count == 0, "Current schema was not deserialized.");
        var remaining = await File.ReadAllBytesAsync(path);
        TestAssert.Check(original.SequenceEqual(remaining) && !File.Exists(path + ".bak"), "Reading valid data mutated its bytes or created a backup.");
    }

    private static async Task SchemaMissingFileAsync()
    {
        using var owned = new OwnedTestDirectory(); var directory = Path.Combine(owned.Root, "absent");
        var path = Path.Combine(directory, "profiles.json"); var expected = new ProfilesDocument(); var calls = 0;
        var result = await new AtomicJsonStore(owned.Root).ReadAsync(path, HubJsonContext.Default.ProfilesDocument,
            () => { calls++; return expected; }, CancellationToken.None);
        TestAssert.Check(ReferenceEquals(result, expected) && calls == 1 && !Directory.Exists(directory), "Missing-file read failed to return its default without creating storage.");
    }

    private static async Task ContainmentAsync()
    {
        using var owned = new OwnedTestDirectory(); var root = Path.Combine(owned.Root, "managed");
        await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.EnsureContained(root, Path.Combine(root, "..", "outside"))), "path_outside_root");
        await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.EnsureContained(root, root + "-other\\profiles.json")), "path_outside_root");
        var inside = Path.Combine(root, "profile", "..", "profiles.json");
        TestAssert.Check(PathSafety.EnsureContained(root.ToUpperInvariant(), inside) == Path.Combine(root, "profiles.json"), "Containment rejected an ordinary normalized child or case-insensitive Windows root.");
    }

    private static async Task RootJunctionAsync()
    {
        using var owned = new OwnedTestDirectory(); var host = CreateHost(owned.Root); var link = Path.Combine(owned.Root, "linked-root");
        await CreateJunctionAsync(link, host);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.EnsureSafeDirectory(link, Path.Combine(link, "profile"))), "reparse_point_rejected");
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => new AtomicJsonStore(link).WriteAsync(Path.Combine(link, "profiles.json"),
                new ProfilesDocument(), HubJsonContext.Default.ProfilesDocument, CancellationToken.None), "reparse_point_rejected");
            AssertHost(host);
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static async Task AncestorJunctionAsync()
    {
        using var owned = new OwnedTestDirectory(); var host = CreateHost(owned.Root); var root = Path.Combine(owned.Root, "managed"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(host, "vendor.exe"), "test-only executable marker");
        var link = Path.Combine(root, "ancestor"); await CreateJunctionAsync(link, host);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.EnsureSafeDirectory(root, Path.Combine(link, "nested"))), "reparse_point_rejected");
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.CanonicalizeExistingFile(Path.Combine(link, "vendor.exe"))), "reparse_point_rejected");
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => new AtomicJsonStore(root).WriteAsync(Path.Combine(link, "profiles.json"),
                new ProfilesDocument(), HubJsonContext.Default.ProfilesDocument, CancellationToken.None), "reparse_point_rejected");
            AssertHost(host); TestAssert.Check(!Directory.Exists(Path.Combine(host, "nested")), "Unsafe directory creation followed a junction.");
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static async Task DanglingJunctionAsync()
    {
        using var owned = new OwnedTestDirectory(); var target = Path.Combine(owned.Root, "deleted-target"); Directory.CreateDirectory(target);
        var root = Path.Combine(owned.Root, "managed"); Directory.CreateDirectory(root); var link = Path.Combine(root, "dangling");
        await CreateJunctionAsync(link, target); Directory.Delete(target);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => PathSafety.EnsureSafeDirectory(root, Path.Combine(link, "nested"))), "reparse_point_rejected");
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => new AtomicJsonStore(root).WriteAsync(Path.Combine(link, "profiles.json"),
                new ProfilesDocument(), HubJsonContext.Default.ProfilesDocument, CancellationToken.None), "reparse_point_rejected");
            TestAssert.Check(!Directory.Exists(target), "Refused dangling path recreated the missing host target.");
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static async Task BackupJunctionAsync()
    {
        using var owned = new OwnedTestDirectory(); var host = CreateHost(owned.Root); var root = Path.Combine(owned.Root, "managed"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profiles.json"); var original = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"profiles\":[]}\n");
        await File.WriteAllBytesAsync(path, original); var link = path + ".bak"; await CreateJunctionAsync(link, host);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => new AtomicJsonStore(root).WriteAsync(path,
                new ProfilesDocument(), HubJsonContext.Default.ProfilesDocument, CancellationToken.None), "reparse_point_rejected");
            var remaining = await File.ReadAllBytesAsync(path);
            TestAssert.Check(original.SequenceEqual(remaining), "Rejected backup path changed current data.");
            AssertHost(host); TestAssert.Check(new DirectoryInfo(link).LinkTarget is not null, "Refusal replaced the backup junction.");
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static async Task CleanupRejectsJunctionAsync()
    {
        using var fixture = new OwnedTestDirectory(); var host = CreateHost(fixture.Root);
        var owned = new OwnedTestDirectory(fixture.Root); Directory.Delete(owned.Root); await CreateJunctionAsync(owned.Root, host);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(owned.Dispose), "reparse_point_rejected");
            AssertHost(host); TestAssert.Check(new DirectoryInfo(owned.Root).LinkTarget is not null, "Cleanup followed or removed a replaced owned root.");
        }
        finally { Directory.Delete(owned.Root, recursive: false); }
    }

    private static async Task WaiterBoundariesAsync()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var valid = new PendingCallback { ProfileId = Guid.NewGuid(), CreatedUtc = now.AddMinutes(-1) };
        foreach (var waiting in new IReadOnlyList<PendingCallback>[] { [], [valid with { ProfileId = Guid.Empty }],
            [valid with { CreatedUtc = default }], [valid with { CreatedUtc = now.AddMinutes(-30) }], [valid with { CreatedUtc = now.AddTicks(1) }] })
            await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextHubService.SelectWaiter(waiting, now)), "callback_ambiguous");
        TestAssert.Check(NextHubService.SelectWaiter([valid with { CreatedUtc = now.AddMinutes(-30).AddTicks(1) }], now).ProfileId == valid.ProfileId,
            "A waiter inside the expiry boundary was refused.");
    }

    private static Task WaiterMixedAsync()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var valid = new PendingCallback { ProfileId = Guid.NewGuid(), CreatedUtc = now.AddMinutes(-1) };
        var selected = NextHubService.SelectWaiter([valid with { ProfileId = Guid.Empty }, valid with { CreatedUtc = default },
            valid with { CreatedUtc = now.AddMinutes(-30) }, valid with { CreatedUtc = now.AddTicks(1) }, valid], now);
        TestAssert.Check(selected.ProfileId == valid.ProfileId, "Invalid records obscured the one valid association.");
        return Task.CompletedTask;
    }

    private static async Task CallbackUriAsync()
    {
        foreach (var uri in new[] { "", " ", "https://example.invalid/oauth", "--user-data-dir=C:\\evil", "antigravity://oauth?code=x\n--flag",
            "antigravity://oauth?code=x\0", "antigravity://" + new string('x', 8193 - "antigravity://".Length) })
            await TestAssert.ThrowsCodeAsync<HubException>(() => Task.Run(() => NextProtocol.ValidateCallbackUri(uri)), "deeplink_callback_invalid");
        NextProtocol.ValidateCallbackUri("ANTIGRAVITY://" + new string('x', 8192 - "ANTIGRAVITY://".Length));
        NextProtocol.ValidateCallbackUri("antigravity://oauth?code=fake&state=%22--flag");
    }

    private static async Task RouterCommandAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = new HubPaths(owned.Root, Path.Combine(owned.Root, "global"));
        var cli = Path.Combine(owned.Root, "agprofile-next.exe"); await File.WriteAllTextAsync(cli, "test-only cli marker");
        var command = $"\"{cli}\" --data-root \"{owned.Root}\" callback \"%1\"";
        TestAssert.Check(NextProtocol.CanonicalizeRouterCommand(paths, command) == command, "Canonical production router command was not preserved.");
        var foreign = Path.Combine(owned.Root, "AGProfileHub.Cli.exe"); await File.WriteAllTextAsync(foreign, "foreign marker");
        foreach (var invalid in new[] { command + " extra", command.Replace(" callback ", " launch ", StringComparison.Ordinal),
            command.Replace("\"%1\"", "\"%2\"", StringComparison.Ordinal), command.Replace("--data-root", "--user-data-dir", StringComparison.Ordinal),
            $"\"{cli}\" --data-root \"{owned.Root}-other\" callback \"%1\"", $"\"{foreign}\" --data-root \"{owned.Root}\" callback \"%1\"" })
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => Task.Run(() => NextProtocol.CanonicalizeRouterCommand(paths, invalid)), "configuration_invalid");
    }

    private static async Task BridgeConflictAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = new HubPaths(Path.Combine(owned.Root, "managed"), Path.Combine(owned.Root, "global"));
        var id = Guid.NewGuid(); paths.EnsureProfileLayout(id); var host = Path.Combine(owned.Root, "host"); Directory.CreateDirectory(host);
        await File.WriteAllTextAsync(Path.Combine(host, ".gitconfig"), "test host"); var path = Path.Combine(paths.Home(id), ".gitconfig");
        await File.WriteAllTextAsync(path, "user-owned config");
        await TestAssert.ThrowsCodeAsync<HubException>(() => new NextBridge(paths).ApplyAsync(id, true, host), "bridge_conflict");
        TestAssert.Check(await File.ReadAllTextAsync(path) == "user-owned config", "Unmanaged config was overwritten.");
    }

    private static async Task BridgeTargetAsync()
    {
        using var owned = new OwnedTestDirectory(); var paths = new HubPaths(Path.Combine(owned.Root, "managed"), Path.Combine(owned.Root, "global"));
        var id = Guid.NewGuid(); var bridge = new NextBridge(paths);
        foreach (var host in new[] { Path.Combine(owned.Root, "quoted\"host"), Path.Combine(owned.Root, "control\nhost") })
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => bridge.ApplyAsync(id, true, host), "configuration_invalid");
        TestAssert.Check(!Directory.Exists(paths.ProfileRoot(id)), "Rejected bridge target initialized a profile before validating its path.");
    }

    private static string CreateHost(string root)
    {
        var host = Path.Combine(root, "host"); Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, "sentinel"), "FAKE-HOST-DATA"); return host;
    }
    private static void AssertHost(string host) => TestAssert.Check(File.ReadAllText(Path.Combine(host, "sentinel")) == "FAKE-HOST-DATA" &&
        !File.Exists(Path.Combine(host, "profiles.json")), "Rejected path wrote through to its host target.");

    private static async Task CreateJunctionAsync(string link, string target)
    {
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Exception("Could not start the temporary junction helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(); await process.WaitForExitAsync();
            await stdout; await stderr; throw new Exception("Temporary junction helper timed out.");
        }
        await stdout; var error = await stderr;
        TestAssert.Check(process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null, "Could not create the temporary junction: " + error);
    }

    private static async Task RunnerDefaultAsync()
    {
        var ordinaryRuns = 0; var desktopRuns = 0;
        var cases = new[] {
            new TestCase("ordinary", "Shared", () => { ordinaryRuns++; return Task.CompletedTask; }),
            new TestCase("desktop", "Next", () => { desktopRuns++; return Task.CompletedTask; }, System: true)
        };
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TestRunner.RunAsync(cases, [], output, error);
        TestAssert.Check(exit == 0 && ordinaryRuns == 1 && desktopRuns == 0, "Default runner executed a desktop integration case.");
        TestAssert.Check(output.ToString().Contains("SKIP  [Next] desktop", StringComparison.Ordinal) &&
            output.ToString().Contains("GROUP Next: 0 passed, 0 failed, 1 skipped", StringComparison.Ordinal), "Skipped system case was absent from the group results.");
    }

    private static async Task RunnerSystemAsync()
    {
        var runs = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TestRunner.RunAsync([new TestCase("desktop", "Next", () => { runs++; return Task.CompletedTask; }, true)],
            ["--include-system"], output, error);
        TestAssert.Check(exit == 0 && runs == 1, "Explicit system opt-in did not execute the integration case.");
    }

    private static async Task RunnerFilterAsync()
    {
        var alpha = 0; var beta = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TestRunner.RunAsync([
            new TestCase("alpha case", "Shared", () => { alpha++; return Task.CompletedTask; }),
            new TestCase("BETA case", "Shared", () => { beta++; return Task.CompletedTask; })
        ], ["--filter", "beta"], output, error);
        TestAssert.Check(exit == 0 && alpha == 0 && beta == 1, "Filter executed unmatched cases or failed case-insensitive matching.");
        TestAssert.Check(output.ToString().Contains("1/1 tests passed; 1 skipped.", StringComparison.Ordinal), "Filtered summary used the wrong execution count.");
        var missingExit = await TestRunner.RunAsync([new TestCase("alpha", "Shared", () => { alpha++; return Task.CompletedTask; })],
            ["--filter", "no matching case"], output, error);
        TestAssert.Check(missingExit == 2 && alpha == 0, "An unmatched filter silently reported a successful check.");
    }

    private static async Task RunnerArgumentsAsync()
    {
        foreach (var args in new[] { new[] { "--unknown" }, new[] { "--filter" }, new[] { "--filter", "" },
            new[] { "--include-system", "--include-system" }, new[] { "--filter", "a", "--filter", "b" } })
        {
            var runs = 0;
            using var output = new StringWriter(); using var error = new StringWriter();
            var exit = await TestRunner.RunAsync([new TestCase("ordinary", "Shared", () => { runs++; return Task.CompletedTask; })], args, output, error);
            TestAssert.Check(exit == 2 && runs == 0 && error.ToString().Contains("usage", StringComparison.OrdinalIgnoreCase), "Malformed runner arguments executed cases or did not fail with usage status.");
        }
    }

    private static async Task RunnerFailureAsync()
    {
        var laterRuns = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TestRunner.RunAsync([
            new TestCase("broken", "Shared", () => throw new InvalidOperationException("expected runner fixture failure")),
            new TestCase("later", "Shared", () => { laterRuns++; return Task.CompletedTask; })
        ], [], output, error);
        TestAssert.Check(exit == 1 && laterRuns == 1 && error.ToString().Contains("broken", StringComparison.Ordinal), "Runner hid a failed case or stopped before remaining checks.");
    }

    private static async Task DesktopProbeCleanupAsync()
    {
        string? root = null;
        try
        {
            try
            {
                await NextTests.RunDesktopProbeAsync((paths, _) =>
                {
                    root = paths.DataRoot;
                    File.WriteAllText(Path.Combine(root, "owned-marker"), "test-only");
                    throw new InvalidOperationException("expected probe failure");
                });
                throw new Exception("Probe operation failure was swallowed.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "expected probe failure") { }
            TestAssert.Check(root is not null && !Directory.Exists(root), "Failed desktop regression left its owned default-root probe behind.");
        }
        finally
        {
            if (root is not null && Directory.Exists(root))
            {
                var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityProfileHubNext", "probes");
                PathSafety.EnsureContained(parent, root); PathSafety.RejectExistingPathAncestors(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

internal static class TestAssert
{
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task<T> ThrowsCodeAsync<T>(Func<Task> operation, string code) where T : HubException
    {
        try { await operation(); }
        catch (T ex)
        {
            Check(ex.Code == code, $"Expected {code}, got {ex.Code}.");
            return ex;
        }
        throw new Exception($"Expected {typeof(T).Name} with code {code}.");
    }
    internal static async Task<T> ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T ex) { return ex; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

internal sealed class OwnedTestDirectory : IDisposable
{
    private readonly string _parent;
    internal string Root { get; }
    internal OwnedTestDirectory(string? parent = null, string prefix = "agprofile-boundary-")
    {
        _parent = Path.GetFullPath(parent ?? Path.GetTempPath());
        PathSafety.RejectExistingPathAncestors(_parent);
        Root = Path.Combine(_parent, prefix + Guid.NewGuid().ToString("N"));
        PathSafety.EnsureContained(_parent, Root); Directory.CreateDirectory(Root);
        PathSafety.RejectExistingPathAncestors(Root);
    }
    public void Dispose()
    {
        PathSafety.EnsureContained(_parent, Root); PathSafety.RejectExistingPathAncestors(Root);
        if (!Directory.Exists(Root)) return;
        var pending = new Stack<string>(); pending.Push(Root);
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ConfigurationException("Owned test cleanup found a link; preserved for inspection.", "reparse_point_rejected");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        Directory.Delete(Root, recursive: true);
    }
}
