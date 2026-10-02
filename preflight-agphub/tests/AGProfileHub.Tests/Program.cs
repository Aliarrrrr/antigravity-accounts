using AGProfileHub.Core;
using System.Text.Json;

var unknownArgs = args.Where(argument => argument != "--include-system").ToArray();
if (unknownArgs.Length > 0)
{
    Console.Error.WriteLine($"未知测试参数：{string.Join(' ', unknownArgs)}；仅支持 --include-system。");
    return 2;
}
var includeSystem = args.Contains("--include-system", StringComparer.Ordinal);
var tests = new List<(string Name, Func<Task> Run)>
{
    ("agent envelope errors never imply readiness or expose secrets", AgentProbeTests.ClassifierAsync),
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
    ("schema v1 profiles remain Direct and load", TestSchemaV1MigrationAsync),
    ("sandboxie create assigns unique box ids", TestSandboxieCreateBoxIdsAsync),
    ("two sandboxie profiles can run concurrently", TestSandboxieConcurrentLaunchAsync),
    ("launch-all starts multiple sandboxie profiles", TestLaunchAllSandboxieAsync),
    ("sandboxie first launch runs credential-shim once", TestSandboxieKeyringShimOnceAsync),
    ("v1 修改保存 schema2 并将原始 v1 留在备份", TestSchemaV1MutationAsync),
    ("唯一名称解析支持大小写和首尾空白", TestUniqueNameResolutionAsync),
    ("重名解析拒绝歧义", TestAmbiguousNameResolutionAsync),
    ("UUID 解析保持稳定且优先于同名字符串", TestUuidResolutionAsync),
    ("不存在或空白的配置引用明确报错", TestMissingProfileResolutionAsync),
    ("未知 backend 拒绝创建和读取且不覆盖配置", TestUnknownBackendAsync),
    ("Sandboxie 不可用时不调用 shim 且不遗留 barrier", TestSandboxieUnavailableAsync),
    ("CLI 缺失时不创建 box 且不遗留 barrier", TestSandboxieMissingCliAsync),
    ("shim 返回 11 后才能写 marker 并启动", () => TestSandboxieShimSuccessAsync(11)),
    ("shim 返回 0 后才能写 marker 并启动", () => TestSandboxieShimSuccessAsync(0)),
    ("shim 其它返回码不留 marker、进程或 barrier", TestSandboxieShimFailureAsync),
    ("已有空 box 无 marker 时拒绝首次 shim", TestExistingBoxWithoutMarkerAsync),
    ("box 的 marker 丢失后不能再次 shim", TestSandboxieLostMarkerAsync),
    ("box 成员不符时禁止 focus、stop 和 force", TestSandboxieMembershipMismatchAsync),
    ("停止 box 清理成员且不影响其它 box", TestSandboxieStopClearsMembersAsync),
    ("PID 列表首行 count 不作为 PID", TestSandboxiePidCountAsync),
    ("空列表、错误 count 和非法 PID 拒绝解析", TestSandboxieInvalidPidsAsync),
    ("同步 shim 的启动参数包含独立 /wait", TestSandboxieWaitArgumentsAsync),
    ("宿主 shim 在凭据操作前拒绝执行", TestHostShimRejectedAsync),
    ("CLI 发现支持同目录和相邻 hub/cli 布局", TestCliDiscoveryAsync),
    ("Sandboxie 配置仅接受完整工具目录且保留其它设置", TestSandboxieConfigurationAsync),
};

var systemTests = new (string Name, Func<Task> Run)[]
{
    ("keyring primitives report size without reading the secret", TestKeyringPrimitivesAsync),
    ("shortcut binds UUID and quoted data root without overwriting", TestShortcutAsync),
};
var skipped = 0;
foreach (var test in systemTests)
{
    if (includeSystem && OperatingSystem.IsWindows()) tests.Add(test);
    else
    {
        skipped++;
        Console.WriteLine($"SKIP  {test.Name}：{(includeSystem ? "仅支持 Windows" : "未指定 --include-system")}");
    }
}
var failures = new List<string>();
var passed = 0;
foreach (var test in tests)
{
    try { await test.Run(); passed++; Console.WriteLine($"PASS  {test.Name}"); }
    catch (TestSkippedException ex) { skipped++; Console.WriteLine($"SKIP  {test.Name}：{ex.Message}"); }
    catch (Exception ex) { failures.Add($"{test.Name}: {ex.GetType().Name}: {ex.Message}"); Console.Error.WriteLine($"FAIL  {failures[^1]}"); }
}
Console.WriteLine($"{passed}/{passed + failures.Count} tests passed；SKIP {skipped}。");
return failures.Count == 0 ? 0 : 1;

