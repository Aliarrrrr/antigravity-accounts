using AGProfileHub.Core;

// hub.ps1 may pass --include-system to the test runner. This suite has no
// system tests, so the flag is parsed and ignored to keep the interface stable.
_ = Array.Exists(args, a => a == "--include-system");

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
    ("isolation backend defaults to Sandboxie and round-trips", TestIsolationBackendDefaultsAsync),
    ("unknown isolation backend is rejected", TestUnknownIsolationBackendAsync),
    ("process-local launches ask for the file token store", TestProcessLocalLaunchPlanAsync),
    ("process-local scopes need no external product", TestProcessLocalScopeAsync),
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
    ("elevation helper resolves repo SDK muxer over older runtimes", TestElevationMuxerResolution),
    ("elevation helper uses the current host when it is a muxer", TestElevationMuxerUsesHostPath),
    ("elevation helper keeps self-contained launches direct", TestElevationMuxerSkipsSelfContained),
    ("elevation helper finds no muxer without a .NET 10 runtime", TestElevationMuxerNotFound),
    ("credential shim requires the verified sandbox box", TestSandboxProcessContext),
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
    ("sandboxie ini round-trips bare and indexed keys", TestSandboxieIniAsync),
    ("elevated helper writes direct-write rules and reports failures", TestElevationHelperAsync),
    ("isolation setup is idempotent and honours legacy direct-write rules", TestIsolationSetupAsync),
    ("profiles resolve by name or unique prefix", TestProfileResolveAsync),
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
    if (SandboxProcessContext.CurrentBox() is null)
        Throws<HubException>(() => AntigravityKeyring.EnsureIsolation(forceFresh: true));
    else
        Throws<HubException>(() => AntigravityKeyring.EnsureIsolation(forceFresh: true, expectedBox: "AGPH_NotTheRunningBox"));
    await Task.CompletedTask;
}

static async Task TestSandboxieIniAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "agph-ini-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var utf16 = Path.Combine(root, "utf16.ini");
        var body = System.Text.Encoding.Unicode.GetBytes(
            "[GlobalSettings]\r\nFileRootPath=\\??\\%SystemDrive%\\Sandbox\r\n" +
            "[AGPH_Test]\r\nEnabled=y\r\nOpenFilePath=C:\\One\r\nOpenFilePath.1=C:\\Two\r\nConfigLevel=10\r\n");
        File.WriteAllBytes(utf16, new byte[] { 0xFF, 0xFE }.Concat(body).ToArray());

        var file = SandboxieIniFile.Load(utf16);
        var values = file.GetValues("AGPH_Test", "OpenFilePath");
        Equal(2, values.Count);
        True(values.Contains("C:\\One") && values.Contains("C:\\Two"), "legacy indexed keys are still read");
        True(file.SectionExists("AGPH_Test"), "the box section is found");
        True(!file.SectionExists("AGPH_Other"), "unrelated sections are not confused");

        file.ReplaceValues("AGPH_Test", "OpenFilePath", new[] { "C:\\Three", "C:\\Four" });
        file.Save();

        var reloaded = SandboxieIniFile.Load(utf16);
        var after = reloaded.GetValues("AGPH_Test", "OpenFilePath");
        Equal(2, after.Count);
        True(after.Contains("C:\\Three") && after.Contains("C:\\Four"), "saved values are read back");
        var text = File.ReadAllText(utf16);
        True(text.Contains("OpenFilePath=C:\\Three"), "writes use the bare key the driver honours");
        True(!text.Contains("OpenFilePath.1"), "indexed keys are never written back");
        True(text.Contains("ConfigLevel=10"), "unrelated settings survive");
        True(text.Contains("[GlobalSettings]"), "other sections survive");
        True(file.LastBackupPath is not null && File.Exists(file.LastBackupPath), "a backup is kept");

        var utf8 = Path.Combine(root, "utf8.ini");
        File.WriteAllText(utf8, "[AGPH_U8]\nOpenFilePath=D:/Alpha\nEnabled=y\n");
        var plain = SandboxieIniFile.Load(utf8);
        Equal(1, plain.GetValues("AGPH_U8", "OpenFilePath").Count);
        plain.ReplaceValues("AGPH_U8", "OpenFilePath", new[] { "D:/Beta" });
        plain.Save();
        var plainText = File.ReadAllText(utf8);
        True(plainText.Contains("D:/Beta"), "utf-8 without BOM is written back");
        True(!plainText.Contains("D:/Alpha"), "replaced values are removed");
        True(!plainText.Contains("OpenFilePath.1"), "utf-8 writes stay bare");

        Throws<HubException>(() => SandboxieIniFile.Load(utf8).ReplaceValues("AGPH_Missing", "OpenFilePath", new[] { "X" }));
        Throws<HubException>(() => SandboxieIniFile.Load(Path.Combine(root, "absent.ini")));
        True(SandboxieIniFile.TryLoad(Path.Combine(root, "absent.ini")) is null, "try-load returns null instead of throwing");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
    await Task.CompletedTask;
}

