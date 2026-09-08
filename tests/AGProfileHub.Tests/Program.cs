using AGProfileHub.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("profile CRUD preserves stable ID and data", TestProfileCrudAsync),
    ("concurrent creates retain every profile", TestConcurrentCreatesAsync),
    ("corrupt and future config are rejected without overwrite", TestInvalidConfigAsync),
    ("managed path traversal is rejected", TestPathContainmentAsync),
    ("launch plan isolates home and user-data", TestLaunchPlanAsync),
    ("quoted user-data command line is parsed", TestCommandLineAsync),
    ("duplicate launch and renderer child keep one root", TestProcessLifecycleAsync),
    ("failed child termination leaves root running", TestFailedChildTerminationAsync),
    ("invalid creation time becomes unknown", TestInvalidCreationTimeAsync),
    ("future runtime schema is rejected", TestRuntimeSchemaAsync),
    ("concurrent restarts remain serialized", TestConcurrentRestartAsync),
    ("malformed configured executable is a configuration error", TestMalformedExecutableAsync),
    ("unknown versions cannot create launch directories", TestUnknownVersionAsync),
    ("executable changes clear prior authentication attestation", TestExecutableChangeAsync),
    ("reused PID never receives close or focus", TestReusedPidAsync),
    ("shortcut binds UUID and quoted data root without overwriting", TestShortcutAsync),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures.Add($"{test.Name}: {ex.GetType().Name}: {ex.Message}"); Console.Error.WriteLine($"FAIL  {failures[^1]}"); }
}
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static async Task TestProfileCrudAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("  Pro A  ");
    Equal("Pro A", profile.Name);
    File.WriteAllText(Path.Combine(fixture.Paths.ProfileRoot(profile.Id), "keep.txt"), "fixture");
    var renamed = await fixture.Hub.RenameProfileAsync(profile.Id, "Builder");
    Equal(profile.Id, renamed.Id);
    var removed = await fixture.Hub.RemoveProfileAsync(profile.Id);
    Equal(profile.Id, removed.Id);
    True(File.Exists(Path.Combine(fixture.Paths.ProfileRoot(profile.Id), "keep.txt")), "remove must preserve profile data");
}

static async Task TestConcurrentCreatesAsync()
{
    using var fixture = new Fixture();
    await Task.WhenAll(Enumerable.Range(0, 8).Select(index => fixture.Hub.CreateProfileAsync($"Profile {index}")));
    var profiles = await fixture.Hub.ListProfilesAsync();
    Equal(8, profiles.Count);
    Equal(8, profiles.Select(profile => profile.Id).Distinct().Count());
}

static async Task TestInvalidConfigAsync()
{
    using var fixture = new Fixture();
    await fixture.Hub.CreateProfileAsync("A");
    foreach (var content in new[] { "{oops", "{\"schemaVersion\":999,\"profiles\":[]}", "{\"schemaVersion\":1,\"profiles\":null}" })
    {
        File.WriteAllText(fixture.Paths.ProfilesFile, content);
        await ThrowsAsync<ConfigurationException>(() => fixture.Hub.ListProfilesAsync());
        Equal(content, File.ReadAllText(fixture.Paths.ProfilesFile));
    }
}

static Task TestPathContainmentAsync()
{
    using var fixture = new Fixture();
    Throws<ConfigurationException>(() => PathSafety.EnsureContained(fixture.Paths.DataRoot, Path.Combine(fixture.Paths.DataRoot, "..", "outside")));
    return Task.CompletedTask;
}

static Task TestLaunchPlanAsync()
{
    using var fixture = new Fixture();
    var driver = new AntigravityDriver(fixture.Paths, fixture.Repository);
    var plan = driver.BuildLaunchPlan(new AntigravityInstallation(fixture.Executable, "2.12.2", true, "test"), Guid.NewGuid());
    True(plan.Arguments.Single().StartsWith("--user-data-dir=", StringComparison.Ordinal));
    True(plan.Environment["USERPROFILE"].EndsWith("home", StringComparison.OrdinalIgnoreCase));
    True(plan.Environment["HOMEPATH"].StartsWith('\\'));
    True(!plan.SanitizedCommandTemplate.Contains(fixture.Paths.DataRoot, StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static Task TestCommandLineAsync()
{
    var path = CommandLineIdentity.TryGetUserDataPath("\"C:\\Program Files\\Antigravity.exe\" \"--user-data-dir=C:\\Data Root\\Profile A\" --type=renderer");
    Equal(Path.GetFullPath("C:\\Data Root\\Profile A"), path);
    return Task.CompletedTask;
}

static async Task TestProcessLifecycleAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    True((await fixture.Hub.LaunchAsync(profile.Id)).StartedNewProcess);
    True(!(await fixture.Hub.LaunchAsync(profile.Id)).StartedNewProcess);
    Equal(1, fixture.World.LaunchCount);
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(profile.Id)).State);
    Equal(RuntimeState.Stopped, (await fixture.Hub.StopAsync(profile.Id, force: true)).Status.State);
    Equal(0, fixture.World.RootCount);
}

