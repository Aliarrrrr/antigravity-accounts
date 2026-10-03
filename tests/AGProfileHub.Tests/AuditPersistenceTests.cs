using System.Diagnostics;
using System.Text;
using AGProfileHub.Core;

internal static class AuditPersistenceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Audit persistence retries a released reader and preserves the original backup", ReleasedAsync),
        ("Audit persistence permanent reader returns a bounded configuration fault", PermanentAsync),
        ("Audit persistence cancellation preserves JSON and cleans temporary files", CancelAsync),
        ("Audit persistence independent process reader is released and reaped", ProcessAsync),
        ("Audit persistence startup write failure never invokes proof cleanup", StartupAsync),
        ("Audit persistence failure-state write preserves the startup cause", FailureCauseAsync),
        ("Audit persistence failed stop recovery retains enumeration cause", StopCauseAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Write(string root, string path, CancellationToken token = default) =>
        new AtomicJsonStore(root).WriteAsync(path, new NextRuntime { ProfileId = Guid.Empty, State = RuntimeState.Starting }, NextJsonContext.Default.NextRuntime, token);
    private const string Original = "{\"schemaVersion\":1,\"sentinel\":\"original\"}";
    private static void Clean(string root) => Check(!Directory.EnumerateFiles(root, ".*.tmp*").Any(), "Owned temporary leaked.");
    private static async Task ReleasedAsync()
    {
        using var owned = new OwnedTestDirectory(); var path = Path.Combine(owned.Root, "state.json");
        await File.WriteAllTextAsync(path, Original);
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(150); reader.Dispose(); });
        try { await Write(owned.Root, path); } finally { await release; reader.Dispose(); }
        Check(await File.ReadAllTextAsync(path + ".bak") == Original, "Backup lost original bytes.");
        Check((await File.ReadAllTextAsync(path)).Contains("profileId"), "Replacement did not commit."); Clean(owned.Root);
    }
    private static async Task PermanentAsync()
    {
        using var owned = new OwnedTestDirectory(); var path = Path.Combine(owned.Root, "state.json"); await File.WriteAllTextAsync(path, Original);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var watch = Stopwatch.StartNew();
            try { await Write(owned.Root, path); throw new Exception("Locked commit succeeded."); }
            catch (ConfigurationException ex) { Check(ex.Code == "configuration_write_failed" && ex.InnerException is IOException or UnauthorizedAccessException, "Wrong fault or lost cause."); }
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), "Retries are not bounded.");
        }
        Check(await File.ReadAllTextAsync(path) == Original, "Original changed."); Clean(owned.Root);
    }
    private static async Task CancelAsync()
    {
        using var owned = new OwnedTestDirectory(); var path = Path.Combine(owned.Root, "state.json"); await File.WriteAllTextAsync(path, Original);
        using var cancel = new CancellationTokenSource(100);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => Write(owned.Root, path, cancel.Token));
        Check(await File.ReadAllTextAsync(path) == Original, "Cancellation replaced original."); Clean(owned.Root);
    }
    private static async Task ProcessAsync()
    {
        using var owned = new OwnedTestDirectory(); var path = Path.Combine(owned.Root, "state.json"); await File.WriteAllTextAsync(path, Original);
        var identity = Guid.NewGuid().ToString("N");
        var script = "$s=[IO.File]::Open('" + path.Replace("'", "''") + "','Open','Read','Read'); try { [Console]::WriteLine('" + identity + "'); [Console]::Out.Flush(); [Console]::ReadLine() | Out-Null } finally {$s.Dispose()}";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        try
        {
            Check(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == identity, "Wrong child readiness identity.");
            var write = Write(owned.Root, path); await Task.Delay(100);
            await process.StandardInput.WriteLineAsync("release"); await process.StandardInput.FlushAsync();
            await write; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.ExitCode == 0 && await File.ReadAllTextAsync(path + ".bak") == Original, "Child or backup failed."); Clean(owned.Root);
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
    private static async Task StartupAsync()
    {
        using var owned = new OwnedTestDirectory();
        var catalog = new Catalog(); var controller = new Controller();
        var service = new NextHubService(owned.Root, catalog, controller);
        var profile = await service.Profiles.CreateAsync("synthetic", null, IsolationBackendKind.ProcessLocal);
        var install = new InstallationFingerprint(Path.Combine(owned.Root, "synthetic.exe"), "test", new());
        var path = Path.Combine(service.Paths.RuntimeStateRoot, "next-" + profile.Id.ToString("D") + ".json");
        FileStream? reader = null;
        catalog.Read = () =>
        {
            if (catalog.Calls == 1) reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            else reader?.Dispose(); // If proof cleanup is incorrectly entered, allow its destructive path to run.
            return controller.Terminations == 0 ? [new ProcessSnapshot(987654, 0, DateTimeOffset.UtcNow, install.Executable,
                "synthetic --user-data-dir=\"" + service.Paths.UserData(profile.Id) + "\"", 0)] : [];
        };
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => service.LaunchCoreAsync(profile.Id, install,
                CancellationToken.None, (_, _, _) => { }), "configuration_write_failed");
            Check(catalog.Calls == 1 && controller.Terminations == 0, "Persistence failure entered startup proof cleanup.");
        }
        finally { reader?.Dispose(); }
        var status = await service.StatusAsync(profile.Id);
        Check(status.State == RuntimeState.Unknown, "Unpersisted proof must remain unknown.");
    }
    private static async Task FailureCauseAsync()
    {
        using var owned = new OwnedTestDirectory(); var catalog = new Catalog();
        var service = new NextHubService(owned.Root, catalog, new Controller());
        var profile = await service.Profiles.CreateAsync("synthetic", null, IsolationBackendKind.ProcessLocal);
        var install = new InstallationFingerprint(Path.Combine(owned.Root, "synthetic.exe"), "test", new());
        var path = Path.Combine(service.Paths.RuntimeStateRoot, "next-" + profile.Id.ToString("D") + ".json");
        FileStream? reader = null; var cause = new HubException("Controlled startup failure", "synthetic_start_failed");
        try
        {
            try
            {
                await service.LaunchCoreAsync(profile.Id, install, CancellationToken.None, (_, _, _) =>
                { reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); throw cause; });
                throw new Exception("Startup unexpectedly succeeded.");
            }
            catch (HubException ex) { Check(ReferenceEquals(ex, cause), "Failure-state save masked the original startup cause."); Check(ex.Data["RecoveryPersistenceFailure"] is ConfigurationException { Code: "configuration_write_failed" }, "Recovery save cause was lost."); }
        }
        finally { reader?.Dispose(); }
        Check((await service.StatusAsync(profile.Id)).State == RuntimeState.Unknown, "Failed persistence claimed stopped.");
    }
    private sealed class Catalog : IProcessCatalog
    {
        public int Calls; public Func<IReadOnlyList<ProcessSnapshot>> Read = () => [];
        public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Read()); }
    }
    private sealed class Controller : IProcessController
    {
        public int Terminations;
        public bool Terminate(ProcessSnapshot expected) { Terminations++; return true; }
        public bool TryFocus(ProcessSnapshot expected) => throw new Exception("Unexpected focus.");
        public bool TryCloseMainWindow(ProcessSnapshot expected) => throw new Exception("Unexpected close.");
        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new Exception("Unexpected wait.");
        public ProcessLiveness ProbeLiveness(ProcessIdentity expected) => throw new Exception("Unexpected probe.");
    }
    private static async Task StopCauseAsync()
    {
        using var owned = new OwnedTestDirectory(); var catalog = new Catalog(); var controller = new Controller();
        var service = new NextHubService(owned.Root, catalog, controller);
        var profile = await service.Profiles.CreateAsync("synthetic", null, IsolationBackendKind.ProcessLocal);
        var path = Path.Combine(service.Paths.RuntimeStateRoot, "next-" + profile.Id.ToString("D") + ".json");
        await new AtomicJsonStore(owned.Root).WriteAsync(path, new NextRuntime { ProfileId = profile.Id,
            State = RuntimeState.Starting, Installation = new InstallationFingerprint(Path.Combine(owned.Root, "synthetic.exe"), "test", new()) }, NextJsonContext.Default.NextRuntime, default);
        FileStream? reader = null;
        catalog.Read = () => { reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); throw new IOException("Controlled catalog failure"); };
        try
        {
            try { await service.StopAsync(profile.Id); throw new Exception("Stop unexpectedly succeeded."); }
            catch (HubException ex)
            {
                Check(ex.Code == "process_enumeration_unavailable", "Recovery save masked stop cause.");
                Check(ex.Data["RecoveryPersistenceFailure"] is ConfigurationException { Code: "configuration_write_failed" }, "Secondary save failure was lost.");
            }
            Check(controller.Terminations == 0, "Unknown process state was terminated.");
        }
        finally { reader?.Dispose(); }
    }
}
