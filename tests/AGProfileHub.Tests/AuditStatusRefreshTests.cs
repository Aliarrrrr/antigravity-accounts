using AGProfileHub.Core;

internal static class AuditStatusRefreshTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Next status refresh shares a canonical executable snapshot", SharedSnapshotAsync),
        ("Next status refresh takes a fresh snapshot per call", NextRefreshAsync),
        ("Next status refresh leaves single status and stop fresh", StandaloneAsync),
        ("Next status refresh keeps same-name executable paths separate", DistinctPathsAsync),
        ("Next status refresh retains synchronous enumeration failures per profile", EnumerationFailureAsync),
        ("Next status refresh retains configuration failure codes", ConfigurationFailureAsync),
        ("Next status refresh preserves per-profile configuration reads", ConfigurationReadAsync),
        ("Next status refresh preserves cancellation", CancellationAsync),
        ("Next status refresh verifies each root creation and image", IdentityAsync)
    ];

    private static void Check(bool condition, string message) => TestAssert.Check(condition, message);

    private static async Task SharedSnapshotAsync()
    {
        using var f = await Fixture.CreateAsync();
        var alias = Path.Combine(Path.GetDirectoryName(f.Image)!, ".", Path.GetFileName(f.Image));
        if (OperatingSystem.IsWindows()) alias = alias.ToUpperInvariant();
        await f.WriteAsync(1, f.Runtime[1] with { Installation = f.Runtime[1].Installation! with { Executable = alias } });
        var statuses = await f.Hub.StatusAllAsync();
        Check(statuses.Count == 2 && statuses.All(x => x.State == RuntimeState.Running), "Canonical aliases changed valid root statuses.");
        Check(statuses[0].Root!.ProcessId == 12345 && statuses[1].Root!.ProcessId == 12346, "Shared snapshot crossed profile ownership.");
        Check(f.Catalog.Calls == 1, $"One refresh performed {f.Catalog.Calls} duplicate executable scans.");
    }

    private static async Task NextRefreshAsync()
    {
        using var f = await Fixture.CreateAsync();
        Check((await f.Hub.StatusAllAsync()).All(x => x.State == RuntimeState.Running), "Initial roots were not seen.");
        f.Catalog.Rows.Clear();
        Check((await f.Hub.StatusAllAsync()).All(x => x.State == RuntimeState.Stopped), "Next refresh reused vanished roots.");
        Check(f.Catalog.Calls == 2, $"Two refreshes performed {f.Catalog.Calls} scans rather than one fresh scan each.");
    }

    private static async Task StandaloneAsync()
    {
        using var f = await Fixture.CreateAsync();
        await f.Hub.StatusAllAsync(); f.Catalog.Rows.Clear();
        Check((await f.Hub.StatusAsync(f.Ids[0])).State == RuntimeState.Stopped, "Single status reused list roots.");
        Check((await f.Hub.StatusAsync(f.Ids[1])).State == RuntimeState.Stopped, "Second single status reused list roots.");
        Check(!await f.Hub.StopAsync(f.Ids[0], true), "Stop reused vanished roots instead of reporting no owned process.");
        Check(f.Catalog.Calls == 4 && f.Controller.Controls == 0, "Standalone status/stop did not acquire fresh snapshots or controlled a stale root.");
    }

    private static async Task DistinctPathsAsync()
    {
        using var f = await Fixture.CreateAsync();
        var other = Path.Combine(Path.GetDirectoryName(f.Image)!, "other", Path.GetFileName(f.Image));
        var second = f.Runtime[1];
        var identity = second.Root! with { ExecutablePath = other };
        await f.WriteAsync(1, second with { Installation = second.Installation! with { Executable = other }, Root = identity, Processes = [identity] });
        f.Catalog.Rows[1] = f.Catalog.Rows[1] with { ExecutablePath = other };
        var statuses = await f.Hub.StatusAllAsync();
        Check(statuses.All(x => x.State == RuntimeState.Running) && statuses[1].Root!.ExecutablePath == other, "Same-name paths shared a wrong image or ownership.");
        Check(f.Catalog.Calls == 2 && f.Catalog.Requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Distinct executable paths were merged.");
    }

    private static async Task EnumerationFailureAsync()
    {
        using var f = await Fixture.CreateAsync(); f.Catalog.Failure = new IOException("synthetic catalog failure");
        var statuses = await f.Hub.StatusAllAsync();
        Check(statuses.All(x => x.State == RuntimeState.Unknown && x.Detail == "process_enumeration_unavailable"), "Enumeration failure became stopped or changed classification.");
        Check(f.Catalog.Calls == 1, "A faulted snapshot was reissued per profile within the refresh.");
        f.Catalog.Failure = null;
        Check((await f.Hub.StatusAllAsync()).All(x => x.State == RuntimeState.Running), "Faulted snapshot persisted beyond the refresh.");
        Check(f.Catalog.Calls == 2, "Next refresh did not retry the catalog.");
    }

    private static async Task ConfigurationFailureAsync()
    {
        using var f = await Fixture.CreateAsync(); f.Catalog.Failure = new ConfigurationException("synthetic", "configuration_write_failed");
        var statuses = await f.Hub.StatusAllAsync();
        Check(statuses.All(x => x.State == RuntimeState.Unknown && x.Detail == "configuration_write_failed"), "A configuration failure was replaced by a catalog code.");
        Check(f.Catalog.Calls == 1, "A synchronous configuration exception was not reused within this refresh.");
    }

    private static async Task ConfigurationReadAsync()
    {
        using var f = await Fixture.CreateAsync();
        f.Catalog.Before = async _ => await f.WriteAsync(0, f.Runtime[0] with { ProfileId = Guid.NewGuid() });
        var statuses = await f.Hub.StatusAllAsync();
        Check(statuses[0].State == RuntimeState.Unknown && statuses[0].Detail == "configuration_invalid", "Second runtime read lost the original configuration error.");
        Check(statuses[1].State == RuntimeState.Running, "One profile's runtime fault contaminated another profile.");
        Check(f.Catalog.Calls == 1, "Configuration reconciliation caused another full catalog scan.");
    }

    private static async Task CancellationAsync()
    {
        using var f = await Fixture.CreateAsync(); using var cancel = new CancellationTokenSource();
        f.Catalog.Before = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var failure = await TestAssert.ThrowsAsync<OperationCanceledException>(() => f.Hub.StatusAllAsync(cancel.Token));
        Check(failure.CancellationToken == cancel.Token && f.Catalog.Calls == 1, "Status refresh replaced cancellation or changed its token.");
    }

    private static async Task IdentityAsync()
    {
        foreach (var unreadable in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync();
            f.Catalog.Rows[1] = unreadable ? f.Catalog.Rows[1] with { ExecutablePath = "", CommandLine = "" } : f.Catalog.Rows[1] with { StartTimeUtc = f.Catalog.Rows[1].StartTimeUtc.AddSeconds(2) };
            var statuses = await f.Hub.StatusAllAsync();
            Check(statuses[0].State == RuntimeState.Running && statuses[1].State == RuntimeState.Unknown, "Sharing skipped a per-profile image/creation identity check.");
            if (unreadable) Check(statuses[1].Detail == "process_enumeration_unavailable", "Unverifiable recorded image lost fail-closed classification.");
            Check(f.Catalog.Calls == 1 && f.Controller.Controls == 0, "Identity reconciliation repeated scans or invoked process control.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OwnedTestDirectory owned = new();
        public Catalog Catalog { get; } = new();
        public Controller Controller { get; } = new();
        public NextHubService Hub { get; }
        public List<Guid> Ids { get; } = [];
        public List<NextRuntime> Runtime { get; } = [];
        public string Image => Path.Combine(owned.Root, "synthetic.exe");
        private Fixture() => Hub = new NextHubService(owned.Root, Catalog, Controller);
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            try
            {
                var time = DateTimeOffset.UtcNow;
                for (int i = 0; i < 2; i++)
                {
                    var id = (await f.Hub.Profiles.CreateAsync("Synthetic-" + i, null, IsolationBackendKind.ProcessLocal)).Id;
                    f.Ids.Add(id);
                    var identity = new ProcessIdentity(12345 + i, time, f.Image, f.Hub.Paths.UserData(id));
                    f.Catalog.Rows.Add(new ProcessSnapshot(identity.ProcessId, 1, time, f.Image, '"' + f.Image + "\" --user-data-dir=\"" + identity.UserDataPath + '"', 0));
                    var runtime = new NextRuntime { ProfileId = id, State = RuntimeState.Running, AttemptUtc = time,
                        Installation = new InstallationFingerprint(f.Image, "synthetic", new()), Root = identity, Processes = [identity], FileTokenStore = true };
                    f.Runtime.Add(runtime); await f.WriteAsync(i, runtime);
                }
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        public Task WriteAsync(int index, NextRuntime runtime) => new AtomicJsonStore(Hub.Paths.DataRoot).WriteAsync(
            Path.Combine(Hub.Paths.RuntimeStateRoot, "next-" + Ids[index].ToString("D") + ".json"), runtime, NextJsonContext.Default.NextRuntime, CancellationToken.None);
        public void Dispose() => owned.Dispose();
    }

    private sealed class Catalog : IProcessCatalog
    {
        public List<ProcessSnapshot> Rows { get; } = [];
        public List<string> Requested { get; } = [];
        public int Calls;
        public Exception? Failure;
        public Func<CancellationToken, Task>? Before;
        public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string executable, CancellationToken cancellationToken = default)
        {
            Calls++; Requested.Add(executable);
            if (Failure is not null) throw Failure;
            return ReadAsync(cancellationToken);
        }
        private async Task<IReadOnlyList<ProcessSnapshot>> ReadAsync(CancellationToken token)
        {
            var callback = Before; Before = null;
            if (callback is not null) await callback(token);
            return Rows.ToArray();
        }
    }

    private sealed class Controller : IProcessController
    {
        public int Controls;
        public bool TryFocus(ProcessSnapshot expected) { Controls++; return false; }
        public bool TryCloseMainWindow(ProcessSnapshot expected) { Controls++; return false; }
        public bool Terminate(ProcessSnapshot expected) { Controls++; return false; }
        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default) { Controls++; return Task.FromResult(false); }
        public ProcessLiveness ProbeLiveness(ProcessIdentity expected) { Controls++; return ProcessLiveness.Unknown; }
    }
}
