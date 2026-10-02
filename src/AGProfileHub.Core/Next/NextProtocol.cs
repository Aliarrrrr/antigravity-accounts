using Microsoft.Win32;

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
    private const string CommandKey = @"Software\Classes\antigravity\shell\open\command";
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromMinutes(30);
    private static string RecordPath(HubPaths paths) => Path.Combine(paths.DataRoot, "runtime", "deeplink-registration.json");
    public static string? ReadRegisteredCommand()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(CommandKey); return key?.GetValue("") as string;
    }
    private static DeepLinkRegistration? ReadRecord(HubPaths paths) => new AtomicJsonStore(paths.DataRoot).ReadAsync(RecordPath(paths),
        HubJsonContext.Default.DeepLinkRegistration, () => null!, CancellationToken.None).GetAwaiter().GetResult();
    public static string CanonicalizeRouterCommand(HubPaths paths, string command)
    {
        var argv = CommandLineIdentity.Split(command);
        if (argv.Count != 5 || argv[1] != "--data-root" || !NextHubService.EqualPath(argv[2], paths.DataRoot) || argv[3] != "callback" || argv[4] != "%1")
            throw new ConfigurationException("Invalid Next callback registration record.");
        var executable = PathSafety.CanonicalizeExistingFile(argv[0]);
        if (!Path.GetFileName(executable).Equals("agprofile-next.exe", StringComparison.OrdinalIgnoreCase)) throw new ConfigurationException("Foreign callback executable.");
        return DesktopShortcut.QuoteArgument(executable) + " --data-root " + DesktopShortcut.QuoteArgument(paths.DataRoot) + " callback \"%1\"";
    }
    public static bool IsRegisteredFor(HubPaths paths)
    {
        var record = ReadRecord(paths); return record is not null && ReadRegisteredCommand() == record.RouterCommand &&
            CanonicalizeRouterCommand(paths, record.RouterCommand) == record.RouterCommand;
    }
    public static void Register(HubPaths paths, string cli)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        cli = PathSafety.CanonicalizeExistingFile(cli);
        var command = DesktopShortcut.QuoteArgument(cli) + " --data-root " + DesktopShortcut.QuoteArgument(paths.DataRoot) + " callback \"%1\"";
        command = CanonicalizeRouterCommand(paths, command);
        var existing = ReadRecord(paths);
        var record = new DeepLinkRegistration { RouterCommand = command, PreviousCommand = existing is null ? ReadRegisteredCommand() : existing.PreviousCommand, RegisteredUtc = DateTimeOffset.UtcNow };
        var store = new AtomicJsonStore(paths.DataRoot);
        store.WriteAsync(RecordPath(paths), record, HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(CommandKey); key.SetValue("", command);
        }
        catch
        {
            if (existing is null) File.Delete(RecordPath(paths));
            else store.WriteAsync(RecordPath(paths), existing, HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None).GetAwaiter().GetResult();
            throw;
        }
    }
    public static bool Restore(HubPaths paths)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var record = ReadRecord(paths); if (record is null || ReadRegisteredCommand() != record.RouterCommand) return false;
        using var key = Registry.CurrentUser.OpenSubKey(CommandKey, true);
        if (record.PreviousCommand is null) key?.DeleteValue("", false); else key?.SetValue("", record.PreviousCommand);
        File.Delete(RecordPath(paths)); return true;
    }
    public static void ValidateCallbackUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 8192 || uri.Any(char.IsControl) || !uri.StartsWith("antigravity://", StringComparison.OrdinalIgnoreCase))
            throw new HubException("Invalid vendor callback URI.", "deeplink_callback_invalid");
    }
}
