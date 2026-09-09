using AGProfileHub.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("agent envelope errors never imply readiness or expose secrets", AgentProbeTests.ClassifierAsync),
    ("keyring primitives report size without reading the secret", TestKeyringPrimitivesAsync),
    ("isolation preparation fails closed and keeps unique box ownership", TestIsolationPreparationAsync),
    ("isolation state rejects corrupt duplicate mappings", TestIsolationStateValidationAsync),
    ("prepared profiles route lifecycle through isolation", TestIsolationLifecycleRoutingAsync),
    ("mixed launch-all fails before starting any profile", TestMixedIsolationLaunchAllAsync),
    ("profile CRUD preserves stable ID and data", TestProfileCrudAsync),
    ("concurrent creates retain every profile", TestConcurrentCreatesAsync),
    ("corrupt and future config are rejected without overwrite", TestInvalidConfigAsync),
    ("managed path traversal is rejected", TestPathContainmentAsync),
    ("launch plan isolates home and user-data", TestLaunchPlanAsync),
    ("quoted user-data command line is parsed", TestCommandLineAsync),
    ("duplicate launch and renderer child keep one root", TestProcessLifecycleAsync),
    ("different profiles cannot launch concurrently", TestConcurrentProfileLaunchGateAsync),
    ("launch-all refuses to partially start multiple profiles", TestLaunchAllGateAsync),
    ("concurrent profile launches are serialized", TestConcurrentProfileLaunchSerializationAsync),
    ("different data roots share the launch gate", TestCrossDataRootLaunchSerializationAsync),
    ("delayed process discovery retains the launch barrier", TestDelayedDiscoveryBarrierAsync),
    ("confirmed pre-create failure clears the launch barrier", TestPreCreateFailureClearsBarrierAsync),
    ("post-create cancellation cannot reopen the launch gate", TestPostCreateCancellationAsync),
    ("runtime write failure retains the launch barrier", TestRuntimeWriteFailureBarrierAsync),
    ("future launch barrier fails closed", TestFutureLaunchBarrierAsync),
    ("unregistered configured-executable root blocks launch", TestUnregisteredRootGateAsync),
    ("unqualified configured-executable root blocks launch", TestUnqualifiedRootGateAsync),
    ("failed child termination leaves root running", TestFailedChildTerminationAsync),
    ("force stop terminates descendants before root", TestForceStopOrderAsync),
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

static async Task TestKeyringPrimitivesAsync()
{
    var target = "AGProfileHub.Probe." + Guid.NewGuid().ToString("N");
    True(AntigravityKeyring.IsProbeTarget(target), "probe prefix is recognised");
    True(!AntigravityKeyring.IsProbeTarget("gemini:antigravity"), "vendor target is never treated as a probe");
    try
    {
        True(!WindowsCredentialStore.GetInfo(target).Exists, "a fresh probe target must be absent");
        True(WindowsCredentialStore.EnsurePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789")), "first ensure creates the entry");
        True(!WindowsCredentialStore.EnsurePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789")), "second ensure is idempotent");
        var info = WindowsCredentialStore.GetInfo(target);
        True(info.Exists, "entry is visible after creation");
        True(info.BlobSize == 10, "size is reported without exposing the blob");
        WindowsCredentialStore.WritePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789012345"));
        True(WindowsCredentialStore.GetInfo(target).BlobSize == 16, "unconditional write overwrites");
    }
    finally
    {
        WindowsCredentialStore.Delete(target);
    }
    True(!WindowsCredentialStore.GetInfo(target).Exists, "probe entry is cleaned up");
    var status = AntigravityKeyring.BuildStatus();
    True(status.Target == "gemini:antigravity", "status targets the shared vendor entry");
    True(status.Note.Length > 0, "status explains that the value is never read");
    if (!SandboxieBackend.IsCurrentProcessSandboxed())
        Throws<HubException>(() => AntigravityKeyring.EnsureIsolation(forceFresh: true));
    await Task.CompletedTask;
}

static async Task TestIsolationPreparationAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, () => backend, () => (fixture.Executable, Array.Empty<string>()));
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();

    backend.Boxes.Add("ExistingBox");
    await ThrowsAsync<HubException>(() => launcher.PrepareAsync(a, "ExistingBox"));
    var prepared = await launcher.PrepareAsync(a, "ExistingBox", claimExistingFreshBox: true);
    True(prepared.KeyringClaimed && prepared.MappingCreated && !prepared.BoxCreated);
    Equal(1, backend.ShimCalls);
    await ThrowsAsync<HubException>(() => launcher.PrepareAsync(b, "ExistingBox", claimExistingFreshBox: true));

    backend.ShimExitCode = 7;
    await ThrowsAsync<HubException>(() => launcher.PrepareAsync(b, "FreshBox"));
    var failed = await launcher.FindAsync(b);
    True(failed is not null && !failed.KeyringClaimed, "failed shim must never mark a box safe");
    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(b));
    Equal(0, backend.LaunchCalls);
}