static async Task TestKeyringPrimitivesAsync()
{
    var target = "AGProfileHub.Probe." + Guid.NewGuid().ToString("N");
    True(AntigravityKeyring.IsProbeTarget(target), "probe prefix is recognised");
    True(!AntigravityKeyring.IsProbeTarget("gemini:antigravity"), "vendor target is never treated as a probe");
    var created = false;
    try
    {
        True(!WindowsCredentialStore.GetInfo(target).Exists, "a fresh probe target must be absent");
        created = WindowsCredentialStore.EnsurePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789"));
        True(created, "first ensure creates the entry");
        True(!WindowsCredentialStore.EnsurePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789")), "second ensure is idempotent");
        var info = WindowsCredentialStore.GetInfo(target);
        True(info.Exists, "entry is visible after creation");
        True(info.BlobSize == 10, "size is reported without exposing the blob");
        WindowsCredentialStore.WritePlaceholder(target, "probe", System.Text.Encoding.UTF8.GetBytes("0123456789012345"));
        True(WindowsCredentialStore.GetInfo(target).BlobSize == 16, "unconditional write overwrites");
    }
    finally
    {
        if (created) WindowsCredentialStore.Delete(target);
    }
    True(!WindowsCredentialStore.GetInfo(target).Exists, "probe entry is cleaned up");
    await Task.CompletedTask;
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
    using var directory = new TestDirectory();
    var parent = directory.Root;
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
    if (!OperatingSystem.IsWindows()) throw new TestSkippedException("COM 快捷方式测试仅支持 Windows。");
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

static async Task TestSchemaV1MigrationAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var created = DateTimeOffset.UtcNow.AddMinutes(-5);
    var json = $$"""
        {"schemaVersion":1,"profiles":[{"id":"{{id:D}}","name":"Legacy","workspacePath":null,"createdUtc":"{{created:O}}","updatedUtc":"{{created:O}}"}]}
        """;
    File.WriteAllText(fixture.Paths.ProfilesFile, json);
    var profiles = await fixture.Hub.ListProfilesAsync();
    Equal(1, profiles.Count);
    Equal(ProfileBackend.Direct, profiles[0].Backend);
    True(profiles[0].BoxId is null, "v1 profiles must not have a box id");
    Equal(json, File.ReadAllText(fixture.Paths.ProfilesFile));
    True(!File.Exists(fixture.Paths.ProfilesFile + ".bak"), "只读加载不应创建迁移备份");
}

static async Task TestSchemaV1MutationAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var created = DateTimeOffset.UtcNow.AddMinutes(-5);
    var json = $$"""
        {"schemaVersion":1,"profiles":[{"id":"{{id:D}}","name":"Legacy","workspacePath":null,"createdUtc":"{{created:O}}","updatedUtc":"{{created:O}}"}]}
        """;
    File.WriteAllText(fixture.Paths.ProfilesFile, json);
    var renamed = await fixture.Hub.RenameProfileAsync(id, "Migrated");
    Equal(id, renamed.Id);
    Equal(created, renamed.CreatedUtc);
    using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Paths.ProfilesFile));
    Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
    Equal("Migrated", saved.RootElement.GetProperty("profiles")[0].GetProperty("name").GetString());
    Equal(json, File.ReadAllText(fixture.Paths.ProfilesFile + ".bak"));
    using var backup = JsonDocument.Parse(File.ReadAllText(fixture.Paths.ProfilesFile + ".bak"));
    Equal(1, backup.RootElement.GetProperty("schemaVersion").GetInt32());
    var profile = await fixture.Hub.GetProfileAsync(id);
    Equal(ProfileBackend.Direct, profile.Backend);
    True(profile.BoxId is null);
}

static async Task TestUniqueNameResolutionAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("Builder A");
    foreach (var resolve in ProfileResolvers(fixture))
    {
        Equal(profile.Id, await resolve("Builder A"));
        Equal(profile.Id, await resolve("  builder a  "));
    }
}

static async Task TestAmbiguousNameResolutionAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("Builder");
    var b = await fixture.Hub.CreateProfileAsync("builder");
    foreach (var resolve in ProfileResolvers(fixture))
    {
        await ThrowsCodeAsync("profile_ambiguous", () => resolve("  BUILDER  "));
        Equal(a.Id, await resolve(a.Id.ToString("D")));
        Equal(b.Id, await resolve(b.Id.ToString("D")));
    }
}

static async Task TestUuidResolutionAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("Stable");
    await fixture.Hub.CreateProfileAsync(profile.Id.ToString("D"));
    await fixture.Hub.RenameProfileAsync(profile.Id, "Renamed");
    foreach (var resolve in ProfileResolvers(fixture))
        foreach (var format in new[] { "D", "N", "B" })
            Equal(profile.Id, await resolve(profile.Id.ToString(format)));
}

