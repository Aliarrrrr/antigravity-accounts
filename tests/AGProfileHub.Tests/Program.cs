using System.Text.Json;
using AGProfileHub.Core;

var declaredTests = new (string Name, Func<Task> Run)[]
{
    ("Next desktop adapter executes a default-root isolated wrapper", NextTests.DesktopLaunchAsync),
    ("Next component/version/strategy changes invalidate proof", NextTests.UpgradeAsync),
    ("Next ownership excludes reused PIDs, similar paths and foreign images", NextTests.OwnershipAsync),
    ("Next JSONC inheritance retains comments/conflicts and skips unknown secrets", NextTests.JsoncAsync),
    ("Next locked atomic writes preserve original", NextTests.LockedWriteAsync),
    ("Next bridge revocation preserves host targets and recovers interruption", NextTests.BridgeAsync),
    ("Next migration requires preview and preserves IDs/source/credentials boundary", NextTests.MigrationAsync),
    ("Next enumeration failure refuses a forced stop", NextTests.EnumerationAsync),
    ("Next callbacks never guess among multiple, expired or future waiters", NextTests.CallbackAsync),
    ("Next callback diagnostics record receipt/stage/fault without URI", NextTests.CallbackDiagnosticsAsync),
    ("Next stop retries an unknown exit snapshot without implicit success", NextTests.ExitRaceAsync),
    ("agent envelope errors never imply readiness or expose secrets", AgentProbeTests.ClassifierAsync),
    ("isolation preparation fails closed and keeps unique scope ownership", TestIsolationPreparationAsync),
    ("isolation state rejects corrupt duplicate mappings", TestIsolationStateValidationAsync),
    ("prepared profiles route lifecycle through isolation", TestIsolationLifecycleRoutingAsync),
    ("mixed launch-all fails before starting any profile", TestMixedIsolationLaunchAllAsync),
    ("profile CRUD preserves stable ID and data", TestProfileCrudAsync),
    ("concurrent creates retain every profile", TestConcurrentCreatesAsync),
    ("corrupt and future config are rejected without overwrite", TestInvalidConfigAsync),
    ("isolation backend defaults to process-local and round-trips", TestIsolationBackendDefaultsAsync),
    ("unknown isolation backend is rejected", TestUnknownIsolationBackendAsync),
    ("process-local launches ask for the file token store", TestProcessLocalLaunchPlanAsync),
    ("process-local scopes need no external product", TestProcessLocalScopeAsync),
    ("launch wrapper keeps non-ASCII paths and escapes percent", TestLaunchWrapperContentAsync),
    ("deep-link callback reaches the profile that is waiting", TestDeepLinkRouterAsync),
    ("an unverified process-local scope fails closed", TestScopeVerificationFailsClosedAsync),
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
    ("unexpected pre-create failure auto-clears the retained barrier", TestUnexpectedPreCreateFailureBarrierAutoClearsAsync),
    ("foreign rootless launch barrier fails closed", TestForeignRootlessBarrierFailsClosedAsync),
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
    ("isolation setup is idempotent", TestIsolationSetupAsync),
    ("profiles resolve by name or unique prefix", TestProfileResolveAsync),
    ("scope attribution parses argv strictly and rejects look-alike paths", TestScopeAttributionStrictAsync),
    ("scope children require a plausible creation order", TestScopeChildStartGuardAsync),
    ("an unreadable process list fails closed everywhere", TestEnumerationFailureFailsClosedAsync),
    ("an unknown process count never reconciles profile state", TestUnknownProcessCountDoesNotReconcileStateAsync),
    ("an expired pending callback is dropped", TestPendingExpiryAsync),
    ("a failed launch clears the waiting grant", TestFailedLaunchClearsPendingAsync),
    ("stopping one profile keeps another profile's pending", TestStopKeepsForeignPendingAsync),
    ("a corrupt registration record blocks the launch before anything starts", TestCorruptRegistrationBlocksLaunchAsync),
    ("callback URIs are validated before any handoff", TestCallbackUriValidationAsync),
    ("the configured executable is digest-pinned everywhere it is used", TestExecutableDigestPinningAsync),
    ("ensure-registered refuses a tampered router command", TestEnsureRegisteredRejectsTamperedCommandAsync),
    ("a warm scope without Hub attestation is not trusted", TestWarmScopeRequiresAttestationAsync),
    ("isolated launch refuses an unpinned executable", TestIsolatedLaunchRequiresPinAsync),
    ("handoff resolves user-data from the profile id", TestHandoffUsesAuthoritativeUserDataAsync),
    ("stopping an idle scope clears the warm attestation", TestIdleStopClearsAttestationAsync),
};

// Groups identify the main class under test, not mutually exclusive coverage.
// These historical entry points directly exercise the shared production classes.
var sharedTests = new HashSet<Func<Task>> { TestProfileCrudAsync, TestConcurrentCreatesAsync, TestInvalidConfigAsync,
    TestIsolationBackendDefaultsAsync, TestUnknownIsolationBackendAsync, TestPathContainmentAsync,
    TestCommandLineAsync, TestShortcutAsync, TestProfileResolveAsync };
var tests = declaredTests.Select(test => new TestCase(test.Name,
    test.Run.Method.DeclaringType == typeof(NextTests) ? "Next" : sharedTests.Contains(test.Run) ? "Shared" : "Legacy",
    test.Run, test.Run == NextTests.DesktopLaunchAsync)).ToList();
tests.AddRange(BoundaryTests.Cases.Select(test => new TestCase(test.Name,
    test.Name.StartsWith("Next ", StringComparison.Ordinal) ? "Next" :
    test.Name.StartsWith("Runner ", StringComparison.Ordinal) ? "Runner" : "Shared", test.Run)));