static async Task TestElevationHelperAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "agph-elev-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var request = new ElevationRequest(AdminOperation.WriteThrough, "AGPH_Test", new[] { "C:\\One", "C:\\Two" });
        var encoded = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, HubJsonContext.Default.ElevationRequest);
        var restored = System.Text.Json.JsonSerializer.Deserialize(encoded, HubJsonContext.Default.ElevationRequest)!;
        Equal(AdminOperation.WriteThrough, restored.Operation);
        Equal("AGPH_Test", restored.BoxName);
        Equal(2, restored.Paths.Count);

        var ini = Path.Combine(root, "Sandboxie.ini");
        File.WriteAllText(ini, "[AGPH_Test]\nEnabled=y\nOpenFilePath=D:/Old\n[Other]\nEnabled=y\n");
        var options = new AdminOperations.Options(ConfigurationPath: ini, RestartService: false, SkipRunningCheck: true);

        var okRequest = Path.Combine(root, "ok.json");
        var okResult = Path.Combine(root, "ok.result.json");
        await File.WriteAllBytesAsync(okRequest, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ElevationRequest(AdminOperation.WriteThrough, "AGPH_Test", new[] { root }), HubJsonContext.Default.ElevationRequest));
        Equal(0, AdminOperations.RunHelper(okRequest, okResult, options));
        var written = File.ReadAllText(ini);
        True(written.Contains("OpenFilePath=" + root), "direct-write rules land in bare form");
        True(!written.Contains("OpenFilePath.1"), "indexed keys are never produced");
        True(!written.Contains("D:/Old"), "previous rules are replaced");
        True(written.Contains("[Other]"), "other boxes are untouched");
        var ok = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(okResult), HubJsonContext.Default.ElevationResult)!;
        True(ok.Succeeded, "success is reported");
        True(ok.BackupPath is not null && File.Exists(ok.BackupPath), "the backup path is reported back");

        var absentRequest = Path.Combine(root, "absent.json");
        var absentResult = Path.Combine(root, "absent.result.json");
        await File.WriteAllBytesAsync(absentRequest, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ElevationRequest(AdminOperation.WriteThrough, "AGPH_Absent", new[] { root }), HubJsonContext.Default.ElevationRequest));
        Equal(1, AdminOperations.RunHelper(absentRequest, absentResult, options));
        var failed = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(absentResult), HubJsonContext.Default.ElevationResult)!;
        True(!failed.Succeeded, "a missing box section is a failure, not a silent no-op");
        True(!string.IsNullOrWhiteSpace(failed.Detail), "failures carry a reason");

        var unknownRequest = Path.Combine(root, "unknown.json");
        var unknownResult = Path.Combine(root, "unknown.result.json");
        await File.WriteAllBytesAsync(unknownRequest, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ElevationRequest("nuke-everything", "AGPH_Test", new[] { root }), HubJsonContext.Default.ElevationRequest));
        Equal(1, AdminOperations.RunHelper(unknownRequest, unknownResult, options));
        True(!System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(unknownResult), HubJsonContext.Default.ElevationResult)!.Succeeded,
            "unknown operations are refused");

        Equal(1, AdminOperations.RunHelper(Path.Combine(root, "nope.json"), Path.Combine(root, "nope.result.json"), options));

        var bridge = new WindowsElevationBridge(Path.Combine(root, "absent.exe"), root);
        True(!bridge.IsAvailable, "a missing helper is never advertised as usable");
        var denied = await bridge.RunAsync(new ElevationRequest(AdminOperation.CreateBox, "AGPH_Test", Array.Empty<string>()));
        True(!denied.Succeeded, "without a helper the bridge reports failure instead of pretending");
        var unavailable = await new UnavailableElevationBridge("disabled").RunAsync(
            new ElevationRequest(AdminOperation.CreateBox, "AGPH_Test", Array.Empty<string>()));
        True(!unavailable.Succeeded && unavailable.Detail == "disabled", "the null bridge never succeeds");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}