static async Task TestMissingProfileResolutionAsync()
{
    using var fixture = new Fixture();
    await fixture.Hub.CreateProfileAsync("Existing");
    var missing = Guid.NewGuid().ToString("D");
    await fixture.Hub.CreateProfileAsync(missing);
    foreach (var resolve in ProfileResolvers(fixture))
    {
        await ThrowsCodeAsync("profile_not_found", () => resolve("Missing"));
        await ThrowsCodeAsync("profile_not_found", () => resolve(missing));
        foreach (var reference in new[] { "", " \t " })
            await ThrowsCodeAsync("invalid_profile_reference", () => resolve(reference));
    }
}

static Func<string, Task<Guid>>[] ProfileResolvers(Fixture fixture) =>
    [reference => fixture.Profiles.ResolveIdAsync(reference), reference => fixture.Hub.ResolveProfileIdAsync(reference)];

static async Task TestUnknownBackendAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    var original = File.ReadAllText(fixture.Paths.ProfilesFile);
    var directories = Directory.GetDirectories(fixture.Paths.ProfilesRoot).Length;
    await ThrowsCodeAsync("invalid_backend", () => fixture.Hub.CreateProfileAsync("Invalid", backend: (ProfileBackend)999));
    Equal(original, File.ReadAllText(fixture.Paths.ProfilesFile));
    Equal(directories, Directory.GetDirectories(fixture.Paths.ProfilesRoot).Length);
    var invalid = new ProfilesDocument { Profiles = [profile with { Backend = (ProfileBackend)999 }] };
    var json = JsonSerializer.Serialize(invalid, HubJsonContext.Default.ProfilesDocument);
    File.WriteAllText(fixture.Paths.ProfilesFile, json);
    await ThrowsCodeAsync("invalid_backend", () => fixture.Hub.ListProfilesAsync());
    await ThrowsCodeAsync("invalid_backend", () => fixture.Hub.RenameProfileAsync(profile.Id, "Invalid"));
    await ThrowsCodeAsync("invalid_backend", () => fixture.Hub.LaunchAsync(profile.Id));
    Equal(json, File.ReadAllText(fixture.Paths.ProfilesFile));
    Equal(0, fixture.World.LaunchCount);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
}

static async Task TestSandboxieCreateBoxIdsAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    var b = await fixture.Hub.CreateProfileAsync("B", backend: ProfileBackend.Sandboxie);
    Equal(ProfileBackend.Sandboxie, a.Backend);
    True(!string.IsNullOrWhiteSpace(a.BoxId), "sandboxie profile gets a box id");
    True(!string.Equals(a.BoxId, b.BoxId, StringComparison.OrdinalIgnoreCase), "box ids are unique");
    Equal($"AGPH_{a.Id:N}", a.BoxId);
    await ThrowsAsync<ConfigurationException>(() => fixture.Hub.CreateProfileAsync("C", backend: ProfileBackend.Sandboxie, boxId: a.BoxId));
}

static async Task TestSandboxieConcurrentLaunchAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    var b = await fixture.Hub.CreateProfileAsync("B", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.LaunchAsync(a.Id);
    await fixture.Hub.LaunchAsync(b.Id);
    Equal(2, fixture.World.RootCount);
    Equal(2, fixture.Sandbox.StartCalls);
    Equal(2, fixture.Sandbox.ShimCalls);
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(a.Id)).State);
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(b.Id)).State);
}

static async Task TestLaunchAllSandboxieAsync()
{
    using var fixture = new Fixture();
    await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.CreateProfileAsync("B", backend: ProfileBackend.Sandboxie);
    var results = await fixture.Hub.LaunchAllAsync();
    Equal(2, results.Count);
    True(results.All(r => r.StartedNewProcess));
    Equal(2, fixture.World.RootCount);
}

static async Task TestSandboxieKeyringShimOnceAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.LaunchAsync(a.Id);
    Equal(1, fixture.Sandbox.ShimCalls);
    await fixture.Hub.StopAsync(a.Id, force: true);
    await fixture.Hub.LaunchAsync(a.Id);
    Equal(1, fixture.Sandbox.ShimCalls);
    True(File.Exists(fixture.Paths.KeyringMarkerFile(a.Id)), "marker is written after first claim");
}
static async Task TestSandboxieUnavailableAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("Unavailable", backend: ProfileBackend.Sandboxie);
    fixture.Sandbox.IsAvailable = false;
    await ThrowsCodeAsync("unsupported_capability", () => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.Sandbox.EnsureBoxCalls);
    Equal(0, fixture.Sandbox.ShimCalls);
    AssertNoSandboxLaunch(fixture, profile.Id);
    var direct = await fixture.Hub.CreateProfileAsync("Direct");
    True((await fixture.Hub.LaunchAsync(direct.Id)).StartedNewProcess, "预检失败不得阻塞 Direct");
}