tests.AddRange(NextProtocolTests.Cases.Select(test => new TestCase(test.Name, "Next", test.Run)));
tests.AddRange(NextCallbackTests.Cases.Select(test => new TestCase(test.Name, "Next", test.Run)));
tests.AddRange(AuditPersistenceTests.Cases.Select(test => new TestCase(test.Name, "Next", test.Run)));
tests.AddRange(AuditClassificationTests.Cases.Select(test => new TestCase(test.Name, "Next", test.Run)));
tests.AddRange(AuditProfileCreationTests.Cases.Select(test => new TestCase(test.Name, "Shared", test.Run)));
return await TestRunner.RunAsync(tests, args, Console.Out, Console.Error);

static async Task TestIsolationSetupAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var setup = new IsolationSetup(launcher, _ => backend);

    // Fresh profile: the whole chain is applied in one call.
    var id = Guid.NewGuid();
    var report = await setup.EnsureReadyAsync(id, IsolationBackendKind.ProcessLocal, allowFreshBox: false);
    True(report.Ready, "a fresh profile becomes ready in one call");
    Equal(IsolationStage.Ready, report.Stage);
    True(backend.Boxes.Contains(IsolatedProfileLauncher.BoxNameFor(id)), "preparation created the scope");

    // The same call a second time must apply nothing.
    var again = await setup.EnsureReadyAsync(id, IsolationBackendKind.ProcessLocal, allowFreshBox: false);
    True(again.Ready);
    True(again.Steps.All(step => !step.Applied), "a second run applies nothing");

    // A pre-existing unowned scope still requires an explicit acknowledgement.
    var other = Guid.NewGuid();
    backend.Boxes.Add(IsolatedProfileLauncher.BoxNameFor(other));
    await ThrowsAsync<HubException>(() => setup.EnsureReadyAsync(other, IsolationBackendKind.ProcessLocal, allowFreshBox: false));
    var claimed = await setup.EnsureReadyAsync(other, IsolationBackendKind.ProcessLocal, allowFreshBox: true);
    True(claimed.Ready, "an acknowledged fresh scope can be taken over");
}

static async Task TestProfileResolveAsync()
{
    using var fixture = new Fixture();
    var alpha = await fixture.Profiles.CreateAsync("Work Alpha", null, IsolationBackendKind.ProcessLocal);
    var beta = await fixture.Profiles.CreateAsync("Work Beta", null, IsolationBackendKind.ProcessLocal);
    var personal = await fixture.Profiles.CreateAsync("Personal", null, IsolationBackendKind.ProcessLocal);

    Equal(alpha.Id, (await fixture.Profiles.ResolveAsync(alpha.Id.ToString("D"))).Id);
    Equal(beta.Id, (await fixture.Profiles.ResolveAsync("work beta")).Id);
    Equal(personal.Id, (await fixture.Profiles.ResolveAsync("Pers")).Id);
    Equal(alpha.Id, (await fixture.Profiles.ResolveAsync(alpha.Id.ToString("D")[..12])).Id);
    await ThrowsAsync<HubException>(() => fixture.Profiles.ResolveAsync("missing"));
    await ThrowsAsync<HubException>(() => fixture.Profiles.ResolveAsync("Work"));
}

static async Task TestIsolationPreparationAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();

    backend.Boxes.Add("ExistingBox");
    await ThrowsAsync<HubException>(() => launcher.PrepareAsync(a, "ExistingBox"));
    var prepared = await launcher.PrepareAsync(a, "ExistingBox", claimExistingFreshBox: true);
    True(prepared.MappingCreated && !prepared.BoxCreated);
    await ThrowsAsync<HubException>(() => launcher.PrepareAsync(b, "ExistingBox", claimExistingFreshBox: true));

    var fresh = await launcher.PrepareAsync(b, "FreshBox");
    True(fresh.BoxCreated && fresh.MappingCreated, "an unknown scope is created and registered");
    var duplicate = await launcher.FindAsync(b);
    True(duplicate is not null, "the mapping is persisted");
}

static async Task TestScopeVerificationFailsClosedAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    // The fake never writes a language-server log, so a process-local launch has
    // no way to prove that it moved off the shared credential entry.
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend,
        scopeVerificationTimeout: TimeSpan.FromMilliseconds(300));
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        var registration = new ProfileRegistration(id, "Scoped", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        };
        document.Profiles.Add(registration);
        return (document, registration);
    });

    // A prepared scope is allowed to attempt a launch.
    var prepared = await launcher.PrepareAsync(id, null, claimExistingFreshBox: true);
    True(prepared.MappingCreated, "a prepared scope is allowed to launch");

    // Without the log evidence the instance is stopped instead of trusted.
    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(id));
    Equal(1, backend.LaunchCalls);
    True(backend.ListPids(IsolatedProfileLauncher.BoxNameFor(id)).Count == 0,
        "an unverified instance must be terminated, not left running");
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
        fixture.Repository, _ => new FakeIsolationBackend());
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

    // Names address profiles interactively, so a duplicate is refused on
    // create and on rename, case-insensitively.
    await ThrowsAsync<HubException>(() => fixture.Hub.CreateProfileAsync("pro a"));
    var second = await fixture.Hub.CreateProfileAsync("Second");
    await ThrowsAsync<HubException>(() => fixture.Hub.RenameProfileAsync(profile.Id, "SECOND"));

    var renamed = await fixture.Hub.RenameProfileAsync(profile.Id, "Builder");
    Equal(profile.Id, renamed.Id);
    var removed = await fixture.Hub.RemoveProfileAsync(profile.Id);
    Equal(profile.Id, removed.Id);
    True(File.Exists(Path.Combine(fixture.Paths.ProfileRoot(profile.Id), "keep.txt")), "remove must preserve profile data");

    // The freed name can be used again.
    var reuse = await fixture.Hub.CreateProfileAsync("Builder");
    Equal("Builder", reuse.Name);
    _ = second;
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