static async Task TestIsolationStateValidationAsync()
{
    using var fixture = new Fixture();
    var idA = Guid.NewGuid();
    var idB = Guid.NewGuid();
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.IsolationStateFile)!);
    var corrupt = $$"""
        {"schemaVersion":1,"boxes":[
          {"profileId":"{{idA}}","boxName":"SameBox","createdUtc":"2026-09-09T00:00:00Z","keyringClaimed":true},
          {"profileId":"{{idB}}","boxName":"SameBox","createdUtc":"2026-09-09T00:00:00Z","keyringClaimed":true}
        ]}
        """;
    File.WriteAllText(fixture.Paths.IsolationStateFile, corrupt);
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, () => new FakeIsolationBackend());
    await ThrowsAsync<ConfigurationException>(() => launcher.FindAsync(idA));
    Equal(corrupt, File.ReadAllText(fixture.Paths.IsolationStateFile));
}

static async Task TestIsolationLifecycleRoutingAsync()
{
    var isolation = new FakeProfileIsolation();
    using var fixture = new Fixture(isolation: isolation);
    var a = await fixture.Hub.CreateProfileAsync("A");
    var b = await fixture.Hub.CreateProfileAsync("B");
    await fixture.Hub.PrepareIsolationAsync(a.Id);
    await fixture.Hub.PrepareIsolationAsync(b.Id);

    var launches = await fixture.Hub.LaunchAllAsync();
    Equal(2, launches.Count);
    True(launches.All(result => result.StartedNewProcess));
    Equal(2, isolation.LaunchCalls);
    Equal(0, fixture.World.LaunchCount);
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(a.Id)).State);
    True((await fixture.Hub.GetStatusAsync(a.Id)).IsolationBoxName is not null);

    await fixture.Hub.RestartAsync(a.Id);
    Equal(1, isolation.RestartCalls);
    await fixture.Hub.StopAsync(b.Id);
    Equal(1, isolation.StopCalls);
    Equal(0, fixture.World.CloseCalls);
}

static async Task TestMixedIsolationLaunchAllAsync()
{
    var isolation = new FakeProfileIsolation();
    using var fixture = new Fixture(isolation: isolation);
    var a = await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.CreateProfileAsync("B");
    await fixture.Hub.PrepareIsolationAsync(a.Id);

    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAllAsync());
    Equal(0, isolation.LaunchCalls);
    Equal(0, fixture.World.LaunchCount);
}

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

static async Task TestConcurrentProfileLaunchGateAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A");
    var b = await fixture.Hub.CreateProfileAsync("B");
    await fixture.Hub.LaunchAsync(a.Id);
    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAsync(b.Id));
    Equal(1, fixture.World.RootCount);
    Equal(1, fixture.World.LaunchCount);
    await fixture.Hub.StopAsync(a.Id, force: true);
    await fixture.Hub.LaunchAsync(b.Id);
    Equal(1, fixture.World.RootCount);
    Equal(2, fixture.World.LaunchCount);
}

static async Task TestLaunchAllGateAsync()
{
    using var fixture = new Fixture();
    await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.CreateProfileAsync("B");
    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAllAsync());
    Equal(0, fixture.World.LaunchCount);
}

static async Task TestConcurrentProfileLaunchSerializationAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A");
    var b = await fixture.Hub.CreateProfileAsync("B");
    var outcomes = await Task.WhenAll(
        CaptureLaunchOutcomeAsync(() => fixture.Hub.LaunchAsync(a.Id)),
        CaptureLaunchOutcomeAsync(() => fixture.Hub.LaunchAsync(b.Id)));
    Equal(1, outcomes.Count(outcome => outcome == "started"));
    Equal(1, outcomes.Count(outcome => outcome == "blocked"));
    Equal(1, fixture.World.RootCount);
}