static async Task TestSandboxieMissingCliAsync()
{
    foreach (var cliPathKnown in new[] { true, false })
    {
        using var fixture = new Fixture(cliPathKnown: cliPathKnown);
        var profile = await fixture.Hub.CreateProfileAsync("Missing CLI", backend: ProfileBackend.Sandboxie);
        File.Delete(fixture.FakeCli);
        await ThrowsCodeAsync("cli_path_unknown", () => fixture.Hub.LaunchAsync(profile.Id));
        Equal(0, fixture.Sandbox.EnsureBoxCalls);
        Equal(0, fixture.Sandbox.ShimCalls);
        AssertNoSandboxLaunch(fixture, profile.Id);
        var direct = await fixture.Hub.CreateProfileAsync("Direct");
        True((await fixture.Hub.LaunchAsync(direct.Id)).StartedNewProcess);
    }
}

static async Task TestSandboxieShimSuccessAsync(int exitCode)
{
    using var fixture = new Fixture();
    fixture.Sandbox.ShimExitCode = exitCode;
    var profile = await fixture.Hub.CreateProfileAsync("Shim", backend: ProfileBackend.Sandboxie);
    var result = await fixture.Hub.LaunchAsync(profile.Id);
    True(result.StartedNewProcess);
    Equal(RuntimeState.Running, result.Status.State);
    Equal(1, fixture.Sandbox.ShimCalls);
    Equal(1, fixture.Sandbox.StartCalls);
    Equal(1, fixture.World.RootCount);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    using var marker = JsonDocument.Parse(File.ReadAllText(fixture.Paths.KeyringMarkerFile(profile.Id)));
    Equal(exitCode, marker.RootElement.GetProperty("exitCode").GetInt32());
    Equal(profile.Id, marker.RootElement.GetProperty("profileId").GetGuid());
    Equal(profile.BoxId, marker.RootElement.GetProperty("boxId").GetString());
    Equal(AntigravityKeyring.Target, marker.RootElement.GetProperty("target").GetString());
    Equal(exitCode == 11 ? AntigravityKeyring.PlaceholderPayload().Length : 0,
        marker.RootElement.GetProperty("blobSizeAfter").GetInt32());
}

static async Task TestSandboxieShimFailureAsync()
{
    foreach (var exitCode in new[] { -1, 1, 2, 10, 12, 13, 99 })
    {
        using var fixture = new Fixture();
        fixture.Sandbox.ShimExitCode = exitCode;
        var profile = await fixture.Hub.CreateProfileAsync("Failed shim", backend: ProfileBackend.Sandboxie);
        await ThrowsCodeAsync("keyring_shim_failed", () => fixture.Hub.LaunchAsync(profile.Id));
        Equal(1, fixture.Sandbox.EnsureBoxCalls);
        Equal(1, fixture.Sandbox.ShimCalls);
        AssertNoSandboxLaunch(fixture, profile.Id);
        fixture.Sandbox.ShimExitCode = 11;
        await ThrowsCodeAsync("keyring_marker_missing", () => fixture.Hub.LaunchAsync(profile.Id));
        Equal(1, fixture.Sandbox.ShimCalls);
        AssertNoSandboxLaunch(fixture, profile.Id);
        var direct = await fixture.Hub.CreateProfileAsync("Direct");
        True((await fixture.Hub.LaunchAsync(direct.Id)).StartedNewProcess);
    }
}

static async Task TestExistingBoxWithoutMarkerAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("Existing box", backend: ProfileBackend.Sandboxie);
    True(await fixture.Sandbox.EnsureBoxAsync(profile.BoxId!, [fixture.Paths.Home(profile.Id)]));
    True(!await fixture.Sandbox.EnsureBoxAsync(profile.BoxId!, [fixture.Paths.Home(profile.Id)]));
    Equal(1, fixture.Sandbox.ConfiguredBoxes.Count);
    await ThrowsCodeAsync("keyring_marker_missing", () => fixture.Hub.LaunchAsync(profile.Id));
    Equal(0, fixture.Sandbox.ShimCalls);
    AssertNoSandboxLaunch(fixture, profile.Id);
}

static async Task TestSandboxieLostMarkerAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("Lost marker", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.LaunchAsync(profile.Id);
    await fixture.Hub.StopAsync(profile.Id, force: true);
    Equal(0, (await fixture.Sandbox.ListProcessesAsync(profile.BoxId!)).Count);
    var preserved = Path.Combine(fixture.Paths.ProfileRoot(profile.Id), "preserved-data.txt");
    File.WriteAllText(preserved, "仅合成数据");
    File.Delete(fixture.Paths.KeyringMarkerFile(profile.Id));
    await ThrowsCodeAsync("keyring_marker_missing", () => fixture.Hub.LaunchAsync(profile.Id));
    Equal(1, fixture.Sandbox.ShimCalls);
    Equal(1, fixture.Sandbox.StartCalls);
    Equal(1, fixture.World.LaunchCount);
    Equal(0, fixture.World.RootCount);
    Equal("仅合成数据", File.ReadAllText(preserved));
    True(!File.Exists(fixture.Paths.KeyringMarkerFile(profile.Id)));
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
}

static async Task TestSandboxieMembershipMismatchAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    var b = await fixture.Hub.CreateProfileAsync("B", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.LaunchAsync(a.Id);
    await fixture.Hub.LaunchAsync(b.Id);
    fixture.Sandbox.HiddenBoxId = a.BoxId;
    Equal(RuntimeState.Unknown, (await fixture.Hub.GetStatusAsync(a.Id)).State);
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(b.Id)).State);
    var statuses = await fixture.Hub.GetAllStatusesAsync();
    Equal(RuntimeState.Unknown, statuses.Single(status => status.ProfileId == a.Id).State);
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.LaunchAsync(a.Id));
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.StopAsync(a.Id));
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.StopAsync(a.Id, force: true));
    await ThrowsAsync<ProcessOwnershipException>(() => fixture.Hub.RestartAsync(a.Id, force: true));
    Equal(0, fixture.World.FocusCalls);
    Equal(0, fixture.World.CloseCalls);
    Equal(0, fixture.World.TerminationOrder.Count);
    Equal(0, fixture.Sandbox.TerminateCalls);
    Equal(2, fixture.World.RootCount);
    Equal(2, fixture.World.LaunchCount);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    fixture.Sandbox.HiddenBoxId = null;
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(a.Id)).State);
}

static async Task TestSandboxieStopClearsMembersAsync()
{
    using var fixture = new Fixture();
    var a = await fixture.Hub.CreateProfileAsync("A", backend: ProfileBackend.Sandboxie);
    var b = await fixture.Hub.CreateProfileAsync("B", backend: ProfileBackend.Sandboxie);
    await fixture.Hub.LaunchAsync(a.Id);
    await fixture.Hub.LaunchAsync(b.Id);
    var aMembers = await fixture.Sandbox.ListProcessesAsync(a.BoxId!);
    var bMembers = await fixture.Sandbox.ListProcessesAsync(b.BoxId!);
    Equal(2, aMembers.Count);
    Equal(2, bMembers.Count);
    Equal(RuntimeState.Stopped, (await fixture.Hub.StopAsync(a.Id, force: true)).Status.State);
    Equal(0, (await fixture.Sandbox.ListProcessesAsync(a.BoxId!)).Count);
    True((await fixture.World.SnapshotAsync(fixture.Executable)).All(snapshot => aMembers.All(member => member.ProcessId != snapshot.ProcessId)));
    True(bMembers.SequenceEqual(await fixture.Sandbox.ListProcessesAsync(b.BoxId!)));
    Equal(RuntimeState.Running, (await fixture.Hub.GetStatusAsync(b.Id)).State);
    Equal(1, fixture.World.RootCount);
    await fixture.Hub.StopAsync(b.Id, force: true);
    Equal(0, (await fixture.Sandbox.ListProcessesAsync(b.BoxId!)).Count);
    Equal(0, (await fixture.World.SnapshotAsync(fixture.Executable)).Count);
    Equal(2, fixture.Sandbox.TerminateCalls);
}

static Task TestSandboxiePidCountAsync()
{
    True(WindowsSandboxieRuntime.ParseProcessIds("2\r\n1234\r\n5678\r\n").SequenceEqual([1234, 5678]), "首行 count 不是 PID");
    True(WindowsSandboxieRuntime.ParseProcessIds("\uFEFF1\n 42 \n").SequenceEqual([42]));
    Equal(0, WindowsSandboxieRuntime.ParseProcessIds("0\r\n").Count);
    return Task.CompletedTask;
}