static async Task TestIsolationBackendDefaultsAsync()
{
    using var fixture = new Fixture();
    // New Profiles are process-local: no sandbox product has to be installed.
    var created = await fixture.Hub.CreateProfileAsync("A");
    Equal(IsolationBackendKind.ProcessLocal, created.Backend);

    // The backend field is additive, so a document written before it existed must
    // still load; a Profile with no field resolves to the process-local default.
    var legacy = "{\"schemaVersion\":1,\"profiles\":[{\"id\":\"" + created.Id +
        "\",\"name\":\"A\",\"workspacePath\":null,\"createdUtc\":\"2026-01-01T00:00:00+00:00\"," +
        "\"updatedUtc\":\"2026-01-01T00:00:00+00:00\"}]}";
    File.WriteAllText(fixture.Paths.ProfilesFile, legacy);
    var profiles = await fixture.Hub.ListProfilesAsync();
    Equal(1, profiles.Count);
    Equal(IsolationBackendKind.ProcessLocal, profiles[0].Backend);

    // A value recorded by an older build still loads and resolves to the same
    // process-local scope, whose launch is verified either way.
    Equal(IsolationBackendKind.Sandboxie, (await fixture.Hub.CreateProfileAsync("Legacy", null, IsolationBackendKind.Sandboxie)).Backend);
}

static async Task TestUnknownIsolationBackendAsync()
{
    using var fixture = new Fixture();
    await fixture.Hub.CreateProfileAsync("A");

    // Numeric values that are not defined members must fail closed, and the file
    // must be left exactly as it was found.
    var unknown = "{\"schemaVersion\":1,\"profiles\":[{\"id\":\"" + Guid.NewGuid() +
        "\",\"name\":\"A\",\"createdUtc\":\"2026-01-01T00:00:00+00:00\"," +
        "\"updatedUtc\":\"2026-01-01T00:00:00+00:00\",\"backend\":7}]}";
    File.WriteAllText(fixture.Paths.ProfilesFile, unknown);
    await ThrowsAsync<ConfigurationException>(() => fixture.Hub.ListProfilesAsync());
    Equal(unknown, File.ReadAllText(fixture.Paths.ProfilesFile));

    File.WriteAllText(fixture.Paths.SettingsFile, "{\"schemaVersion\":1,\"defaultBackend\":7}");
    await ThrowsAsync<ConfigurationException>(() => fixture.Repository.ReadSettingsAsync());
}

static Task TestProcessLocalLaunchPlanAsync()
{
    using var fixture = new Fixture();
    var driver = new AntigravityDriver(fixture.Paths, fixture.Repository);
    var installation = new AntigravityInstallation(fixture.Executable, "2.12.2", true, "test");
    var id = Guid.NewGuid();

    // The sandbox plan must not gain anything: that path already owns a private
    // credential scope, so its launch stays byte-for-byte what it was.
    var sandboxed = driver.BuildLaunchPlan(installation, id, IsolationBackendKind.Sandboxie);
    True(!sandboxed.Environment.ContainsKey("WSL_DISTRO_NAME"), "sandbox launches must not carry the process-local detector");
    Equal(1, sandboxed.Arguments.Count);

    var local = driver.BuildLaunchPlan(installation, id, IsolationBackendKind.ProcessLocal);
    True(local.Environment.ContainsKey("WSL_DISTRO_NAME"), "process-local launches ask the language server for its file store");
    True(local.Environment["WSL_DISTRO_NAME"].StartsWith("AGPH-", StringComparison.Ordinal));
    Equal(local.Environment["USERPROFILE"], local.Environment["HOME"]);
    True(local.Environment["USERPROFILE"].EndsWith("home", StringComparison.OrdinalIgnoreCase));
    // The process home already decides where the token file lands, so adding
    // --gemini_dir would be a second mechanism for the same guarantee.
    Equal(1, local.Arguments.Count);
    return Task.CompletedTask;
}

static Task TestProcessLocalScopeAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var scope = IsolationBackends.ScopeNameFor(id);
    True(scope.StartsWith("AGPH_", StringComparison.Ordinal));

    var backend = IsolationBackends.TryCreate(fixture.Paths, IsolationBackendKind.ProcessLocal);
    True(backend is ProcessLocalBackend, "the process-local backend resolves without any external product");
    True(!backend!.BoxExists(scope), "a scope does not exist before it is prepared");
    Equal(0, backend.ListPids(scope).Count);
    backend.CreateBox(scope);
    True(backend.BoxExists(scope), "preparing a scope only needs its managed layout");

    // Unknown kinds have no backend, and malformed scope names stay
    // configuration errors rather than a silent fallback.
    Equal(null, IsolationBackends.TryCreate(fixture.Paths, (IsolationBackendKind)9));
    Throws<ConfigurationException>(() => { _ = new ProcessLocalBackend(fixture.Paths).BoxExists("not-a-scope"); });
    return Task.CompletedTask;
}

