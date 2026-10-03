using AGProfileHub.Core;

internal static class NextProtocolTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Next protocol cancellation before registration leaves no record", PreCancelledAsync),
        ("Next protocol registration and restoration preserve absent and vendor handlers", RoundTripAsync),
        ("Next protocol empty handlers preserve their exact backup", EmptyHandlerAsync),
        ("Next protocol an unrecorded canonical self recovers without a self backup", UnrecordedSelfAsync),
        ("Next protocol unrecorded lookalikes cannot claim ownership", UnrecordedLookalikeAsync),
        ("Next protocol repeated registration preserves the first backup", RepeatAsync),
        ("Next protocol reassertion requires an existing record and recognized handler", ReassertBoundaryAsync),
        ("Next protocol reassertion keeps restoration behind the caller's transaction gate", ReassertCompositeGateAsync),
        ("Next protocol unknown handlers and extra vendor arguments are refused", UnknownHandlerAsync),
        ("Next protocol corrupt and tampered records remain unchanged", InvalidRecordAsync),
        ("Next protocol registration is serialized across data roots", CrossRootGateAsync),
        ("Next protocol cancellation while waiting for the gate has no effects", GateCancellationAsync),
        ("Next protocol cancellation before mutation preserves the original handler", BeforeMutationCancellationAsync),
        ("Next protocol cancellation after mutation cannot strand the transaction", AfterMutationCancellationAsync),
        ("Next protocol failed writes before and after visibility restore the previous handler", FailedRegistrationAsync),
        ("Next protocol failed compensation retains the owned handler and recovery record", FailedCompensationAsync),
        ("Next protocol compensation preserves a third-party takeover", TakeoverAsync),
        ("Next protocol restore skips missing and foreign ownership safely", RestoreSkipAsync),
        ("Next protocol restore errors retain recovery evidence", RestoreFailureAsync),
        ("Next probe protocol restoration accepts only exact vendor handlers", ProbeRestoreAsync),
        ("Next probe gate prevents stale restoration over another data root", ProbeCrossRootGateAsync)
    ];

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static async Task ExpectCode(Func<Task> operation, string code)
    {
        try { await operation(); }
        catch (HubException error) when (error.Code == code) { return; }
        throw new Exception("Expected protocol failure: " + code);
    }

    private static async Task ExpectCancelled(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancelled protocol operation completed.");
    }

    private static async Task PreCancelledAsync()
    {
        using var fixture = new ProtocolFixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ExpectCancelled(() => NextProtocol.RegisterAsync(fixture.Paths, fixture.Cli, null, cancelled.Token));
        Check(!File.Exists(fixture.RecordPath), "Cancelled registration persisted a record.");
    }

    private static async Task RoundTripAsync()
    {
        foreach (var arguments in new string?[] { null, "\"%1\"", "--open-url \"%1\"", "--open-url -- \"%1\"" })
        {
            using var fixture = new ProtocolFixture();
            var previous = arguments is null ? null : '"' + fixture.Vendor + "\" " + arguments;
            fixture.Commands.Current = previous;
            await fixture.RegisterAsync();
            Check(fixture.Commands.Current == fixture.ExpectedRouter, "Registration produced an unexpected callback command.");
            var record = await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None);
            Check(record?.PreviousCommand == previous, "Registration did not save the exact previous command.");
            Check(await fixture.IsRegisteredAsync(), "The registered router was not recognized.");
            Check(await fixture.RestoreAsync(), "The owned router was not restored.");
            Check(fixture.Commands.Current == previous && !File.Exists(fixture.RecordPath), "Restoration lost the prior handler or retained the active record.");
        }
    }

    private static async Task RepeatAsync()
    {
        using var fixture = new ProtocolFixture();
        var previous = '"' + fixture.Vendor + "\" --open-url \"%1\"";
        fixture.Commands.Current = previous;
        await fixture.RegisterAsync();
        var newCli = Path.Combine(fixture.Root, "updated", "agprofile-next.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(newCli)!);
        await File.WriteAllTextAsync(newCli, "updated fixture");
        await NextProtocol.RegisterAsync(fixture.Paths, newCli, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None);
        await NextProtocol.RegisterAsync(fixture.Paths, newCli, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None);
        Check((await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None))?.PreviousCommand == previous,
            "Repeated registration replaced the first backup with a Next command.");
        Check(await fixture.RestoreAsync() && fixture.Commands.Current == previous, "Repeated registration did not restore the original vendor handler.");
    }

    private static async Task EmptyHandlerAsync()
    {
        using var fixture = new ProtocolFixture();
        fixture.Commands.Current = "";
        await fixture.RegisterAsync();
        var record = await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None);
        Check(record?.PreviousCommand == "" && fixture.Commands.Current == fixture.ExpectedRouter,
            "An empty handler was refused or its exact empty string backup was lost.");
        Check(await fixture.RestoreAsync() && fixture.Commands.Current == "" && !File.Exists(fixture.RecordPath),
            "Restoration converted an empty string handler into an absent handler.");
    }

    private static async Task UnrecordedSelfAsync()
    {
        using var fixture = new ProtocolFixture();
        fixture.Commands.Current = fixture.ExpectedRouter;
        await fixture.RegisterAsync();
        var record = await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None);
        Check(record is not null && record.PreviousCommand is null && fixture.Commands.Current == fixture.ExpectedRouter,
            "An exact canonical self was refused or backed itself up as its previous handler.");
        Check(await fixture.RestoreAsync() && fixture.Commands.Current is null && !File.Exists(fixture.RecordPath),
            "Restoration left an unrecorded self handler pointing at a removable executable.");
    }

    private static async Task UnrecordedLookalikeAsync()
    {
        using var fixture = new ProtocolFixture();
        var foreignCli = Path.Combine(fixture.Root, "foreign", "agprofile-next.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignCli)!);
        await File.WriteAllTextAsync(foreignCli, "foreign fixture");
        var foreignRoot = Path.Combine(fixture.Root, "foreign-root");
        foreach (var command in new[]
        {
            '"' + foreignCli + "\" --data-root \"" + fixture.Paths.DataRoot + "\" callback \"%1\"",
            '"' + fixture.Cli + "\" --data-root \"" + foreignRoot + "\" callback \"%1\"",
            fixture.ExpectedRouter + " --foreign"
        })
        {
            fixture.Commands.Current = command;
            await ExpectCode(() => fixture.RegisterAsync(), "protocol_owned_elsewhere");
            Check(fixture.Commands.Current == command && !File.Exists(fixture.RecordPath),
                "An unrecorded handler sharing only a filename, executable, or command prefix was accepted.");
        }
    }

    private static async Task UnknownHandlerAsync()
    {
        foreach (var command in new[] { "foreign-manager callback \"%1\"", "foreign-handler \"%1\"" })
        {
            using var fixture = new ProtocolFixture();
            fixture.Commands.Current = command;
            var before = fixture.Commands.Current;
            await ExpectCode(() => fixture.RegisterAsync(), "protocol_owned_elsewhere");
            Check(fixture.Commands.Current == before && !File.Exists(fixture.RecordPath), "Unknown ownership was overwritten.");
        }
        using var extra = new ProtocolFixture();
        extra.Commands.Current = '"' + extra.Vendor + "\" --open-url --unsafe \"%1\"";
        await ExpectCode(() => extra.RegisterAsync(), "protocol_owned_elsewhere");
        Check(!File.Exists(extra.RecordPath), "Extra vendor arguments were accepted.");
    }

    private static async Task ReassertBoundaryAsync()
    {
        using var fixture = new ProtocolFixture();
        await ExpectCode(() => NextProtocol.ReassertAsync(fixture.Paths, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None),
            "protocol_not_registered");
        await fixture.RegisterAsync();
        fixture.Commands.Current = '"' + fixture.Vendor + "\" --open-url \"%1\"";
        await NextProtocol.ReassertAsync(fixture.Paths, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None);
        Check(fixture.Commands.Current == fixture.ExpectedRouter &&
            (await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None))?.PreviousCommand is null,
            "Vendor reassertion replaced the first backup.");
        var saved = await File.ReadAllBytesAsync(fixture.RecordPath);
        foreach (var empty in new string?[] { null, "" })
        {
            fixture.Commands.Current = empty;
            await ExpectCode(() => NextProtocol.ReassertAsync(fixture.Paths, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None),
                "protocol_owned_elsewhere");
            Check(fixture.Commands.Current == empty && (await File.ReadAllBytesAsync(fixture.RecordPath)).SequenceEqual(saved),
                "Reassertion took an absent or empty handler instead of refusing changed ownership.");
        }
        fixture.Commands.Current = fixture.ExpectedRouter;
        Check(await fixture.RestoreAsync(), "Fixture restoration failed.");
        await ExpectCode(() => NextProtocol.ReassertAsync(fixture.Paths, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None),
            "protocol_not_registered");
        Check(fixture.Commands.Current is null && !File.Exists(fixture.RecordPath), "Reassertion recreated ownership after restoration removed its record.");
    }

    private static async Task InvalidRecordAsync()
    {
        using var fixture = new ProtocolFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.RecordPath)!);
        var corrupt = System.Text.Encoding.UTF8.GetBytes("{ broken");
        await File.WriteAllBytesAsync(fixture.RecordPath, corrupt);
        await ExpectCode(() => fixture.RegisterAsync(), "configuration_corrupt");
        Check((await File.ReadAllBytesAsync(fixture.RecordPath)).SequenceEqual(corrupt) && fixture.Commands.Current is null,
            "Corrupt protocol record was overwritten.");
        File.Delete(fixture.RecordPath);
        await fixture.RegisterAsync();
        await new AtomicJsonStore(fixture.Paths.DataRoot).WriteAsync(fixture.RecordPath,
            new DeepLinkRegistration { RouterCommand = fixture.ExpectedRouter + " --foreign", RegisteredUtc = DateTimeOffset.UtcNow },
            HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None);
        var tampered = await File.ReadAllBytesAsync(fixture.RecordPath);
        await ExpectCode(() => fixture.RegisterAsync(), "configuration_invalid");
        await ExpectCode(async () => _ = await fixture.RestoreAsync(), "configuration_invalid");
        await ExpectCode(async () => _ = await fixture.IsRegisteredAsync(), "configuration_invalid");
        Check((await File.ReadAllBytesAsync(fixture.RecordPath)).SequenceEqual(tampered) && fixture.Commands.Current == fixture.ExpectedRouter,
            "Tampered record changed callback ownership.");
    }

    private static async Task ReassertCompositeGateAsync()
    {
        using var fixture = new ProtocolFixture();
        await fixture.RegisterAsync();
        fixture.Commands.Current = '"' + fixture.Vendor + "\" --open-url \"%1\"";
        Task<bool> restoration;
        await using (var held = await FileLock.AcquireAsync(fixture.GatePath, TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            await NextProtocol.ReassertUnderLockAsync(fixture.Paths, fixture.Vendor, fixture.Commands, CancellationToken.None);
            restoration = fixture.RestoreAsync();
            await Task.Delay(100);
            Check(!restoration.IsCompleted && fixture.Commands.Current == fixture.ExpectedRouter && File.Exists(fixture.RecordPath),
                "Restoration interrupted the caller's transaction after reassertion completed.");
        }
        Check(await restoration && fixture.Commands.Current is null && !File.Exists(fixture.RecordPath),
            "Restoration did not continue after the composite transaction released its gate.");
    }

    private static async Task CrossRootGateAsync()
    {
        using var fixture = new ProtocolFixture();
        var other = new HubPaths(Path.Combine(fixture.Root, "other-data"), Path.Combine(fixture.Root, "other-global"));
        Task first; Task second;
        await using (var held = await FileLock.AcquireAsync(fixture.GatePath, TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            first = fixture.RegisterAsync();
            second = NextProtocol.RegisterAsync(other, fixture.Cli, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None);
            await Task.Delay(100);
            Check(!first.IsCompleted && !second.IsCompleted, "A registration bypassed the shared protocol gate.");
            Check(!File.Exists(fixture.RecordPath) && !Directory.Exists(other.DataRoot), "Blocked registration modified data before acquiring its gate.");
        }
        var successes = 0;
        foreach (var operation in new[] { first, second })
        {
            try { await operation; successes++; }
            catch (HubException error) when (error.Code == "protocol_owned_elsewhere") { }
        }
        Check(successes == 1, "Two data roots both claimed callback ownership.");
    }

    private static async Task GateCancellationAsync()
    {
        using var fixture = new ProtocolFixture();
        using var cancelled = new CancellationTokenSource();
        await using var held = await FileLock.AcquireAsync(fixture.GatePath, TimeSpan.FromSeconds(2), CancellationToken.None);
        var operation = fixture.RegisterAsync(cancelled.Token);
        cancelled.Cancel();
        await ExpectCancelled(() => operation);
        Check(fixture.Commands.Current is null && !File.Exists(fixture.RecordPath), "Gate cancellation left a handler or record.");
    }

    private static async Task BeforeMutationCancellationAsync()
    {
        using var fixture = new ProtocolFixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Commands.Reading = cancelled.Cancel;
        await ExpectCancelled(() => fixture.RegisterAsync(cancelled.Token));
        Check(fixture.Commands.Current is null && !File.Exists(fixture.RecordPath), "Cancellation before the record mutation changed ownership.");
    }

    private static async Task AfterMutationCancellationAsync()
    {
        using var fixture = new ProtocolFixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Commands.BeforeWrite = _ => cancelled.Cancel();
        await fixture.RegisterAsync(cancelled.Token);
        Check(fixture.Commands.Current == fixture.ExpectedRouter && await fixture.IsRegisteredAsync(),
            "Cancellation after record mutation stranded registration.");
        fixture.Commands.BeforeWrite = null;
        using var restoreCancelled = new CancellationTokenSource();
        fixture.Commands.BeforeDelete = restoreCancelled.Cancel;
        Check(await fixture.RestoreAsync(restoreCancelled.Token), "Cancellation during restoration interrupted the committed transaction.");
        Check(fixture.Commands.Current is null && !File.Exists(fixture.RecordPath), "Committed restoration retained an owned handler or record.");
    }

    private static async Task FailedRegistrationAsync()
    {
        foreach (var afterVisible in new[] { false, true })
        {
            using var fixture = new ProtocolFixture();
            var previous = '"' + fixture.Vendor + "\" \"%1\"";
            fixture.Commands.Current = previous;
            var failed = false;
            Action<string> failOnce = _ => { if (!failed) { failed = true; throw new IOException("fixture write failure"); } };
            if (afterVisible) fixture.Commands.AfterWrite = failOnce; else fixture.Commands.BeforeWrite = failOnce;
            await ExpectCode(() => fixture.RegisterAsync(), "protocol_registration_failed");
            Check(fixture.Commands.Current == previous && !File.Exists(fixture.RecordPath),
                "Failed registration did not compensate the visible handler and provisional record.");
        }
    }

    private static async Task FailedCompensationAsync()
    {
        using var fixture = new ProtocolFixture();
        var previous = '"' + fixture.Vendor + "\" \"%1\"";
        fixture.Commands.Current = previous;
        fixture.Commands.BeforeWrite = command => { if (command == previous) throw new IOException("fixture compensation failure"); };
        fixture.Commands.AfterWrite = command => { if (command == fixture.ExpectedRouter) throw new IOException("fixture visible write failure"); };
        await ExpectCode(() => fixture.RegisterAsync(), "protocol_registration_failed");
        Check(fixture.Commands.Current == fixture.ExpectedRouter &&
            (await NextProtocol.ReadRecordAsync(fixture.Paths, CancellationToken.None))?.PreviousCommand == previous,
            "Failed compensation discarded the still-owned handler's recovery record.");
        fixture.Commands.BeforeWrite = null; fixture.Commands.AfterWrite = null;
        Check(await fixture.RestoreAsync() && fixture.Commands.Current == previous, "Retained recovery record could not restore ownership.");
    }

    private static async Task TakeoverAsync()
    {
        using var fixture = new ProtocolFixture();
        const string foreign = "another-manager \"%1\"";
        fixture.Commands.AfterWrite = _ => { fixture.Commands.Current = foreign; throw new IOException("fixture takeover"); };
        await ExpectCode(() => fixture.RegisterAsync(), "protocol_registration_failed");
        Check(fixture.Commands.Current == foreign && !File.Exists(fixture.RecordPath), "Compensation overwrote a third-party takeover.");
    }

    private static async Task RestoreSkipAsync()
    {
        using var fixture = new ProtocolFixture();
        Check(!await fixture.RestoreAsync(), "Missing registration was treated as owned.");
        await fixture.RegisterAsync();
        var saved = await File.ReadAllBytesAsync(fixture.RecordPath);
        fixture.Commands.Current = "another-manager \"%1\"";
        Check(!await fixture.RestoreAsync(), "Foreign registration was overwritten.");
        Check(fixture.Commands.Current == "another-manager \"%1\"" && (await File.ReadAllBytesAsync(fixture.RecordPath)).SequenceEqual(saved),
            "Foreign ownership skip modified registry or recovery evidence.");
    }

    private static async Task RestoreFailureAsync()
    {
        foreach (var afterVisible in new[] { false, true })
        {
            using var fixture = new ProtocolFixture();
            await fixture.RegisterAsync();
            var saved = await File.ReadAllBytesAsync(fixture.RecordPath);
            if (afterVisible) fixture.Commands.AfterDelete = () => throw new IOException("fixture visible delete failure");
            else fixture.Commands.BeforeDelete = () => throw new IOException("fixture delete failure");
            await ExpectCode(async () => _ = await fixture.RestoreAsync(), "protocol_restore_failed");
            Check((await File.ReadAllBytesAsync(fixture.RecordPath)).SequenceEqual(saved), "Restore failure discarded recovery evidence.");
            Check(fixture.Commands.Current == (afterVisible ? null : fixture.ExpectedRouter), "Restore failure changed unexpected ownership.");
        }
        using var mismatch = new ProtocolFixture();
        await mismatch.RegisterAsync();
        mismatch.Commands.AfterDelete = () => mismatch.Commands.Current = "foreign takeover";
        await ExpectCode(async () => _ = await mismatch.RestoreAsync(), "protocol_restore_failed");
        Check(File.Exists(mismatch.RecordPath) && mismatch.Commands.Current == "foreign takeover", "Post-write verification failure lost evidence or overwrote foreign ownership.");
    }

    private static async Task ProbeRestoreAsync()
    {
        using var fixture = new ProtocolFixture();
        const string previous = "previous handler";
        fixture.Commands.Current = previous;
        Check(await NextProtocol.RestoreProbeProtocolAsync(previous, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None),
            "An unchanged probe handler did not count as restored.");
        fixture.Commands.Current = '"' + fixture.Vendor + "\" --open-url \"%1\"";
        Check(await NextProtocol.RestoreProbeProtocolAsync(previous, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None) && fixture.Commands.Current == previous,
            "Exact vendor handler was not restored after probing.");
        fixture.Commands.Current = '"' + fixture.Vendor + "\" --open-url --foreign \"%1\"";
        var foreign = fixture.Commands.Current;
        Check(!await NextProtocol.RestoreProbeProtocolAsync(previous, fixture.Vendor, fixture.Commands, fixture.GatePath, CancellationToken.None) && fixture.Commands.Current == foreign,
            "Probe restoration overwrote an unknown handler sharing the executable prefix.");
    }

    private static async Task ProbeCrossRootGateAsync()
    {
        using var fixture = new ProtocolFixture();
        var other = new HubPaths(Path.Combine(fixture.Root, "other-data"), Path.Combine(fixture.Root, "other-global"));
        Task registration;
        await using (var held = await FileLock.AcquireAsync(fixture.GatePath, TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            // A probe snapshots and then temporarily observes the vendor's protocol registration while holding the gate.
            var previous = fixture.Commands.Read();
            fixture.Commands.Current = '"' + fixture.Vendor + "\" --open-url \"%1\"";
            registration = NextProtocol.RegisterAsync(other, fixture.Cli, fixture.Vendor, fixture.Commands,
                fixture.GatePath, CancellationToken.None);
            await Task.Delay(100);
            Check(!registration.IsCompleted && !Directory.Exists(other.DataRoot),
                "Another data root registered before the probe completed its protocol transaction.");
            Check(NextProtocol.RestoreProbeProtocolUnderLock(previous, fixture.Vendor, fixture.Commands, CancellationToken.None) &&
                fixture.Commands.Current is null, "Probe failed to restore its snapshot while holding the shared gate.");
        }
        await registration;
        var otherRecord = await NextProtocol.ReadRecordAsync(other, CancellationToken.None);
        Check(otherRecord is not null && fixture.Commands.Current == otherRecord.RouterCommand &&
            otherRecord.PreviousCommand is null,
            "Probe restoration clobbered the registration completed by another data root after releasing the gate.");
    }

    private sealed class MemoryCommands : IProtocolCommandStore
    {
        public string? Current { get; set; }
        public Action? Reading { get; set; }
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? AfterWrite { get; set; }
        public Action? BeforeDelete { get; set; }
        public Action? AfterDelete { get; set; }
        public string? Read() { Reading?.Invoke(); return Current; }
        public void Write(string command) { BeforeWrite?.Invoke(command); Current = command; AfterWrite?.Invoke(command); }
        public void Delete() { BeforeDelete?.Invoke(); Current = null; AfterDelete?.Invoke(); }
    }

    private sealed class ProtocolFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agprofile-next-protocol-" + Guid.NewGuid().ToString("N"));
        public HubPaths Paths { get; }
        public string Cli => Path.Combine(Root, "agprofile-next.exe");
        public string Vendor => Path.Combine(Root, "Antigravity.exe");
        public string GatePath => Path.Combine(Root, "gate", "protocol.lock");
        public string RecordPath => Path.Combine(Paths.DataRoot, "runtime", "deeplink-registration.json");
        public string ExpectedRouter => '"' + Cli + "\" --data-root \"" + Paths.DataRoot + "\" callback \"%1\"";
        public MemoryCommands Commands { get; } = new();
        public ProtocolFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Cli, "fixture"); File.WriteAllText(Vendor, "fixture");
            Paths = new HubPaths(Path.Combine(Root, "data"), Path.Combine(Root, "global"));
        }
        public Task RegisterAsync(CancellationToken token = default) =>
            NextProtocol.RegisterAsync(Paths, Cli, Vendor, Commands, GatePath, token);
        public Task<bool> IsRegisteredAsync() => NextProtocol.IsRegisteredForAsync(Paths, Commands, GatePath, CancellationToken.None);
        public Task<bool> RestoreAsync(CancellationToken token = default) => NextProtocol.RestoreAsync(Paths, Commands, GatePath, token);
        public void Dispose()
        {
            PathSafety.RejectExistingPathAncestors(Root);
            Directory.Delete(Root, recursive: true);
        }
    }
}