static Task TestSandboxieInvalidPidsAsync()
{
    foreach (var output in new[] { "", " \r\n ", "1", "2\n1234", "1\n1234\n5678", "-1", "count\n1234",
        "1\n0", "1\n-2", "1\nabc", "2\n1234\n1234", "2147483647", "2147483648\n1234", "1\n2147483648" })
        ThrowsCode("sandbox_query_failed", () => WindowsSandboxieRuntime.ParseProcessIds(output));
    return Task.CompletedTask;
}

static Task TestSandboxieWaitArgumentsAsync()
{
    using var fixture = new Fixture();
    var box = "AGPH_" + Guid.NewGuid().ToString("N");
    var environment = new Dictionary<string, string> { ["USERPROFILE"] = Path.Combine(fixture.Root, "home with spaces") };
    string[] command = ["credential-shim", "--box-id", box, "--force", "--fresh"];
    var wait = WindowsSandboxieRuntime.BuildStartArguments(box, fixture.FakeCli, command, environment, wait: true);
    var noWait = WindowsSandboxieRuntime.BuildStartArguments(box, fixture.FakeCli, command, environment, wait: false);
    True(wait.SequenceEqual(new[] { $"/box:{box}", "/silent", "/wait", $"/env:USERPROFILE={environment["USERPROFILE"]}", fixture.FakeCli }.Concat(command)));
    True(!noWait.Contains("/wait"));
    True(noWait.SequenceEqual(wait.Where(argument => argument != "/wait")));
    ThrowsCode("executable_path_invalid", () => WindowsSandboxieRuntime.BuildStartArguments(box, "relative.exe", command, environment, wait: true));
    return Task.CompletedTask;
}

static Task TestHostShimRejectedAsync()
{
    if (!OperatingSystem.IsWindows()) throw new TestSkippedException("宿主保护验证仅在 Windows 上执行。");
    if (!string.IsNullOrWhiteSpace(SandboxProcessContext.CurrentBox()))
        throw new TestSkippedException("测试进程已在 sandbox 内，禁止触及该 box 的凭据。");
    ThrowsCode("sandbox_context_required", () => AntigravityKeyring.EnsureIsolation());
    ThrowsCode("sandbox_context_required", () => AntigravityKeyring.EnsureIsolation(forceFresh: true));
    ThrowsCode("sandbox_context_required", () => AntigravityKeyring.EnsureIsolation(forceFresh: true, expectedBox: "AGPH_" + Guid.NewGuid().ToString("N")));
    return Task.CompletedTask;
}

static Task TestCliDiscoveryAsync()
{
    using var fixture = new Fixture();
    // 让源码构建候选路径的四层向上查找也留在本测试自建目录中。
    var package = Path.Combine(fixture.Root, "package", "a", "b", "c", "d");
    var hub = Path.Combine(package, "hub");
    var cli = Path.Combine(package, "cli");
    Directory.CreateDirectory(hub);
    Directory.CreateDirectory(cli);
    ThrowsCode("cli_not_found", () => DesktopShortcut.FindCliExecutable(hub));
    var sibling = Path.Combine(cli, "AGProfileHub.Cli.exe");
    File.WriteAllBytes(sibling, [0x4D, 0x5A]);
    Equal(Path.GetFullPath(sibling), DesktopShortcut.FindCliExecutable(hub));
    var adjacent = Path.Combine(hub, "AGProfileHub.Cli.exe");
    File.WriteAllBytes(adjacent, [0x4D, 0x5A]);
    Equal(Path.GetFullPath(adjacent), DesktopShortcut.FindCliExecutable(hub));
    return Task.CompletedTask;
}

