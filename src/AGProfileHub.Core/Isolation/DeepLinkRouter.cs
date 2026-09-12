using Microsoft.Win32;

namespace AGProfileHub.Core;

/// <summary>Describes the Profile that is currently allowed to receive a deep-link callback.</summary>
public sealed record PendingCallback
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Guid ProfileId { get; init; }
    public string UserDataPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// Routes the vendor's <c>antigravity://</c> callback to the Profile that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Windows starts <c>Antigravity.exe "%1"</c> for that scheme. Without a sandbox
/// every instance shares one registration, so the callback cannot tell which
/// Profile is waiting: it starts a fresh desktop with the launching process's
/// environment, which is how a second window for the wrong profile appears.
/// </para>
/// <para>
/// The fix relies on a behaviour that was verified against the real desktop:
/// starting <c>Antigravity.exe --user-data-dir=&lt;running profile&gt; "&lt;uri&gt;"</c>
/// hands the URI to the instance that already owns that user-data directory and
/// exits. The router therefore only has to know <em>which</em> profile is
/// waiting, and it reads that from a file the Hub writes when it launches.
/// </para>
/// <para>
/// Pointing the scheme at the router changes a machine setting, so the previous
/// command is kept in the data root and can always be restored.
/// </para>
/// </remarks>
public static class DeepLinkRouter
{
    public const string Scheme = "antigravity";

    private const string CommandKeyPath = @"Software\Classes\" + Scheme + @"\shell\open\command";

    /// <summary>The registered command that routes a callback to the waiting Profile.</summary>
    public static string RouterCommand(string cliExecutable, string dataRoot) =>
        $"\"{cliExecutable}\" --data-root \"{dataRoot}\" callback \"%1\"";

    public static string PendingPath(HubPaths paths) =>
        Path.Combine(paths.GlobalLaunchStateRoot, "deeplink-pending.json");

    public static async Task<PendingCallback?> ReadPendingAsync(HubPaths paths, CancellationToken cancellationToken = default)
    {
        var path = PendingPath(paths);
        if (!File.Exists(path)) return null;
        var pending = await new AtomicJsonStore(paths.DataRoot)
            .ReadAsync(path, HubJsonContext.Default.PendingCallback,
                () => null!, cancellationToken).ConfigureAwait(false);
        return pending is null || pending.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(pending.UserDataPath)
            ? null
            : pending;
    }

    public static Task WritePendingAsync(HubPaths paths, Guid profileId, string userDataPath, CancellationToken cancellationToken = default) =>
        new AtomicJsonStore(paths.DataRoot).WriteAsync(PendingPath(paths),
            new PendingCallback { ProfileId = profileId, UserDataPath = userDataPath, CreatedUtc = DateTimeOffset.UtcNow },
            HubJsonContext.Default.PendingCallback, cancellationToken);

    public static void ClearPending(HubPaths paths)
    {
        var path = PendingPath(paths);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Builds the command line that hands <paramref name="uri"/> to the Profile
    /// that is waiting for it. Split out so it can be tested without Windows.
    /// </summary>
    public static IReadOnlyList<string> BuildRelaunchArguments(string executable, string userDataPath, string uri) =>
        [executable, $"--user-data-dir={userDataPath}", uri];

    /// <summary>Hands a callback URI to the Profile that is waiting for it.</summary>
    /// <returns>The pid of the short-lived handoff process, or 0 when nothing is waiting.</returns>
    public static int Route(HubPaths paths, string uri)
    {
        var pending = ReadPendingAsync(paths).ConfigureAwait(false).GetAwaiter().GetResult();
        if (pending is null) return 0;
        var executable = Environment.ProcessPath
            ?? throw new HubException("Cannot locate the CLI executable required by the deep-link router.", "deeplink_router_path_unknown");
        var arguments = BuildRelaunchArguments(executable, pending.UserDataPath, uri);
        var info = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments.Skip(1)) info.ArgumentList.Add(argument);
        // Skip(1) drops the executable because ProcessStartInfo.FileName already carries it.
        return System.Diagnostics.Process.Start(info)?.Id ?? 0;
    }

    /// <summary>The command the scheme currently points at, or null when it is not registered.</summary>
    public static string? ReadRegisteredCommand()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// Points the scheme at the router, keeping the previous command in the data
    /// root so it can always be restored.
    /// </summary>
    public static void Register(HubPaths paths, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Deep-link registration requires Windows.");
        // Re-registering must not forget the handler that was there before us,
        // otherwise a second register would make restore a no-op.
        var previous = TryReadRegistration(paths)?.PreviousCommand ?? ReadRegisteredCommand();
        var record = new DeepLinkRegistration
        {
            PreviousCommand = previous,
            RouterCommand = RouterCommand(cliExecutable, paths.DataRoot),
            RegisteredUtc = DateTimeOffset.UtcNow
        };
        new AtomicJsonStore(paths.DataRoot).WriteAsync(RegistrationPath(paths), record,
            HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        WriteRegisteredCommand(record.RouterCommand);
    }

    /// <summary>Puts the scheme back the way it was found, if the router owns it.</summary>
    public static bool Restore(HubPaths paths)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var path = RegistrationPath(paths);
        if (!File.Exists(path)) return false;
        var record = new AtomicJsonStore(paths.DataRoot).ReadAsync(path, HubJsonContext.Default.DeepLinkRegistration,
            () => throw new ConfigurationException("The deep-link registration record is invalid.", "deeplink_registration_invalid"),
            CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        if (ReadRegisteredCommand() != record.RouterCommand) return false;
        if (record.PreviousCommand is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath, writable: true);
            // The unnamed (default) value of the key is addressed by an empty name.
            key?.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
        else
        {
            WriteRegisteredCommand(record.PreviousCommand);
        }
        File.Delete(path);
        return true;
    }

    /// <summary>True when the scheme points at this data root's router.</summary>
    public static bool IsRegisteredFor(HubPaths paths)
    {
        var record = TryReadRegistration(paths);
        return record is not null && string.Equals(ReadRegisteredCommand(), record.RouterCommand, StringComparison.Ordinal);
    }

    private static string RegistrationPath(HubPaths paths) =>
        Path.Combine(paths.GlobalLaunchStateRoot, "deeplink-registration.json");

    private static DeepLinkRegistration? TryReadRegistration(HubPaths paths)
    {
        var path = RegistrationPath(paths);
        if (!File.Exists(path)) return null;
        return new AtomicJsonStore(paths.DataRoot).ReadAsync(path, HubJsonContext.Default.DeepLinkRegistration,
            () => throw new ConfigurationException("The deep-link registration record is invalid.", "deeplink_registration_invalid"),
            CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private static void WriteRegisteredCommand(string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The registry requires Windows.");
        using var key = Registry.CurrentUser.CreateSubKey(CommandKeyPath);
        key.SetValue(null, command);
    }
}

public sealed record DeepLinkRegistration
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public string? PreviousCommand { get; init; }
    public string RouterCommand { get; init; } = string.Empty;
    public DateTimeOffset RegisteredUtc { get; init; }
}