static async Task TestCrossDataRootLaunchSerializationAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "AGProfileHub-Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    try
    {
        var executable = Path.Combine(parent, "fake-antigravity.exe");
        File.WriteAllBytes(executable, []);
        var guardRoot = Path.Combine(parent, "shared-launch-guard");
        var pathsA = new HubPaths(Path.Combine(parent, "data-a"), guardRoot);
        var pathsB = new HubPaths(Path.Combine(parent, "data-b"), guardRoot);
        var repositoryA = new ConfigRepository(pathsA);
        var repositoryB = new ConfigRepository(pathsB);
        var profilesA = new ProfileManager(repositoryA, pathsA);
        var profilesB = new ProfileManager(repositoryB, pathsB);
        var world = new FakeProcessWorld(pathsA, executable);
        var hubA = HubBootstrap.CreateForTesting(pathsA, repositoryA, profilesA, new FakeDriver(pathsA, executable), world, world, world);
        var hubB = HubBootstrap.CreateForTesting(pathsB, repositoryB, profilesB, new FakeDriver(pathsB, executable), world, world, world);
        var a = await hubA.CreateProfileAsync("A");
        var b = await hubB.CreateProfileAsync("B");

        var outcomes = await Task.WhenAll(
            CaptureLaunchOutcomeAsync(() => hubA.LaunchAsync(a.Id)),
            CaptureLaunchOutcomeAsync(() => hubB.LaunchAsync(b.Id)));

        Equal(1, outcomes.Count(outcome => outcome == "started"));
        Equal(1, outcomes.Count(outcome => outcome == "blocked"));
        Equal(1, world.RootCount);
        Equal(1, world.MaximumRootCount);
    }
    finally
    {
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}

static async Task TestDelayedDiscoveryBarrierAsync()
{
    using var fixture = new Fixture(2, TimeSpan.FromMilliseconds(1));
    var a = await fixture.Hub.CreateProfileAsync("A");
    var b = await fixture.Hub.CreateProfileAsync("B");
    fixture.World.HideSnapshots = true;

    await ThrowsAsync<LaunchStateUncertainException>(() => fixture.Hub.LaunchAsync(a.Id));
    True(File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(1, fixture.World.RootCount);
    await ThrowsAsync<LaunchStateUncertainException>(() => fixture.Hub.LaunchAsync(b.Id));
    Equal(1, fixture.World.LaunchCount);

    fixture.World.HideSnapshots = false;
    True(!(await fixture.Hub.LaunchAsync(a.Id)).StartedNewProcess);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAsync(b.Id));
    Equal(1, fixture.World.RootCount);
}

static async Task TestPreCreateFailureClearsBarrierAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    fixture.World.FailBeforeStart = true;
    await ThrowsAsync<LaunchNotStartedException>(() => fixture.Hub.LaunchAsync(profile.Id));
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    fixture.World.FailBeforeStart = false;
    True((await fixture.Hub.LaunchAsync(profile.Id)).StartedNewProcess);
}

static async Task TestPostCreateCancellationAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    using var cancellation = new CancellationTokenSource();
    fixture.World.AfterStarted = cancellation.Cancel;
    var result = await fixture.Hub.LaunchAsync(profile.Id, cancellation.Token);
    True(result.StartedNewProcess);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(1, fixture.World.RootCount);
}

static async Task TestRuntimeWriteFailureBarrierAsync()
{
    using var fixture = new Fixture(2, TimeSpan.FromMilliseconds(1));
    var a = await fixture.Hub.CreateProfileAsync("A");
    var b = await fixture.Hub.CreateProfileAsync("B");
    Directory.Delete(fixture.Paths.RuntimeStateRoot);
    File.WriteAllText(fixture.Paths.RuntimeStateRoot, "block runtime directory creation");

    await ThrowsAsync<IOException>(() => fixture.Hub.LaunchAsync(a.Id));
    True(File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(1, fixture.World.RootCount);
    await ThrowsAsync<IOException>(() => fixture.Hub.LaunchAsync(b.Id));
    Equal(1, fixture.World.LaunchCount);
}

static async Task TestFutureLaunchBarrierAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    File.WriteAllText(fixture.Paths.LaunchBarrierFile, "{\"schemaVersion\":999}");
    await ThrowsAsync<ConfigurationException>(() => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.World.LaunchCount);
}

static async Task TestUnregisteredRootGateAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    fixture.World.AddUnregisteredRoot(Path.Combine(fixture.Root, "external-user-data"));
    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.World.LaunchCount);
    Equal(1, fixture.World.RootCount);
}

static async Task TestUnqualifiedRootGateAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    fixture.World.AddUnqualifiedRoot();
    await ThrowsAsync<UnsupportedCapabilityException>(() => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.World.LaunchCount);
    Equal(1, fixture.World.RootCount);
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