static async Task TestSandboxieConfigurationAsync()
{
    using var fixture = new Fixture();
    var original = new HubSettings
    {
        AntigravityExecutablePath = fixture.Executable,
        LastTestedVersion = "2.12.2",
        AuthIsolationAttestedUtc = DateTimeOffset.UtcNow,
        AuthIsolationAttestedVersion = "2.12.2"
    };
    await fixture.Repository.WriteSettingsAsync(original);
    Equal(original, await fixture.Hub.GetSettingsAsync());
    var saved = File.ReadAllText(fixture.Paths.SettingsFile);
    var tools = Path.Combine(fixture.Root, "fake tools");
    Directory.CreateDirectory(tools);
    foreach (var invalid in new[] { "relative-tools", Path.Combine(fixture.Root, "missing-tools"), tools })
    {
        await ThrowsCodeAsync("sandbox_path_invalid", () => fixture.Hub.ConfigureSandboxieAsync(invalid));
        Equal(saved, File.ReadAllText(fixture.Paths.SettingsFile));
    }
    var start = Path.Combine(tools, "Start.exe");
    var ini = Path.Combine(tools, "SbieIni.exe");
    File.WriteAllBytes(start, [0x4D, 0x5A]);
    await ThrowsCodeAsync("sandbox_path_invalid", () => fixture.Hub.ConfigureSandboxieAsync(tools));
    Equal(saved, File.ReadAllText(fixture.Paths.SettingsFile));
    File.Delete(start);
    File.WriteAllBytes(ini, [0x4D, 0x5A]);
    await ThrowsCodeAsync("sandbox_path_invalid", () => fixture.Hub.ConfigureSandboxieAsync(tools));
    Equal(saved, File.ReadAllText(fixture.Paths.SettingsFile));
    File.WriteAllBytes(start, [0x4D, 0x5A]);
    var configured = await fixture.Hub.ConfigureSandboxieAsync(Path.Combine(tools, "."));
    Equal(original with { SandboxieHome = Path.GetFullPath(tools) }, configured);
    Equal(configured, await fixture.Hub.GetSettingsAsync());
    var runtime = new WindowsSandboxieRuntime(configured.SandboxieHome);
    Equal(Path.GetFullPath(tools), runtime.Home);
    Equal(OperatingSystem.IsWindows(), runtime.IsAvailable);
    Equal(original, await fixture.Hub.ConfigureSandboxieAsync(null));
    await fixture.Hub.ConfigureSandboxieAsync(tools);
    Equal(original, await fixture.Hub.ConfigureSandboxieAsync(" "));
    Equal(original, await fixture.Hub.GetSettingsAsync());
    Equal(0, fixture.World.LaunchCount);
    Equal(0, fixture.Sandbox.EnsureBoxCalls);
}

static void AssertNoSandboxLaunch(Fixture fixture, Guid profileId)
{
    True(!File.Exists(fixture.Paths.KeyringMarkerFile(profileId)), "失败预检不得留下 marker");
    True(!File.Exists(fixture.Paths.LaunchBarrierFile), "失败预检不得留下全局 barrier");
    Equal(0, fixture.World.LaunchCount);
    Equal(0, fixture.World.RootCount);
    Equal(0, fixture.Sandbox.StartCalls);
}

static void ThrowsCode(string code, Action action)
{
    try { action(); }
    catch (HubException ex) { Equal(code, ex.Code); return; }
    throw new InvalidOperationException($"预期错误码 {code}。");
}

static async Task ThrowsCodeAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (HubException ex) { Equal(code, ex.Code); return; }
    throw new InvalidOperationException($"预期错误码 {code}。");
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

sealed class TestSkippedException(string message) : Exception(message);