static async Task TestFailedChildTerminationAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.LaunchAsync(profile.Id);
    fixture.World.FailChildTermination = true;
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.StopAsync(profile.Id, force: true));
    Equal(1, fixture.World.RootCount);
}

static async Task TestInvalidCreationTimeAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    fixture.World.AddClaim(profile.Id, DateTimeOffset.MinValue);
    Equal(RuntimeState.Unknown, (await fixture.Hub.GetStatusAsync(profile.Id)).State);
    Equal(0, fixture.World.FocusCalls + fixture.World.CloseCalls);
}

static async Task TestRuntimeSchemaAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    Directory.CreateDirectory(fixture.Paths.RuntimeStateRoot);
    File.WriteAllText(fixture.Paths.RuntimeFile(profile.Id), $$"""
        {"schemaVersion":999,"profileId":"{{profile.Id}}","root":{"processId":42,"startTimeUtc":"2026-09-07T00:00:00Z","executablePath":"{{Escape(fixture.Executable)}}","userDataPath":"{{Escape(fixture.Paths.UserData(profile.Id))}}"},"updatedUtc":"2026-09-07T00:00:00Z"}
        """);
    await ThrowsAsync<ConfigurationException>(() => fixture.Hub.GetStatusAsync(profile.Id));
}

static async Task TestConcurrentRestartAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.LaunchAsync(profile.Id);
    await Task.WhenAll(fixture.Hub.RestartAsync(profile.Id, force: true), fixture.Hub.RestartAsync(profile.Id, force: true));
    Equal(1, fixture.World.RootCount);
    Equal(1, fixture.World.MaximumRootCount);
    Equal(3, fixture.World.LaunchCount);
}

static async Task TestMalformedExecutableAsync()
{
    using var fixture = new Fixture();
    await fixture.Repository.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = "bad\0path" });
    await ThrowsAsync<ConfigurationException>(() => new AntigravityDriver(fixture.Paths, fixture.Repository).DiscoverAsync());
}

static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
static Task TestUnknownVersionAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var driver = new AntigravityDriver(fixture.Paths, fixture.Repository);
    Throws<HubException>(() => driver.BuildLaunchPlan(new AntigravityInstallation(fixture.Executable, "99.0.0", false, "Unverified version"), id));
    True(!Directory.Exists(fixture.Paths.ProfileRoot(id)), "unsupported version must not mutate profile data");
    return Task.CompletedTask;
}

static async Task TestExecutableChangeAsync()
{
    using var fixture = new Fixture();
    await fixture.Repository.WriteSettingsAsync(new HubSettings
    {
        AntigravityExecutablePath = fixture.Executable,
        AuthIsolationAttestedUtc = DateTimeOffset.UtcNow,
        AuthIsolationAttestedVersion = "2.12.2"
    });
    var same = await fixture.Hub.ConfigureExecutableAsync(fixture.Executable);
    True(same.AuthIsolationAttestedUtc is not null);
    var replacement = Path.Combine(fixture.Root, "replacement.exe");
    File.WriteAllBytes(replacement, []);
    var changed = await fixture.Hub.ConfigureExecutableAsync(replacement);
    True(changed.AuthIsolationAttestedUtc is null && changed.AuthIsolationAttestedVersion is null);
}

static async Task TestReusedPidAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.LaunchAsync(profile.Id);
    fixture.World.ReuseRootPid();
    Equal(RuntimeState.Unknown, (await fixture.Hub.GetStatusAsync(profile.Id)).State);
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.StopAsync(profile.Id, force: true));
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.World.CloseCalls + fixture.World.FocusCalls);
    Equal(1, fixture.World.RootCount);
}