static Task TestLaunchWrapperContentAsync()
{
    // A Chinese Windows user name makes the managed home non-ASCII; ASCII
    // encoding would silently corrupt it into '?'.
    var home = "C:\\Users\\张三\\AppData\\Local\\AntigravityProfileHub\\profiles\\abc\\home";
    var content = ProcessLocalBackend.BuildLaunchWrapperContent(
        new Dictionary<string, string>
        {
            ["USERPROFILE"] = home,
            ["ELECTRON_RUN_AS_NODE"] = "1",
            ["TEMP"] = "C:\\Temp\\100% coverage"
        },
        "C:\\Program Files\\Antigravity\\Antigravity.exe",
        ["--user-data-dir=C:\\Data\\100%\\user-data"]);

    True(content.Contains(home, StringComparison.Ordinal), "non-ASCII paths survive verbatim");
    True(content.Contains("100%% coverage", StringComparison.Ordinal), "percent signs are doubled for cmd");
    True(content.Contains("--user-data-dir=C:\\Data\\100%%\\user-data", StringComparison.Ordinal), "argument percent signs are doubled");
    True(!content.Contains("ELECTRON_RUN_AS_NODE=1", StringComparison.Ordinal), "the node-hijack variable is never set");
    True(content.Contains("set \"ELECTRON_RUN_AS_NODE=\"", StringComparison.Ordinal), "the node-hijack variable is cleared");
    var lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    Equal("@echo off", lines[0]);
    True(lines[1].StartsWith("@chcp 65001", StringComparison.Ordinal), "cmd switches to UTF-8 before any non-ASCII line");
    True(lines[^1].StartsWith("start ", StringComparison.Ordinal), "the wrapper ends by starting the desktop");
    True(!content.StartsWith('\ufeff'), "no BOM; cmd misreads it");
    return Task.CompletedTask;
}

static async Task TestDeepLinkRouterAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var userData = fixture.Paths.UserData(id);

    // Nothing is waiting before a launch, and the arguments are exactly what the
    // desktop needs to hand the URI to the instance that already owns that
    // user-data directory.
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
    var arguments = DeepLinkRouter.BuildRelaunchArguments("cli.exe", userData, "antigravity://oauth/callback?code=x");
    Equal(3, arguments.Count);
    Equal("cli.exe", arguments[0]);
    Equal($"--user-data-dir={userData}", arguments[1]);
    Equal("antigravity://oauth/callback?code=x", arguments[2]);

    await DeepLinkRouter.WritePendingAsync(fixture.Paths, id, userData);
    var pending = await DeepLinkRouter.ReadPendingAsync(fixture.Paths);
    True(pending is not null && pending.ProfileId == id && pending.UserDataPath == userData,
        "the waiting profile must survive a round-trip");

    // The handoff must relaunch the vendor desktop, never the Hub CLI: the CLI
    // cannot receive OAuth URIs and would drop the callback with a usage error.
    await fixture.Repository.WriteSettingsAsync(new HubSettings
    {
        AntigravityExecutablePath = fixture.Executable,
        ExecutableSha256 = await ExecutableAttestation.ComputeSha256Async(fixture.Executable)
    });
    var handoff = DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x");
    True(handoff is not null, "a waiting profile produces a handoff");
    Equal(Path.GetFullPath(fixture.Executable), handoff!.FileName);
    Equal(2, handoff.ArgumentList.Count);
    Equal($"--user-data-dir={userData}", handoff.ArgumentList[0]);
    Equal("antigravity://oauth/callback?code=x", handoff.ArgumentList[1]);

    DeepLinkRouter.ClearPending(fixture.Paths);
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
    Equal(null, DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x"));
}

static Task TestLaunchPlanAsync()
{
    using var fixture = new Fixture();
    var driver = new AntigravityDriver(fixture.Paths, fixture.Repository);
    var plan = driver.BuildLaunchPlan(new AntigravityInstallation(fixture.Executable, "2.12.2", true, "test"), Guid.NewGuid(), IsolationBackendKind.Sandboxie);
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
        var pin = await ExecutableAttestation.ComputeSha256Async(executable);
        await repositoryA.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = executable, ExecutableSha256 = pin });
        await repositoryB.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = executable, ExecutableSha256 = pin });
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

static async Task TestUnexpectedPreCreateFailureBarrierAutoClearsAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    fixture.World.FailBeforeStartUnexpected = true;
    await ThrowsAsync<InvalidOperationException>(() => fixture.Hub.LaunchAsync(profile.Id));
    True(File.Exists(fixture.Paths.LaunchBarrierFile));
    fixture.World.FailBeforeStartUnexpected = false;
    True((await fixture.Hub.LaunchAsync(profile.Id)).StartedNewProcess);
    True(!File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(1, fixture.World.RootCount);
}

