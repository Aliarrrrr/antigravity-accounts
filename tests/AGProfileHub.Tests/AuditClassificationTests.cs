using AGProfileHub.Core;

internal static class AuditClassificationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Audit classification Status second-read identity", () => ConfigurationAsync(false, false, false)),
        ("Audit classification Status second-read corruption", () => ConfigurationAsync(true, false, false)),
        ("Audit classification Stop second-read identity", () => ConfigurationAsync(false, false, true)),
        ("Audit classification Stop second-read corruption", () => ConfigurationAsync(true, false, true)),
        ("Audit classification first-read identity", () => ConfigurationAsync(false, true, false)),
        ("Audit classification first-read corruption", () => ConfigurationAsync(true, true, true)),
        ("Audit classification first-read Stop identity", () => ConfigurationAsync(false, true, true)),
        ("Audit classification first-read Status corruption", () => ConfigurationAsync(true, true, false)),
        ("Audit classification cancellation", CancellationAsync),
        ("Audit classification catalog failures", CatalogAsync),
        ("Audit classification ownership remains fail-closed", OwnershipAsync)
    ];
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task ConfigurationAsync(bool corrupt, bool first, bool stop)
    {
        using var f = await Fixture.CreateAsync();
        var code = corrupt ? "configuration_corrupt" : "configuration_invalid";
        async Task Mutate()
        {
            if (corrupt) await File.WriteAllTextAsync(f.Path, "{ broken");
            else await f.WriteAsync(f.Runtime with { ProfileId = Guid.NewGuid() });
        }
        if (first) await Mutate(); else f.Catalog.Before = _ => Mutate();
        if (stop || first)
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => stop ? f.Hub.StopAsync(f.Id, true) : f.Hub.StatusAsync(f.Id), code);
        else
        {
            var status = await f.Hub.StatusAsync(f.Id);
            Check(status.State == RuntimeState.Unknown && status.Detail == code, $"Expected {code}, got {status.State}/{status.Detail}.");
        }
        Check(f.Catalog.Calls == (first ? 0 : 1), "Mutation/read boundary not exercised.");
        f.AssertNoControls();
    }
    private static async Task CancellationAsync()
    {
        foreach (var stop in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync(); using var cancel = new CancellationTokenSource();
            f.Catalog.Before = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => stop ? f.Hub.StopAsync(f.Id, true, cancel.Token) : f.Hub.StatusAsync(f.Id, cancel.Token));
            Check(f.Catalog.Calls == 1, "Cancellation did not reach catalog."); f.AssertNoControls();
        }
    }
    private static async Task CatalogAsync()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("synthetic"), new IOException("synthetic"), new HubException("synthetic", "process_enumeration_unavailable") })
        {
            using var f = await Fixture.CreateAsync(); f.Catalog.Before = _ => throw failure;
            Check((await f.Hub.StatusAsync(f.Id)).Detail == "process_enumeration_unavailable", "Catalog fault lost wrapper.");
            f.Catalog.Before = _ => throw failure;
            await TestAssert.ThrowsCodeAsync<HubException>(() => f.Hub.StopAsync(f.Id, true), "process_enumeration_unavailable");
            f.AssertNoControls();
        }
    }
    private static async Task OwnershipAsync()
    {
        foreach (var kind in new[] { 0, 1, 2 })
        {
            using var f = await Fixture.CreateAsync();
            f.Catalog.Row = kind switch
            {
                0 => f.Catalog.Row with { StartTimeUtc = default },
                1 => f.Catalog.Row with { ExecutablePath = "" },
                _ => f.Catalog.Row with { ExecutablePath = "", CommandLine = "" }
            };
            Check((await f.Hub.StatusAsync(f.Id)).Detail == "process_enumeration_unavailable", "Ownership fault lost wrapper.");
            await TestAssert.ThrowsCodeAsync<HubException>(() => f.Hub.StopAsync(f.Id, true), "process_enumeration_unavailable");
            f.AssertNoControls();
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly OwnedTestDirectory owned = new();
        public Catalog Catalog { get; } = new();
        public Controller Controller { get; } = new();
        public NextHubService Hub { get; }
        public Guid Id { get; private set; }
        public NextRuntime Runtime { get; private set; } = new();
        public string Path => System.IO.Path.Combine(Hub.Paths.RuntimeStateRoot, "next-" + Id.ToString("D") + ".json");
        private Fixture() { Hub = new NextHubService(owned.Root, Catalog, Controller); }
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            try
            {
                f.Id = (await f.Hub.Profiles.CreateAsync("Synthetic", null, IsolationBackendKind.ProcessLocal)).Id;
                f.Hub.Paths.EnsureProfileLayout(f.Id);
                var image = System.IO.Path.Combine(f.owned.Root, "synthetic.exe");
                var time = DateTimeOffset.UtcNow;
                var identity = new ProcessIdentity(12345, time, image, f.Hub.Paths.UserData(f.Id));
                f.Catalog.Row = new ProcessSnapshot(12345, 1, time, image, '"' + image + "\" --user-data-dir=\"" + identity.UserDataPath + '"', 0);
                f.Runtime = new NextRuntime { ProfileId = f.Id, State = RuntimeState.Running, AttemptUtc = time,
                    Installation = new InstallationFingerprint(image, "synthetic", new()), Root = identity, Processes = [identity] };
                await f.WriteAsync(f.Runtime); return f;
            }
            catch { f.Dispose(); throw; }
        }
        public Task WriteAsync(NextRuntime runtime) => new AtomicJsonStore(Hub.Paths.DataRoot).WriteAsync(Path, runtime, NextJsonContext.Default.NextRuntime, CancellationToken.None);
        public void AssertNoControls() => Check(Controller.Controls == 0, "A failed check invoked process controls.");
        public void Dispose() => owned.Dispose();
    }
    private sealed class Catalog : IProcessCatalog
    {
        public ProcessSnapshot Row = null!;
        public Func<CancellationToken, Task>? Before;
        public int Calls;
        public async Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string executable, CancellationToken cancellationToken = default)
        {
            Calls++; var callback = Before; Before = null;
            if (callback is not null) await callback(cancellationToken);
            return [Row];
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