static async Task TestForceStopOrderAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    await fixture.Hub.LaunchAsync(profile.Id);
    var processTree = fixture.World.AddGrandchildToRunningTree();

    var result = await fixture.Hub.StopAsync(profile.Id, force: true);

    Equal(RuntimeState.Stopped, result.Status.State);
    True(fixture.World.TerminationOrder.SequenceEqual([processTree.Grandchild, processTree.Child, processTree.Root]),
        "force stop must terminate the deepest descendant first and the root last");
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
static async Task<string> CaptureLaunchOutcomeAsync(Func<Task> action)
{
    try { await action(); return "started"; }
    catch (UnsupportedCapabilityException) { return "blocked"; }
}

sealed class Fixture : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "AGProfileHub-Tests");
    public Fixture(int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null, IProfileIsolation? isolation = null)
    {
        Root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new HubPaths(Root, Path.Combine(Root, "global-launch-state"));
        Repository = new ConfigRepository(Paths);
        Profiles = new ProfileManager(Repository, Paths);
        Executable = Path.Combine(Root, "fake-antigravity.exe");
        File.WriteAllBytes(Executable, []);
        World = new FakeProcessWorld(Paths, Executable);
        Hub = HubBootstrap.CreateForTesting(Paths, Repository, Profiles, new FakeDriver(Paths, Executable), World, World, World,
            launchVerificationAttempts: launchVerificationAttempts, launchVerificationDelay: launchVerificationDelay, isolation: isolation);
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

sealed class FakeProfileIsolation : IProfileIsolation
{
    private readonly Dictionary<Guid, IsolationBoxRecord> _boxes = [];
    private readonly HashSet<Guid> _running = [];
    public int LaunchCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int RestartCalls { get; private set; }

    public Task<IsolationBoxRecord?> FindAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_boxes.GetValueOrDefault(profileId));

    public Task<IsolationPrepareResult> PrepareAsync(Guid profileId, string? explicitBoxName = null,
        bool claimExistingFreshBox = false, CancellationToken cancellationToken = default)
    {
        var created = !_boxes.TryGetValue(profileId, out var record);
        record ??= new IsolationBoxRecord(profileId, explicitBoxName ?? IsolatedProfileLauncher.BoxNameFor(profileId), DateTimeOffset.UtcNow, true);
        _boxes[profileId] = record;
        return Task.FromResult(new IsolationPrepareResult(profileId, record.BoxName, created, created, true, created ? 11 : 0));
    }

    public Task<IsolationLaunchResult> LaunchAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var record = _boxes[profileId];
        var started = _running.Add(profileId);
        if (started) LaunchCalls++;
        return Task.FromResult(new IsolationLaunchResult(profileId, record.BoxName, [10000 + _running.Count], started));
    }

    public Task<bool> StopAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        StopCalls++;
        return Task.FromResult(_running.Remove(profileId));
    }

    public Task<IsolationLaunchResult> RestartAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        RestartCalls++;
        _running.Add(profileId);
        var record = _boxes[profileId];
        return Task.FromResult(new IsolationLaunchResult(profileId, record.BoxName, [11000 + _running.Count], true));
    }

    public Task<IsolationRuntimeStatus?> GetStatusAsync(Guid profileId, string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!_boxes.TryGetValue(profileId, out var record)) return Task.FromResult<IsolationRuntimeStatus?>(null);
        return Task.FromResult<IsolationRuntimeStatus?>(new IsolationRuntimeStatus(profileId, record.BoxName, true, true, true,
            _running.Contains(profileId) ? 1 : 0, true, []));
    }

    public async Task<IReadOnlyList<IsolationRuntimeStatus>> StatusAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<IsolationRuntimeStatus>();
        foreach (var id in _boxes.Keys)
        {
            var status = await GetStatusAsync(id, cancellationToken: cancellationToken);
            if (status is not null) result.Add(status);
        }
        return result;
    }

    public IReadOnlyList<string> WriteThroughPaths(Guid profileId, string? workspacePath = null) =>
        workspacePath is null ? [$"profile-{profileId:D}"] : [$"profile-{profileId:D}", workspacePath];
}