static async Task TestForeignRootlessBarrierFailsClosedAsync()
{
    using var fixture = new Fixture();
    var profile = await fixture.Hub.CreateProfileAsync("A");
    var barrier = $$"""{"schemaVersion":{{SchemaVersions.Current}},"attemptId":"{{Guid.NewGuid()}}","profileId":"{{profile.Id}}","originDataRoot":"C:\\elsewhere\\hub","executablePath":"{{fixture.Executable.Replace("\\", "\\\\")}}","userDataPath":"{{fixture.Paths.UserData(profile.Id).Replace("\\", "\\\\")}}","createdUtc":"{{DateTimeOffset.UtcNow:O}}","root":null}""";
    File.WriteAllText(fixture.Paths.LaunchBarrierFile, barrier);
    await ThrowsAsync<LaunchStateUncertainException>(() => fixture.Hub.LaunchAsync(profile.Id));
    True(File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(0, fixture.World.LaunchCount);
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

    await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => fixture.Hub.LaunchAsync(a.Id), "configuration_write_failed");
    True(File.Exists(fixture.Paths.LaunchBarrierFile));
    Equal(1, fixture.World.RootCount);
    await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => fixture.Hub.LaunchAsync(b.Id), "configuration_write_failed");
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
    Throws<HubException>(() => driver.BuildLaunchPlan(new AntigravityInstallation(fixture.Executable, "99.0.0", false, "Unverified version"), id, IsolationBackendKind.ProcessLocal));
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
    True(File.Exists(path) && Path.GetDirectoryName(path) == directory, "shortcut must be created in the Unicode destination");
    // WScript.Shell cannot reopen a Unicode .lnk path on the English hosted runner.
    // Inspect an exact copy at an ASCII path while retaining the original for no-overwrite checks.
    var inspectionPath = Path.Combine(fixture.Root, "inspect.lnk");
    File.Copy(path, inspectionPath);
    var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
    object? link = null;
    try
    {
        link = ((dynamic)shell!).CreateShortcut(inspectionPath);
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
static async Task TestScopeAttributionStrictAsync()
{
    // A space-free root keeps every negative case in the argv domain where a
    // single token really is the whole value; quoted handling has its own case.
    var expected = @"D:\root\profiles\ABCDEF01\user-data";
    var now = DateTimeOffset.UtcNow;
    ProcessLocalBackend.ProcessRow Row(int pid, int ppid, string name, string? commandLine, int startOffsetSeconds = 0) =>
        new(pid, ppid, name, commandLine, now.AddSeconds(startOffsetSeconds));

    // The quoted and bare argv forms of the exact path both match.
    var exactQuoted = new[] { Row(10, 1, "Antigravity.exe", $"\"C:\\apps\\Antigravity.exe\" --user-data-dir=\"{expected}\" --flag") };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(exactQuoted, expected).Count);
    var exactBare = new[] { Row(10, 1, "Antigravity.exe", $"C:\\apps\\Antigravity.exe --user-data-dir={expected}") };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(exactBare, expected).Count);

    // A quoted path containing spaces stays one argv element and still matches.
    var spaced = @"D:\data root\profiles\ABCDEF01\user-data";
    var quotedSpaces = new[] { Row(17, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{spaced}\"") };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(quotedSpaces, spaced).Count);

    // A path that merely embeds the expected one as a prefix or infix must not match:
    // the old substring comparison attributed such look-alike roots to this scope.
    var lookAlikes = new[]
    {
        Row(11, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir={expected}-old"),
        Row(12, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected}\\extra\""),
        Row(13, 1, "Antigravity.exe", "Antigravity.exe --user-data-dir=--user-data-dir="),
        Row(14, 1, "language_server.exe", $"language_server --user-data-dir={expected}")
    };
    Equal(0, ProcessLocalBackend.SelectScopeProcesses(lookAlikes, expected).Count);

    // A command line with two --user-data-dir values is ambiguous and matches nothing.
    var ambiguous = new[] { Row(15, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir={expected} --user-data-dir=C:\\other") };
    Equal(0, ProcessLocalBackend.SelectScopeProcesses(ambiguous, expected).Count);

    // Attribution is a full-path comparison: case differences still match.
    var sloppyCase = new[] { Row(16, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected.ToLowerInvariant()}\"") };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(sloppyCase, expected).Count);
}

static async Task TestScopeChildStartGuardAsync()
{
    var expected = @"D:\root\profiles\ABCDEF01\user-data";
    var now = DateTimeOffset.UtcNow;
    ProcessLocalBackend.ProcessRow Row(int pid, int ppid, string name, string? commandLine, int startOffsetSeconds) =>
        new(pid, ppid, name, commandLine, now.AddSeconds(startOffsetSeconds));

    // A language server child created after the desktop root joins the scope.
    var family = new[]
    {
        Row(20, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected}\"", 0),
        Row(21, 20, "language_server.exe", "language_server --pipe", 5)
    };
    Equal(2, ProcessLocalBackend.SelectScopeProcesses(family, expected).Count);

    // A reused parent PID must not pull an unrelated older process into the
    // scope: a child created before its in-scope parent is left out.
    var reusedParent = new[]
    {
        Row(30, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected}\"", 0),
        Row(31, 30, "language_server.exe", "language_server --pipe", -600)
    };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(reusedParent, expected).Count);

    // A child whose creation time cannot be parsed stays outside the scope.
    var unverifiableChild = new[]
    {
        Row(40, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected}\"", 0),
        new ProcessLocalBackend.ProcessRow(41, 40, "language_server.exe", "language_server --pipe", DateTimeOffset.MinValue)
    };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(unverifiableChild, expected).Count);

    // Grandchildren inherit through the same guard: after an older child is
    // rejected, its own children are unreachable from the scope.
    var blockedChain = new[]
    {
        Row(50, 1, "Antigravity.exe", $"Antigravity.exe --user-data-dir=\"{expected}\"", 0),
        Row(51, 50, "language_server.exe", "language_server --pipe", -600),
        Row(52, 51, "language_server.exe", "language_server --pipe", 9)
    };
    Equal(1, ProcessLocalBackend.SelectScopeProcesses(blockedChain, expected).Count);
}

static async Task TestEnumerationFailureFailsClosedAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend { FailEnumeration = true };
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await launcher.PrepareAsync(id);

    // Status reports an unknown count instead of an idle scope.
    var status = await launcher.GetStatusAsync(id);
    True(status is not null && status.ProcessCount == -1, "an unreadable process list must be reported as unknown, not empty");

    // Stop refuses to claim success, and launch refuses to start blindly.
    await ThrowsAsync<HubException>(() => launcher.StopAsync(id));
    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(id));
    True(backend.LaunchCalls == 0, "a launch that cannot see the scope must not start anything");
}

static async Task TestUnknownProcessCountDoesNotReconcileStateAsync()
{
    var isolation = new FakeProfileIsolation { ForcedProcessCount = -1 };
    using var fixture = new Fixture(isolation: isolation);
    var launched = await fixture.Profiles.CreateAsync("Launched", null, IsolationBackendKind.ProcessLocal);
    var idle = await fixture.Profiles.CreateAsync("Idle", null, IsolationBackendKind.ProcessLocal);

    // The launched profile is Running in the supervisor's view; the idle one is
    // Stopped. Both now gain an isolation scope whose process count is unknown.
    await fixture.Hub.LaunchAsync(launched.Id);
    await isolation.PrepareAsync(launched.Id);
    await isolation.PrepareAsync(idle.Id);

    var runningStatus = await fixture.Hub.GetStatusAsync(launched.Id);
    True(runningStatus.State == RuntimeState.Running, "an unknown process count must not flip Running to Unknown");
    var idleStatus = await fixture.Hub.GetStatusAsync(idle.Id);
    True(idleStatus.State == RuntimeState.Stopped, "an unknown process count must not flip Stopped to Running");
    True((idleStatus.Detail ?? "").Contains("liveness is unknown", StringComparison.Ordinal),
        "the status detail must say liveness is unknown");
}

