namespace AGProfileHub.Core;

public sealed record PendingCallback
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProfileId { get; init; }
    public string UserDataPath { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
}
public sealed record DeepLinkRegistration
{
    public int SchemaVersion { get; init; } = 1;
    public string? PreviousCommand { get; init; }
    public string RouterCommand { get; init; } = "";
    public DateTimeOffset RegisteredUtc { get; init; }
}
public static class NextProtocol
{
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);
    internal static string DefaultLockPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AntigravityProfileHubNext", "runtime", "locks", "protocol.lock");
    private static string RecordPath(HubPaths paths) => Path.Combine(paths.DataRoot, "runtime", "deeplink-registration.json");
    public static string? ReadRegisteredCommand() => new WindowsProtocolCommandStore().Read();

    internal static async Task<DeepLinkRegistration?> ReadRecordAsync(HubPaths paths, CancellationToken token = default)
    {
        var record = await new AtomicJsonStore(paths.DataRoot).ReadAsync(RecordPath(paths),
            HubJsonContext.Default.DeepLinkRegistration, () => null!, token).ConfigureAwait(false);
        if (record is not null && CanonicalizeRouterCommand(paths, record.RouterCommand) != record.RouterCommand)
            throw new ConfigurationException("Invalid Next callback registration record.");
        return record;
    }

    public static string CanonicalizeRouterCommand(HubPaths paths, string command)
    {
        var argv = CommandLineIdentity.Split(command);
        if (argv.Count != 5 || argv[1] != "--data-root" || !NextHubService.EqualPath(argv[2], paths.DataRoot) || argv[3] != "callback" || argv[4] != "%1")
            throw new ConfigurationException("Invalid Next callback registration record.");
        var executable = PathSafety.CanonicalizeExistingFile(argv[0]);
        if (!Path.GetFileName(executable).Equals("agprofile-next.exe", StringComparison.OrdinalIgnoreCase)) throw new ConfigurationException("Foreign callback executable.");
        return DesktopShortcut.QuoteArgument(executable) + " --data-root " + DesktopShortcut.QuoteArgument(paths.DataRoot) + " callback \"%1\"";
    }
    public static async Task<bool> IsRegisteredForAsync(HubPaths paths, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return false;
        return await IsRegisteredForAsync(paths, new WindowsProtocolCommandStore(), DefaultLockPath, token).ConfigureAwait(false);
    }

    internal static async Task<bool> IsRegisteredForAsync(HubPaths paths, IProtocolCommandStore commands, string gatePath, CancellationToken token)
    {
        await using var held = await FileLock.AcquireAsync(gatePath, LockTimeout, token).ConfigureAwait(false);
        return await IsRegisteredForUnderLockAsync(paths, commands, token).ConfigureAwait(false);
    }

    internal static async Task<bool> IsRegisteredForUnderLockAsync(HubPaths paths, IProtocolCommandStore commands,
        CancellationToken token)
    {
        var record = await ReadRecordAsync(paths, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return record is not null && commands.Read() == record.RouterCommand;
    }

    public static async Task RegisterAsync(HubPaths paths, string cli, string? vendorExecutable = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await RegisterAsync(paths, cli, vendorExecutable, new WindowsProtocolCommandStore(), DefaultLockPath, token).ConfigureAwait(false);
    }

    internal static async Task RegisterAsync(HubPaths paths, string cli, string? vendorExecutable,
        IProtocolCommandStore commands, string gatePath, CancellationToken token)
    {
        await using var held = await FileLock.AcquireAsync(gatePath, LockTimeout, token).ConfigureAwait(false);
        var existing = await ReadRecordAsync(paths, token).ConfigureAwait(false);
        await RegisterCoreAsync(paths, cli, vendorExecutable, commands, existing, requireHandler: false, token).ConfigureAwait(false);
    }

    internal static async Task ReassertAsync(HubPaths paths, string? vendorExecutable,
        IProtocolCommandStore commands, string gatePath, CancellationToken token)
    {
        await using var held = await FileLock.AcquireAsync(gatePath, LockTimeout, token).ConfigureAwait(false);
        await ReassertUnderLockAsync(paths, vendorExecutable, commands, token).ConfigureAwait(false);
    }

    // Composite login transactions keep this gate until their durable waiting slot is written.
    internal static async Task ReassertUnderLockAsync(HubPaths paths, string? vendorExecutable,
        IProtocolCommandStore commands, CancellationToken token)
    {
        var existing = await ReadRecordAsync(paths, token).ConfigureAwait(false)
            ?? throw new HubException("Register callbacks before authorization.", "protocol_not_registered");
        var cli = CommandLineIdentity.Split(existing.RouterCommand)[0];
        await RegisterCoreAsync(paths, cli, vendorExecutable, commands, existing, requireHandler: true, token).ConfigureAwait(false);
    }

    private static async Task RegisterCoreAsync(HubPaths paths, string cli, string? vendorExecutable,
        IProtocolCommandStore commands, DeepLinkRegistration? existing, bool requireHandler, CancellationToken token)
    {
        cli = PathSafety.CanonicalizeExistingFile(cli);
        var command = DesktopShortcut.QuoteArgument(cli) + " --data-root " + DesktopShortcut.QuoteArgument(paths.DataRoot) + " callback \"%1\"";
        command = CanonicalizeRouterCommand(paths, command);
        var previous = commands.Read();
        var emptyHandler = string.IsNullOrEmpty(previous);
        var recordedRouter = existing is not null && previous == existing.RouterCommand;
        var unrecordedSelf = existing is null && previous == command;
        if (requireHandler && emptyHandler ||
            !emptyHandler && !recordedRouter && !unrecordedSelf && !IsVendorCommand(previous!, vendorExecutable))
            throw new HubException("Another handler owns callbacks. Restore its registration before Next takes ownership.", "protocol_owned_elsewhere");
        var record = new DeepLinkRegistration { RouterCommand = command,
            PreviousCommand = existing is not null ? existing.PreviousCommand : unrecordedSelf ? null : previous,
            RegisteredUtc = DateTimeOffset.UtcNow };
        var store = new AtomicJsonStore(paths.DataRoot);
        token.ThrowIfCancellationRequested();
        // Cancellation is permitted up to this point. Commit and compensation keep a durable recovery record.
        try
        {
            await store.WriteAsync(RecordPath(paths), record, HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None).ConfigureAwait(false);
            if (commands.Read() != previous)
                throw new HubException("Callback ownership changed during registration.", "protocol_owned_elsewhere");
            commands.Write(command);
            if (commands.Read() != command)
                throw new HubException("Callback ownership changed during registration.", "protocol_owned_elsewhere");
        }
        catch (Exception error)
        {
            await CompensateRegistrationAsync(paths, commands, store, existing, previous, command).ConfigureAwait(false);
            var code = error is HubException { Code: "protocol_owned_elsewhere" } ? "protocol_owned_elsewhere" : "protocol_registration_failed";
            throw new HubException("Callback registration failed; recovery evidence was retained when required.", code, error);
        }
    }

    private static async Task CompensateRegistrationAsync(HubPaths paths, IProtocolCommandStore commands, AtomicJsonStore store,
        DeepLinkRegistration? existing, string? previous, string command)
    {
        try
        {
            var current = commands.Read();
            if (current == command && previous != command)
            {
                // The gate serializes Hub processes. External writers do not participate, so recheck before compensation.
                if (commands.Read() == command) SetCommand(commands, previous);
                current = commands.Read();
            }
            if (current == command && previous != command) return;
            if (existing is null) DeleteRecord(paths);
            else await store.WriteAsync(RecordPath(paths), existing, HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* An unverifiable or still-owned handler must retain its recovery record. */ }
    }

    public static async Task<bool> RestoreAsync(HubPaths paths, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return false;
        return await RestoreAsync(paths, new WindowsProtocolCommandStore(), DefaultLockPath, token).ConfigureAwait(false);
    }

    internal static async Task<bool> RestoreAsync(HubPaths paths, IProtocolCommandStore commands, string gatePath, CancellationToken token)
    {
        await using var held = await FileLock.AcquireAsync(gatePath, LockTimeout, token).ConfigureAwait(false);
        var record = await ReadRecordAsync(paths, token).ConfigureAwait(false);
        if (record is null) return false;
        var mutationStarted = false;
        try
        {
            if (commands.Read() != record.RouterCommand) return false;
            token.ThrowIfCancellationRequested();
            if (commands.Read() != record.RouterCommand) return false;
            token.ThrowIfCancellationRequested();
            mutationStarted = true;
            SetCommand(commands, record.PreviousCommand);
            if (commands.Read() != record.PreviousCommand) throw new IOException("Restored command could not be verified.");
            DeleteRecord(paths);
            return true;
        }
        catch (OperationCanceledException) when (!mutationStarted) { throw; }
        catch (Exception error)
        { throw new HubException("Callback restoration failed; the recovery record was retained.", "protocol_restore_failed", error); }
    }

    internal static async Task<bool> RestoreProbeProtocolAsync(string? previous, string executable,
        IProtocolCommandStore commands, string gatePath, CancellationToken token)
    {
        await using var held = await FileLock.AcquireAsync(gatePath, LockTimeout, token).ConfigureAwait(false);
        return RestoreProbeProtocolUnderLock(previous, executable, commands, token);
    }

    // The caller must hold the shared per-user protocol gate from the initial snapshot through probe cleanup.
    internal static bool RestoreProbeProtocolUnderLock(string? previous, string executable,
        IProtocolCommandStore commands, CancellationToken token)
    {
        var mutationStarted = false;
        try
        {
            var current = commands.Read();
            token.ThrowIfCancellationRequested();
            if (current == previous) return true;
            if (current is null || !IsVendorCommand(current, executable)) return false;
            if (commands.Read() != current) return false;
            token.ThrowIfCancellationRequested();
            mutationStarted = true;
            SetCommand(commands, previous);
            if (commands.Read() != previous) throw new IOException("Restored probe command could not be verified.");
            return true;
        }
        catch (OperationCanceledException) when (!mutationStarted) { throw; }
        catch (Exception error)
        { throw new HubException("Probe callback restoration failed.", "protocol_restore_failed", error); }
    }

    private static bool IsVendorCommand(string command, string? executable)
    {
        if (executable is null || command.Length > 8192 || command.Any(char.IsControl)) return false;
        var arguments = CommandLineIdentity.Split(command);
        var supported = arguments.Count == 2 && arguments[1] == "%1" ||
            arguments.Count == 3 && arguments[1] == "--open-url" && arguments[2] == "%1" ||
            arguments.Count == 4 && arguments[1] == "--open-url" && arguments[2] == "--" && arguments[3] == "%1";
        if (!supported || !Path.IsPathFullyQualified(arguments[0])) return false;
        try
        {
            return NextHubService.EqualPath(PathSafety.CanonicalizeExistingFile(arguments[0]), PathSafety.CanonicalizeExistingFile(executable));
        }
        catch (ConfigurationException) { return false; }
    }

    private static void SetCommand(IProtocolCommandStore commands, string? command)
    { if (command is null) commands.Delete(); else commands.Write(command); }

    private static void DeleteRecord(HubPaths paths)
    {
        var path = RecordPath(paths);
        PathSafety.RejectExistingReparsePoints(paths.DataRoot, path);
        File.Delete(path);
    }
    public static void ValidateCallbackUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 8192 || uri.Any(char.IsControl) || !uri.StartsWith("antigravity://", StringComparison.OrdinalIgnoreCase))
            throw new HubException("Invalid vendor callback URI.", "deeplink_callback_invalid");
    }
}