static async Task TestIsolationSetupAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    var elevation = new FakeElevationBridge(backend);
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend, () => (fixture.Executable, Array.Empty<string>()));
    var setup = new IsolationSetup(launcher, elevation, _ => backend);

    // Fresh profile: the whole chain is applied in one call.
    var id = Guid.NewGuid();
    var report = await setup.EnsureReadyAsync(id, IsolationBackendKind.Sandboxie, allowFreshBox: false);
    True(report.Ready, "a fresh profile becomes ready in one call");
    Equal(IsolationStage.Ready, report.Stage);
    Equal(1, elevation.Calls.Count(c => c.Operation == AdminOperation.CreateBox));
    Equal(1, elevation.Calls.Count(c => c.Operation == AdminOperation.WriteThrough));
    Equal(1, backend.ShimCalls);

    // The same call a second time must apply nothing.
    elevation.Calls.Clear();
    var shimBefore = backend.ShimCalls;
    var again = await setup.EnsureReadyAsync(id, IsolationBackendKind.Sandboxie, allowFreshBox: false);
    True(again.Ready);
    Equal(0, elevation.Calls.Count);
    Equal(shimBefore, backend.ShimCalls);
    True(again.Steps.All(step => !step.Applied), "a second run applies nothing");

    // A pre-existing unowned box still requires an explicit acknowledgement.
    var other = Guid.NewGuid();
    backend.Boxes.Add(IsolatedProfileLauncher.BoxNameFor(other));
    await ThrowsAsync<HubException>(() => setup.EnsureReadyAsync(other, IsolationBackendKind.Sandboxie, allowFreshBox: false));
    var claimed = await setup.EnsureReadyAsync(other, IsolationBackendKind.Sandboxie, allowFreshBox: true);
    True(claimed.Ready, "an acknowledged fresh box can be taken over");

    // The legacy five-rule direct-write form is honoured as complete, so
    // profiles configured before the one-rule form are not forced to reapply.
    var legacy = Guid.NewGuid();
    var legacyBox = IsolatedProfileLauncher.BoxNameFor(legacy);
    backend.Boxes.Add(legacyBox);
    await launcher.PrepareAsync(legacy, legacyBox, claimExistingFreshBox: true);
    backend.SetConfig(legacyBox,
        fixture.Paths.Home(legacy), fixture.Paths.Roaming(legacy), fixture.Paths.Local(legacy),
        fixture.Paths.Temp(legacy), fixture.Paths.UserData(legacy));
    Equal(IsolationStage.Ready, await setup.DetectAsync(legacy, IsolationBackendKind.Sandboxie));

    // Elevation failure is surfaced, never swallowed.
    elevation.FailNext = true;
    var blocked = Guid.NewGuid();
    await ThrowsAsync<HubException>(() => setup.EnsureReadyAsync(blocked, IsolationBackendKind.Sandboxie, allowFreshBox: false));
    True((await launcher.FindAsync(blocked)) is null, "a failed elevation leaves no half-prepared mapping");
}

static async Task TestProfileResolveAsync()
{
    using var fixture = new Fixture();
    var alpha = await fixture.Profiles.CreateAsync("Work Alpha");
    var beta = await fixture.Profiles.CreateAsync("Work Beta");
    var personal = await fixture.Profiles.CreateAsync("Personal");

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
        fixture.Repository, _ => backend, () => (fixture.Executable, Array.Empty<string>()));
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