static async Task TestPendingExpiryAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var userData = fixture.Paths.UserData(id);
    await fixture.Repository.WriteSettingsAsync(new HubSettings
    {
        AntigravityExecutablePath = fixture.Executable,
        ExecutableSha256 = await ExecutableAttestation.ComputeSha256Async(fixture.Executable)
    });

    await DeepLinkRouter.WritePendingAsync(fixture.Paths, id, userData);
    True(DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x") is not null,
        "a fresh pending is routable");

    // A pending from a year ago must not keep handing URIs to this profile:
    // the sign-in it waited for either completed or was abandoned.
    var path = DeepLinkRouter.PendingPath(fixture.Paths);
    var aged = new PendingCallback { ProfileId = id, UserDataPath = userData, CreatedUtc = DateTimeOffset.UtcNow.AddYears(-1) };
    File.WriteAllText(path, JsonSerializer.Serialize(aged, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
    True(!File.Exists(path), "an expired pending is deleted, not left behind");
    Equal(null, DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x"));

    // A record with no creation time is treated as stale as well.
    var timeless = new PendingCallback { ProfileId = id, UserDataPath = userData };
    File.WriteAllText(path, JsonSerializer.Serialize(timeless, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
}

static async Task TestFailedLaunchClearsPendingAsync()
{
    // Discovery failure: nothing was started, so nothing may stay waiting.
    var backend = new FakeIsolationBackend();
    using var fixture = new Fixture();
    var driver = new FakeDriver(fixture.Paths, fixture.Executable) { FailDiscovery = true };
    var launcher = new IsolatedProfileLauncher(fixture.Paths, driver, fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        document.Profiles.Add(new ProfileRegistration(id, "Scoped", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        });
        return (document, document.Profiles[0]);
    });
    await launcher.PrepareAsync(id);

    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(id));
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
    True(!File.Exists(DeepLinkRouter.PendingPath(fixture.Paths)), "a failed discovery must not leave a waiting grant");

    // Verification failure: the instance is terminated, and the grant goes with it.
    var strictBackend = new FakeIsolationBackend();
    var strictLauncher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => strictBackend, scopeVerificationTimeout: TimeSpan.FromMilliseconds(300));
    await ThrowsAsync<HubException>(() => strictLauncher.LaunchAsync(id));
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
}

static async Task TestStopKeepsForeignPendingAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var stopping = Guid.NewGuid();
    var waiting = Guid.NewGuid();
    foreach (var id in new[] { stopping, waiting })
        await fixture.Repository.UpdateProfilesAsync(document =>
        {
            document.Profiles.Add(new ProfileRegistration(id, $"Profile {id:D}", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            {
                Backend = IsolationBackendKind.ProcessLocal
            });
            return (document, document.Profiles[0]);
        });
    await launcher.PrepareAsync(stopping);
    await launcher.PrepareAsync(waiting);

    // The waiting profile has a live grant; stopping the other profile must
    // not destroy it.
    await DeepLinkRouter.WritePendingAsync(fixture.Paths, waiting, fixture.Paths.UserData(waiting));
    await launcher.StopAsync(stopping);
    var surviving = await DeepLinkRouter.ReadPendingAsync(fixture.Paths);
    True(surviving is not null && surviving.ProfileId == waiting, "stopping another profile must keep the waiting grant");

    // Stopping the waiting profile itself does clear its own grant.
    await launcher.StopAsync(waiting);
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
}

static async Task TestCorruptRegistrationBlocksLaunchAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        document.Profiles.Add(new ProfileRegistration(id, "Scoped", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        });
        return (document, document.Profiles[0]);
    });
    await launcher.PrepareAsync(id);

    // A corrupt deep-link registration record used to abort the launch after
    // the instance had already started, skipping the token-store gate. It must
    // fail before the wrapper runs, and it must not leave a waiting grant.
    var registrationPath = Path.Combine(fixture.Paths.DataRoot, "runtime", "deeplink-registration.json");
    Directory.CreateDirectory(Path.GetDirectoryName(registrationPath)!);
    File.WriteAllText(registrationPath, "{ not json");

    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(id));
    True(backend.LaunchCalls == 0, "a corrupt registration record must fail before the desktop starts");
    True(!File.Exists(DeepLinkRouter.PendingPath(fixture.Paths)), "the failed launch must not leave a waiting grant");
}

static async Task TestEnsureRegisteredRejectsTamperedCommandAsync()
{
    using var fixture = new Fixture();
    if (!OperatingSystem.IsWindows())
    {
        // Canonicalize is pure enough to test without the registry write path.
        var cli = Path.Combine(fixture.Root, "cli", "AGProfileHub.Cli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllBytes(cli, []);
        var rebuilt = DeepLinkRouter.CanonicalizeRouterCommand(
            fixture.Paths, $"\"{cli}\" --data-root \"C:\\evil\" callback \"%1\"");
        True(rebuilt.Contains(fixture.Paths.DataRoot, StringComparison.OrdinalIgnoreCase),
            "the data root is always this Hub's");
        True(!rebuilt.Contains("C:\\evil", StringComparison.OrdinalIgnoreCase));
        await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.CanonicalizeRouterCommand(
            fixture.Paths, "\"C:\\Windows\\System32\\cmd.exe\" /c evil")));
        return;
    }

    var cliPath = Path.Combine(fixture.Root, "cli", "AGProfileHub.Cli.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
    File.WriteAllBytes(cliPath, []);
    var registrationPath = Path.Combine(fixture.Paths.DataRoot, "runtime", "deeplink-registration.json");
    Directory.CreateDirectory(Path.GetDirectoryName(registrationPath)!);
    File.WriteAllText(registrationPath, JsonSerializer.Serialize(
        new DeepLinkRegistration
        {
            PreviousCommand = null,
            RouterCommand = "\"C:\\Windows\\System32\\cmd.exe\" /c start evil \"%1\"",
            RegisteredUtc = DateTimeOffset.UtcNow
        },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.EnsureRegistered(fixture.Paths)));
}

static async Task TestWarmScopeRequiresAttestationAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        var registration = new ProfileRegistration(id, "Warm", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        };
        document.Profiles.Add(registration);
        return (document, registration);
    });
    await launcher.PrepareAsync(id);
    backend.LaunchDetached(IsolatedProfileLauncher.BoxNameFor(id), fixture.Executable, [], new Dictionary<string, string>());

    // Live processes without a Hub-written attestation are not a trusted session.
    await ThrowsAsync<HubException>(() => launcher.LaunchAsync(id));

    fixture.Paths.EnsureProfileLayout(id);
    await File.WriteAllTextAsync(
        Path.Combine(fixture.Paths.ProfileRoot(id), "scope-attestation.json"),
        JsonSerializer.Serialize(
            new ScopeAttestation { ProfileId = id, VerifiedUtc = DateTimeOffset.UtcNow },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    var warm = await launcher.LaunchAsync(id);
    True(!warm.StartedNewProcess, "an attested warm scope is reported as already running");
}

static async Task TestIsolatedLaunchRequiresPinAsync()
{
    using var fixture = new Fixture();
    await fixture.Repository.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = fixture.Executable });
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        var registration = new ProfileRegistration(id, "Unpinned", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        };
        document.Profiles.Add(registration);
        return (document, registration);
    });
    await launcher.PrepareAsync(id);
    var ex = await ThrowsCaptureAsync<HubException>(() => launcher.LaunchAsync(id));
    Equal("executable_not_pinned", ex.Code);
    Equal(0, backend.LaunchCalls);
}

static async Task TestHandoffUsesAuthoritativeUserDataAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid();
    var expected = fixture.Paths.UserData(id);
    var lying = Path.Combine(fixture.Root, "not-the-profile", "user-data");
    Directory.CreateDirectory(lying);
    await DeepLinkRouter.WritePendingAsync(fixture.Paths, id, lying);
    await fixture.Repository.WriteSettingsAsync(new HubSettings
    {
        AntigravityExecutablePath = fixture.Executable,
        ExecutableSha256 = await ExecutableAttestation.ComputeSha256Async(fixture.Executable)
    });
    var handoff = DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x");
    True(handoff is not null);
    Equal($"--user-data-dir={expected}", handoff!.ArgumentList[0]);
}

static async Task<HubException> ThrowsCaptureAsync<T>(Func<Task> action) where T : HubException
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task TestIdleStopClearsAttestationAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend);
    var id = Guid.NewGuid();
    await fixture.Repository.UpdateProfilesAsync(document =>
    {
        var registration = new ProfileRegistration(id, "Idle", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Backend = IsolationBackendKind.ProcessLocal
        };
        document.Profiles.Add(registration);
        return (document, registration);
    });
    await launcher.PrepareAsync(id);
    fixture.Paths.EnsureProfileLayout(id);
    var attestation = Path.Combine(fixture.Paths.ProfileRoot(id), "scope-attestation.json");
    await File.WriteAllTextAsync(attestation, JsonSerializer.Serialize(
        new ScopeAttestation { ProfileId = id, VerifiedUtc = DateTimeOffset.UtcNow },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    Equal(false, await launcher.StopAsync(id));
    True(!File.Exists(attestation), "an idle stop must drop the previous cold-start attestation");
}

static async Task TestCallbackUriValidationAsync()
{
    // The OS handler quotes %1 so a webpage cannot inject a second argument;
    // the router still refuses anything that is not a plausible vendor URI.
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.ValidateCallbackUri("--user-data-dir=C:\\evil")));
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.ValidateCallbackUri("https://example.com/oauth")));
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.ValidateCallbackUri("antigravity://oauth?code=x\n--gpu-launcher=evil")));
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.ValidateCallbackUri("")));
    await ThrowsAsync<HubException>(() => Task.Run(() => DeepLinkRouter.ValidateCallbackUri(new string('a', DeepLinkRouter.MaxCallbackUriLength + 1))));

    // Boundary: exactly the cap is accepted, and the scheme is case-insensitive.
    DeepLinkRouter.ValidateCallbackUri(new string('a', DeepLinkRouter.MaxCallbackUriLength - "antigravity://".Length).Insert(0, "ANTIGRAVITY://"));
}

static async Task TestExecutableDigestPinningAsync()
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "resources", "bin"));
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "resources", "app.asar"), "mock package");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "resources", "bin", "language_server.exe"), "mock server");
    var driver = new AntigravityDriver(fixture.Paths, fixture.Repository);

    // A legacy configuration without a pin keeps working unchanged.
    await fixture.Repository.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = fixture.Executable });
    _ = await driver.DiscoverAsync();

    // Configuring the executable records its digest.
    var settings = await fixture.Hub.ConfigureExecutableAsync(fixture.Executable);
    var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.Executable))).ToLowerInvariant();
    Equal(expected, settings.ExecutableSha256);
    _ = await driver.DiscoverAsync();

    // A silently swapped binary is refused by discovery...
    await File.WriteAllTextAsync(fixture.Executable, "swapped payload");
    await ThrowsAsync<HubException>(() => driver.DiscoverAsync());

    // ...and by the deep-link handoff, which would otherwise relaunch it for
    // every webpage-triggered antigravity:// callback.
    var id = Guid.NewGuid();
    await DeepLinkRouter.WritePendingAsync(fixture.Paths, id, fixture.Paths.UserData(id));
    await ThrowsAsync<HubException>(() => Task.Run(() =>
        DeepLinkRouter.BuildHandoff(fixture.Paths, "antigravity://oauth/callback?code=x")));
    DeepLinkRouter.ClearPending(fixture.Paths);
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