sealed class TestDirectory : IDisposable
{
    private readonly string _parent;
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public TestDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AGPH_TEST_ROOT");
        _parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data"));
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("AGPH_TEST_ROOT 必须是项目内的绝对路径。");
            _parent = Path.GetFullPath(configured);
            var project = new DirectoryInfo(_parent);
            while (project is not null && !File.Exists(Path.Combine(project.FullName, "tests", "AGProfileHub.Tests", "AGProfileHub.Tests.csproj")))
                project = project.Parent;
            if (project is null)
                throw new InvalidOperationException("AGPH_TEST_ROOT 必须位于含 AGProfileHub.Tests 项目的目录内。");
        }
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.DesktopDirectory })
        {
            var forbidden = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbidden)) + Path.DirectorySeparatorChar;
            if ((_parent + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试数据不能位于用户 AppData 或桌面目录。");
        }
        PathSafety.RejectExistingPathAncestors(_parent);
        Directory.CreateDirectory(_parent);
        Root = Path.Combine(_parent, "case-" + _owner);
        if (Directory.Exists(Root) || File.Exists(Root))
            throw new InvalidOperationException("测试目录已存在，拒绝复用或清理。");
        Directory.CreateDirectory(Root);
        using var marker = new StreamWriter(new FileStream(Path.Combine(Root, ".agph-test-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None));
        marker.Write(_owner);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        PathSafety.RejectExistingReparsePoints(_parent, Root);
        var marker = Path.Combine(Root, ".agph-test-owner");
        PathSafety.RejectExistingPathAncestors(marker);
        if (!File.Exists(marker) || File.ReadAllText(marker) != _owner)
            throw new InvalidOperationException("测试目录所有权标记不符，拒绝清理。");
        Directory.Delete(Root, recursive: true);
    }
}

sealed class Fixture : IDisposable
{
    private readonly TestDirectory _directory = new();
    public Fixture(int launchVerificationAttempts = 20, TimeSpan? launchVerificationDelay = null, bool cliPathKnown = true)
    {
        Paths = new HubPaths(Root, Path.Combine(Root, "global-launch-state"));
        Repository = new ConfigRepository(Paths);
        Profiles = new ProfileManager(Repository, Paths);
        Executable = Path.Combine(Root, "fake-antigravity.exe");
        File.WriteAllBytes(Executable, []);
        FakeCli = Path.Combine(Root, "AGProfileHub.Cli.exe");
        File.WriteAllBytes(FakeCli, [0x4D, 0x5A]);
        World = new FakeProcessWorld(Paths, Executable);
        Sandbox = new FakeSandboxRuntime(World, Executable);
        Hub = HubBootstrap.CreateForTesting(Paths, Repository, Profiles, new FakeDriver(Paths, Executable), World, World, World,
            sandbox: Sandbox, hubCliExecutable: cliPathKnown ? FakeCli : null,
            launchVerificationAttempts: launchVerificationAttempts, launchVerificationDelay: launchVerificationDelay);
    }
    public string Root => _directory.Root;
    public string Executable { get; }
    public string FakeCli { get; }
    public HubPaths Paths { get; }
    public ConfigRepository Repository { get; }
    public ProfileManager Profiles { get; }
    public FakeProcessWorld World { get; }
    public FakeSandboxRuntime Sandbox { get; }
    public HubService Hub { get; }
    public void Dispose() => _directory.Dispose();
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

sealed class FakeSandboxRuntime(FakeProcessWorld world, string executable) : ISandboxRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<int>> _boxPids = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _configuredBoxes = [];
    public int EnsureBoxCalls { get; private set; }
    public int ShimCalls { get; private set; }
    public int StartCalls { get; private set; }
    public int TerminateCalls { get; private set; }
    public bool IsAvailable { get; set; } = true;
    public int ShimExitCode { get; set; } = 11;
    public string? HiddenBoxId { get; set; }
    public string RuntimeDescription => "FakeSandboxRuntime";
    public IReadOnlyList<string> ConfiguredBoxes { get { lock (_gate) return _configuredBoxes.ToList(); } }

    public Task<bool> EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default)
    {
        WindowsSandboxieRuntime.ValidateBoxId(boxId);
        lock (_gate)
        {
            EnsureBoxCalls++;
            var fresh = !_configuredBoxes.Contains(boxId, StringComparer.OrdinalIgnoreCase);
            if (fresh) _configuredBoxes.Add(boxId);
            if (!_boxPids.ContainsKey(boxId)) _boxPids[boxId] = [];
            return Task.FromResult(fresh);
        }
    }

    public Task<int> RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (arguments.SequenceEqual(new[] { "credential-shim", "--box-id", boxId, "--force", "--fresh" }))
        {
            lock (_gate)
            {
                ShimCalls++;
                return Task.FromResult(ShimExitCode);
            }
        }
        throw new HubException($"非预期的 box 内命令：{string.Join(' ', arguments)}", "sandbox_command_failed");
    }

    public async Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default)
    {
        var plan = new LaunchPlan(executablePath, "2.12.2", arguments, environment, "sandbox fake launch");
        var started = await world.LaunchAsync(plan, cancellationToken);
        var snapshots = await world.SnapshotAsync(executable, cancellationToken);
        lock (_gate)
        {
            StartCalls++;
            if (!_boxPids.TryGetValue(boxId, out var set))
            {
                set = [];
                _boxPids[boxId] = set;
            }
            set.Add(started.ProcessId);
            foreach (var child in snapshots.Where(snapshot => snapshot.ParentProcessId == started.ProcessId))
                set.Add(child.ProcessId);
        }
    }

    public Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (string.Equals(HiddenBoxId, boxId, StringComparison.OrdinalIgnoreCase) || !_boxPids.TryGetValue(boxId, out var set))
                return Task.FromResult<IReadOnlyList<SandboxRuntimeSnapshot>>([]);
            return Task.FromResult<IReadOnlyList<SandboxRuntimeSnapshot>>(
                set.Select(pid => new SandboxRuntimeSnapshot(pid, "Antigravity", executable)).ToList());
        }
    }

    public async Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default)
    {
        HashSet<int> pids;
        lock (_gate)
        {
            TerminateCalls++;
            if (!_boxPids.TryGetValue(boxId, out var set)) return;
            pids = set.ToHashSet();
        }
        var snapshots = await world.SnapshotAsync(executable, cancellationToken);
        // FakeProcessWorld 按父进程在前的顺序创建，反向停止以避免留下 renderer。
        foreach (var snapshot in snapshots.Reverse().Where(snapshot => pids.Contains(snapshot.ProcessId)))
            world.Terminate(snapshot);
        var remaining = await world.SnapshotAsync(executable, cancellationToken);
        lock (_gate)
        {
            _boxPids[boxId].IntersectWith(remaining.Select(snapshot => snapshot.ProcessId));
        }
    }
}