static async Task TestScopeVerificationFailsClosedAsync()
{
    using var fixture = new Fixture();
    var backend = new FakeIsolationBackend();
    // The fake never writes a language-server log, so a process-local launch has
    // no way to prove that it moved off the shared credential entry.
    var launcher = new IsolatedProfileLauncher(fixture.Paths, new FakeDriver(fixture.Paths, fixture.Executable),
        fixture.Repository, _ => backend, () => (fixture.Executable, Array.Empty<string>()),
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

    // A process-local scope claims no credential entry, so the shim stays idle.
    var prepared = await launcher.PrepareAsync(id, null, claimExistingFreshBox: true);
    True(prepared.KeyringClaimed, "a prepared scope is allowed to launch");
    Equal(0, backend.ShimCalls);

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

static async Task TestIsolationBackendDefaultsAsync()
{
    using var fixture = new Fixture();
    // New Profiles are process-local: no sandbox product has to be installed.
    var created = await fixture.Hub.CreateProfileAsync("A");
    Equal(IsolationBackendKind.ProcessLocal, created.Backend);

    // The backend field is additive, so a document written before it existed must
    // still load; a Profile with no field keeps the historical Sandboxie backend.
    var legacy = "{\"schemaVersion\":1,\"profiles\":[{\"id\":\"" + created.Id +
        "\",\"name\":\"A\",\"workspacePath\":null,\"createdUtc\":\"2026-01-01T00:00:00+00:00\"," +
        "\"updatedUtc\":\"2026-01-01T00:00:00+00:00\"}]}";
    File.WriteAllText(fixture.Paths.ProfilesFile, legacy);
    var profiles = await fixture.Hub.ListProfilesAsync();
    Equal(1, profiles.Count);
    Equal(IsolationBackendKind.Sandboxie, profiles[0].Backend);

    // An explicit choice always wins over the default, in both directions.
    Equal(IsolationBackendKind.ProcessLocal, (await fixture.Hub.CreateProfileAsync("Local", null, IsolationBackendKind.ProcessLocal)).Backend);
    Equal(IsolationBackendKind.Sandboxie, (await fixture.Hub.CreateProfileAsync("Sandboxed", null, IsolationBackendKind.Sandboxie)).Backend);

    // The default is itself a setting that round-trips.
    var settings = await fixture.Repository.ReadSettingsAsync();
    await fixture.Repository.WriteSettingsAsync(settings with { DefaultBackend = IsolationBackendKind.Sandboxie });
    Equal(IsolationBackendKind.Sandboxie, (await fixture.Hub.CreateProfileAsync("FollowsSetting")).Backend);
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
    var sandboxed = driver.BuildLaunchPlan(installation, id);
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
    Equal(1, backend.GetIndexedSettingValues(scope, "OpenFilePath").Count);

    // Unknown or malformed scopes stay configuration errors rather than a silent fallback.
    Throws<ConfigurationException>(() => IsolationBackends.TryCreate(fixture.Paths, (IsolationBackendKind)9));
    Throws<ConfigurationException>(() => { _ = new ProcessLocalBackend(fixture.Paths).BoxExists("not-a-scope"); });
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

    DeepLinkRouter.ClearPending(fixture.Paths);
    Equal(null, await DeepLinkRouter.ReadPendingAsync(fixture.Paths));
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

static (string Root, string Helper, string ToolsMuxer, string SystemRoot) CreateElevationLayout(bool selfContained, bool repoTools, bool systemHas10)
{
    var root = Path.Combine(Path.GetTempPath(), "AGProfileHub-Tests", Guid.NewGuid().ToString("N"));
    var helperDir = Path.Combine(root, "repo", "src", "AGProfileHub.Cli", "bin", "Release", "net10.0");
    Directory.CreateDirectory(helperDir);
    var helper = Path.Combine(helperDir, "AGProfileHub.Cli.exe");
    File.WriteAllBytes(helper, []);
    File.WriteAllBytes(Path.ChangeExtension(helper, ".dll"), []);
    if (selfContained) File.WriteAllBytes(Path.Combine(helperDir, "coreclr.dll"), []);
    var toolsMuxer = Path.Combine(root, "repo", ".tools", "dotnet", "dotnet.exe");
    if (repoTools) WriteDotnetLayout(toolsMuxer, "10.0.0");
    var systemRoot = Path.Combine(root, "pf", "dotnet");
    WriteDotnetLayout(Path.Combine(systemRoot, "dotnet.exe"), systemHas10 ? "10.0.0" : "7.0.20");
    return (root, helper, toolsMuxer, systemRoot);

    static void WriteDotnetLayout(string muxerPath, string runtimeVersion)
    {
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(muxerPath)!, "shared", "Microsoft.NETCore.App", runtimeVersion));
        File.WriteAllBytes(muxerPath, []);
    }
}

static Task TestElevationMuxerResolution()
{
    var (root, helper, toolsMuxer, systemRoot) = CreateElevationLayout(selfContained: false, repoTools: true, systemHas10: false);
    try
    {
        // An apphost-style DOTNET_HOST_PATH and a 7-only DOTNET_ROOT are both
        // unusable, so resolution must walk up to the repo-local .tools SDK.
        Equal(toolsMuxer, WindowsElevationBridge.ResolveDotnetMuxer(helper,
            Path.Combine(root, "pf", "dotnet", "AGProfileHub.Cli.exe"), systemRoot, systemRoot, Path.Combine(root, "pf")));
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static Task TestElevationMuxerUsesHostPath()
{
    var (root, helper, toolsMuxer, _) = CreateElevationLayout(selfContained: false, repoTools: true, systemHas10: false);
    try { Equal(toolsMuxer, WindowsElevationBridge.ResolveDotnetMuxer(helper, toolsMuxer, null, null, null)); }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static Task TestElevationMuxerSkipsSelfContained()
{
    var (root, helper, toolsMuxer, _) = CreateElevationLayout(selfContained: true, repoTools: true, systemHas10: true);
    try { Equal(null, WindowsElevationBridge.ResolveDotnetMuxer(helper, toolsMuxer, null, null, null)); }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static Task TestElevationMuxerNotFound()
{
    var (root, helper, _, systemRoot) = CreateElevationLayout(selfContained: false, repoTools: false, systemHas10: false);
    try { Equal(null, WindowsElevationBridge.ResolveDotnetMuxer(helper, null, systemRoot, systemRoot, Path.Combine(root, "pf"))); }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static Task TestSandboxProcessContext()
{
    var box = SandboxProcessContext.CurrentBox();
    if (box is null)
    {
        // On an unsandboxed host nothing is injected, so every request must fail
        // closed rather than fall through to the host credential store.
        Throws<HubException>(() => SandboxProcessContext.RequireBox());
        Throws<HubException>(() => SandboxProcessContext.RequireBox("AGPH_AnyBox"));
    }
    else
    {
        // Running inside a box (for example a CI job launched through Sandboxie):
        // the box itself is accepted, and anything else is still rejected.
        True(box.Length > 0, "a sandboxed process reports its box name");
        SandboxProcessContext.RequireBox();
        SandboxProcessContext.RequireBox(box);
        SandboxProcessContext.RequireBox(box.ToUpperInvariant());
        Throws<HubException>(() => SandboxProcessContext.RequireBox(box + "-other-box"));
    }
    return Task.CompletedTask;
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
    public LaunchPlan BuildLaunchPlan(AntigravityInstallation installation, Guid profileId,
        IsolationBackendKind backend = IsolationBackendKind.Sandboxie)
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
    public void SetConfig(string box, params string[] values) => _settings[box] = [.. values];
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

sealed class FakeElevationBridge(FakeIsolationBackend backend) : IElevationBridge
{
    public bool IsAvailable => true;
    public bool IsElevated => true;
    public List<ElevationRequest> Calls { get; } = [];
    public bool FailNext { get; set; }

    public Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        if (FailNext) return Task.FromResult(ElevationResult.Failed("fake elevation failure"));
        switch (request.Operation)
        {
            case AdminOperation.CreateBox:
                backend.CreateBox(request.BoxName);
                break;
            case AdminOperation.WriteThrough:
                backend.SetConfig(request.BoxName, [.. request.Paths]);
                break;
            default:
                return Task.FromResult(ElevationResult.Failed($"unknown operation {request.Operation}"));
        }
        return Task.FromResult(ElevationResult.Ok());
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