internal sealed record TestCase(string Name, string Group, Func<Task> Run, bool System = false);

internal static class TestRunner
{
    internal static async Task<int> RunAsync(IReadOnlyList<TestCase> tests, string[] args, TextWriter output, TextWriter error)
    {
        var includeSystem = false; string? filter = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--include-system" && !includeSystem) includeSystem = true;
            else if (args[i] == "--filter" && filter is null && i + 1 < args.Length &&
                !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) filter = args[++i];
            else
            {
                await error.WriteLineAsync("usage: AGProfileHub.Tests [--include-system] [--filter <substring>]");
                return 2;
            }
        }
        if (filter is not null && !tests.Any(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            await error.WriteLineAsync("usage: --filter did not match any test case.");
            return 2;
        }
        var groups = tests.Select(test => test.Group).Distinct().ToDictionary(group => group, _ => (Passed: 0, Failed: 0, Skipped: 0));
        var failures = 0; var executed = 0; var skipped = 0;
        foreach (var test in tests)
        {
            var group = groups[test.Group];
            var reason = filter is not null && !test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ? "filter" :
                test.System && !includeSystem ? "requires --include-system" : null;
            if (reason is not null)
            {
                skipped++; groups[test.Group] = (group.Passed, group.Failed, group.Skipped + 1);
                await output.WriteLineAsync($"SKIP  [{test.Group}] {test.Name} ({reason})");
                continue;
            }
            executed++;
            try
            {
                await test.Run(); groups[test.Group] = (group.Passed + 1, group.Failed, group.Skipped);
                await output.WriteLineAsync($"PASS  [{test.Group}] {test.Name}");
            }
            catch (Exception ex)
            {
                failures++; groups[test.Group] = (group.Passed, group.Failed + 1, group.Skipped);
                await error.WriteLineAsync($"FAIL  [{test.Group}] {test.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        foreach (var (name, group) in groups)
            await output.WriteLineAsync($"GROUP {name}: {group.Passed} passed, {group.Failed} failed, {group.Skipped} skipped.");
        await output.WriteLineAsync($"{executed - failures}/{executed} tests passed; {skipped} skipped.");
        return failures == 0 ? 0 : 1;
    }
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
        var sha256 = ExecutableAttestation.ComputeSha256Async(Executable).GetAwaiter().GetResult();
        Repository.WriteSettingsAsync(new HubSettings { AntigravityExecutablePath = Executable, ExecutableSha256 = sha256 }).GetAwaiter().GetResult();
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
    /// <summary>Makes DiscoverAsync fail the way a missing vendor executable does.</summary>
    public bool FailDiscovery { get; set; }

    public Task<AntigravityInstallation> DiscoverAsync(CancellationToken cancellationToken = default) =>
        FailDiscovery
            ? throw new HubException("Antigravity Desktop was not found (test).", "executable_not_found")
            : Task.FromResult(new AntigravityInstallation(executable, "2.12.2", true, "test"));
    public LaunchPlan BuildLaunchPlan(AntigravityInstallation installation, Guid profileId,
        IsolationBackendKind backend)
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

    /// <summary>Overrides the reported scope process count; -1 simulates an unreadable process list.</summary>
    public int? ForcedProcessCount { get; set; }

    public Task<IsolationBoxRecord?> FindAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_boxes.GetValueOrDefault(profileId));

    public Task<IsolationPrepareResult> PrepareAsync(Guid profileId, string? explicitBoxName = null,
        bool claimExistingFreshBox = false, CancellationToken cancellationToken = default)
    {
        var created = !_boxes.TryGetValue(profileId, out var record);
        record ??= new IsolationBoxRecord(profileId, explicitBoxName ?? IsolatedProfileLauncher.BoxNameFor(profileId), DateTimeOffset.UtcNow);
        _boxes[profileId] = record;
        return Task.FromResult(new IsolationPrepareResult(profileId, record.BoxName, created, created));
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
        return Task.FromResult<IsolationRuntimeStatus?>(new IsolationRuntimeStatus(profileId, record.BoxName, true, true,
            ForcedProcessCount ?? (_running.Contains(profileId) ? 1 : 0)));
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

}

sealed class FakeIsolationBackend : IIsolationBackend
{
    private readonly Dictionary<string, List<int>> _pids = new(StringComparer.OrdinalIgnoreCase);
    private int _nextPid = 20000;
    public HashSet<string> Boxes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int LaunchCalls { get; private set; }

    /// <summary>Makes ListPids fail the way an unreadable WMI enumeration does.</summary>
    public bool FailEnumeration { get; set; }

    public bool BoxExists(string box) => Boxes.Contains(box);
    public void CreateBox(string box) => Boxes.Add(box);
    public IReadOnlyList<int> ListPids(string box)
    {
        if (FailEnumeration) throw new HubException("The process list could not be read (test).", "process_enumeration_unavailable");
        return _pids.GetValueOrDefault(box, []).ToList();
    }
    public void Terminate(string box) => _pids.Remove(box);
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
    public bool FailBeforeStartUnexpected { get; set; }
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
            if (FailBeforeStartUnexpected) throw new InvalidOperationException("fake unexpected pre-create failure");
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