sealed class FakeIsolationBackend : IIsolationBackend
{
    private readonly Dictionary<string, List<int>> _pids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _settings = new(StringComparer.OrdinalIgnoreCase);
    private int _nextPid = 20000;
    public HashSet<string> Boxes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ShimExitCode { get; set; } = 11;
    public int ShimCalls { get; private set; }
    public int LaunchCalls { get; private set; }
    public bool BoxExists(string box) => Boxes.Contains(box);
    public void CreateBox(string box) => Boxes.Add(box);
    public IReadOnlyList<int> ListPids(string box) => _pids.GetValueOrDefault(box, []).ToList();
    public IReadOnlyList<string> GetIndexedSettingValues(string box, string setting, int maximum = 64) =>
        _settings.GetValueOrDefault(box, []).ToList();
    public void Terminate(string box) => _pids.Remove(box);
    public int RunInBox(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, bool wait = false)
    {
        ShimCalls++;
        return ShimExitCode;
    }
    public int LaunchDetached(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        LaunchCalls++;
        _pids[box] = [++_nextPid];
        return _nextPid;
    }
}

sealed class FakeProcessWorld(HubPaths paths, string executable) : IProcessCatalog, IProcessLauncher, IProcessController
{
    private readonly object _gate = new();
    private readonly List<ProcessSnapshot> _items = [];
    private readonly List<int> _terminationOrder = [];
    private int _nextPid = 1000;
    public int LaunchCount { get; private set; }
    public int MaximumRootCount { get; private set; }
    public int FocusCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public bool FailChildTermination { get; set; }
    public bool FailBeforeStart { get; set; }
    public bool HideSnapshots { get; set; }
    public Action? AfterStarted { get; set; }
    public int RootCount { get { lock (_gate) return _items.Count(item => item.ParentProcessId == 0); } }
    public IReadOnlyList<int> TerminationOrder { get { lock (_gate) return _terminationOrder.ToList(); } }
    public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default) { lock (_gate) return Task.FromResult<IReadOnlyList<ProcessSnapshot>>(HideSnapshots ? [] : _items.ToList()); }
    public Task<LaunchedProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FailBeforeStart) throw new LaunchNotStartedException("fake pre-create failure");
            var rootPid = _nextPid++; var childPid = _nextPid++; var started = DateTimeOffset.UtcNow; var userData = plan.Arguments.Single();
            _items.Add(new ProcessSnapshot(rootPid, 0, started, executable, $"\"{executable}\" \"{userData}\"", 1));
            _items.Add(new ProcessSnapshot(childPid, rootPid, started.AddMilliseconds(50), executable, $"\"{executable}\" \"{userData}\" --type=renderer", 0));
            LaunchCount++; MaximumRootCount = Math.Max(MaximumRootCount, _items.Count(item => item.ParentProcessId == 0));
            AfterStarted?.Invoke();
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
            _terminationOrder.Add(expected.ProcessId);
            if (FailChildTermination && expected.ParentProcessId != 0) return false;
            var index = _items.FindIndex(item => Same(item, expected));
            if (index < 0) return false;
            _items.RemoveAt(index); return true;
        }
    }
    public void AddClaim(Guid profileId, DateTimeOffset started) { lock (_gate) _items.Add(new ProcessSnapshot(_nextPid++, 0, started, executable, $"\"{executable}\" \"--user-data-dir={paths.UserData(profileId)}\"", 1)); }
    public void AddUnregisteredRoot(string userDataPath) { lock (_gate) _items.Add(new ProcessSnapshot(_nextPid++, 0, DateTimeOffset.UtcNow, executable, $"\"{executable}\" \"--user-data-dir={userDataPath}\"", 1)); }
    public void AddUnqualifiedRoot() { lock (_gate) _items.Add(new ProcessSnapshot(_nextPid++, 0, DateTimeOffset.UtcNow, executable, $"\"{executable}\"", 1)); }
    public (int Root, int Child, int Grandchild) AddGrandchildToRunningTree()
    {
        lock (_gate)
        {
            var root = _items.Single(item => item.ParentProcessId == 0);
            var child = _items.Single(item => item.ParentProcessId == root.ProcessId);
            var grandchild = _nextPid++;
            _items.Add(new ProcessSnapshot(grandchild, child.ProcessId, child.StartTimeUtc.AddMilliseconds(50), executable,
                child.CommandLine + " --type=utility", 0));
            return (root.ProcessId, child.ProcessId, grandchild);
        }
    }
    public ProcessLiveness ProbeLiveness(ProcessIdentity expected)
    {
        lock (_gate)
        {
            var samePid = _items.FirstOrDefault(item => item.ProcessId == expected.ProcessId);
            if (samePid is null) return ProcessLiveness.NotFound;
            return samePid.StartTimeUtc == expected.StartTimeUtc && string.Equals(samePid.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? ProcessLiveness.Alive : ProcessLiveness.NotFound;
        }
    }
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