static async Task TestShortcutAsync()
{
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("测试 / A");
    var directory = Path.Combine(fixture.Root, "桌面 with spaces");
    Directory.CreateDirectory(directory);
    var path = await fixture.Hub.CreateShortcutAsync(profile.Id, fixture.Executable, directory);
    var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
    object? link = null;
    try
    {
        link = ((dynamic)shell!).CreateShortcut(path);
        Equal(fixture.Executable, (string)((dynamic)link).TargetPath);
        Equal($"--data-root {DesktopShortcut.QuoteArgument(fixture.Paths.DataRoot)} launch {profile.Id:D}", (string)((dynamic)link).Arguments);
        await fixture.Hub.RenameProfileAsync(profile.Id, "Renamed");
        True(((string)((dynamic)link).Arguments).EndsWith(profile.Id.ToString("D")));
    }
    finally
    {
        if (link is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }
    var bytes = File.ReadAllBytes(path);
    await fixture.Hub.RenameProfileAsync(profile.Id, profile.Name);
    await ThrowsAsync<HubException>(() => fixture.Hub.CreateShortcutAsync(profile.Id, fixture.Executable, directory));
    True(bytes.SequenceEqual(File.ReadAllBytes(path)), "existing shortcut must be preserved");
    Equal("\"D:\\data root\\\\\"", DesktopShortcut.QuoteArgument("D:\\data root\\"));
}
static void True(bool condition, string? message = null) { if (!condition) throw new InvalidOperationException(message ?? "Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

sealed class Fixture : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "AGProfileHub-Tests");
    public Fixture()
    {
        Root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new HubPaths(Root);
        Repository = new ConfigRepository(Paths);
        Profiles = new ProfileManager(Repository, Paths);
        Executable = Path.Combine(Root, "fake-antigravity.exe");
        File.WriteAllBytes(Executable, []);
        World = new FakeProcessWorld(Paths, Executable);
        Hub = HubBootstrap.CreateForTesting(Paths, Repository, Profiles, new FakeDriver(Paths, Executable), World, World, World);
    }
    public string Root { get; }
    public string Executable { get; }
    public HubPaths Paths { get; }
    public ConfigRepository Repository { get; }
    public ProfileManager Profiles { get; }
    public FakeProcessWorld World { get; }
    public HubService Hub { get; }
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        var parent = Path.GetFullPath(Parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

sealed class FakeDriver(HubPaths paths, string executable) : IAntigravityDriver
{
    public Task<AntigravityInstallation> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AntigravityInstallation(executable, "2.12.2", true, "test"));
    public LaunchPlan BuildLaunchPlan(AntigravityInstallation installation, Guid profileId)
    {
        paths.EnsureProfileLayout(profileId);
        return new LaunchPlan(executable, "2.12.2", [$"--user-data-dir={paths.UserData(profileId)}"], new Dictionary<string, string>(), "fake --user-data-dir=<managed>");
    }
}

sealed class FakeProcessWorld(HubPaths paths, string executable) : IProcessCatalog, IProcessLauncher, IProcessController
{
    private readonly object _gate = new();
    private readonly List<ProcessSnapshot> _items = [];
    private int _nextPid = 1000;
    public int LaunchCount { get; private set; }
    public int MaximumRootCount { get; private set; }
    public int FocusCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public bool FailChildTermination { get; set; }
    public int RootCount { get { lock (_gate) return _items.Count(item => item.ParentProcessId == 0); } }
    public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default) { lock (_gate) return Task.FromResult<IReadOnlyList<ProcessSnapshot>>(_items.ToList()); }
    public Task<LaunchedProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var rootPid = _nextPid++; var childPid = _nextPid++; var started = DateTimeOffset.UtcNow; var userData = plan.Arguments.Single();
            _items.Add(new ProcessSnapshot(rootPid, 0, started, executable, $"\"{executable}\" \"{userData}\"", 1));
            _items.Add(new ProcessSnapshot(childPid, rootPid, started.AddMilliseconds(50), executable, $"\"{executable}\" \"{userData}\" --type=renderer", 0));
            LaunchCount++; MaximumRootCount = Math.Max(MaximumRootCount, _items.Count(item => item.ParentProcessId == 0));
            return Task.FromResult(new LaunchedProcess(rootPid, started));
        }
    }
    public bool TryFocus(ProcessSnapshot expected) { lock (_gate) { FocusCalls++; return Contains(expected); } }
    public bool TryCloseMainWindow(ProcessSnapshot expected) { lock (_gate) { CloseCalls++; return false; } }
    public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default) { lock (_gate) return Task.FromResult(_items.All(item => item.ProcessId != processId)); }
    public bool Terminate(ProcessSnapshot expected)
    {
        lock (_gate)
        {
            if (FailChildTermination && expected.ParentProcessId != 0) return false;
            var index = _items.FindIndex(item => Same(item, expected));
            if (index < 0) return false;
            _items.RemoveAt(index); return true;
        }
    }
    public void AddClaim(Guid profileId, DateTimeOffset started) { lock (_gate) _items.Add(new ProcessSnapshot(_nextPid++, 0, started, executable, $"\"{executable}\" \"--user-data-dir={paths.UserData(profileId)}\"", 1)); }
    public void ReuseRootPid()
    {
        lock (_gate)
        {
            var root = _items.Single(item => item.ParentProcessId == 0);
            _items.Clear();
            _items.Add(root with { StartTimeUtc = root.StartTimeUtc.AddMinutes(1), CommandLine = "unrelated process" });
        }
    }
    private bool Contains(ProcessSnapshot expected) => _items.Any(item => Same(item, expected));
    private static bool Same(ProcessSnapshot left, ProcessSnapshot right) => left.ProcessId == right.ProcessId && left.StartTimeUtc == right.StartTimeUtc && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase);
}
