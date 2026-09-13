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

    /// <summary>
    /// How long a pending callback stays routable. A launch writes the pending
    /// that lets the next <c>antigravity://</c> callback reach the profile that
    /// is signing in; sign-ins happen right after a launch, so a pending that
    /// is older than this has either completed or was abandoned — and an
    /// abandoned "waiting" grant must not keep handing URIs to that profile.
    /// </summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromMinutes(30);

    /// <summary>The registered command that routes a callback to the waiting Profile.</summary>
    public static string RouterCommand(string cliExecutable, string dataRoot) =>
        $"\"{cliExecutable}\" --data-root \"{dataRoot}\" callback \"%1\"";

    public static string PendingPath(HubPaths paths) =>
        Path.Combine(paths.DataRoot, "runtime", "deeplink-pending.json");

    public static async Task<PendingCallback?> ReadPendingAsync(HubPaths paths, CancellationToken cancellationToken = default)
    {
        var pending = await ReadPendingCoreAsync(paths, cancellationToken).ConfigureAwait(false);
        if (pending is null) return null;
        // A pending that predates this window is stale: route nothing to it.
        if (pending.CreatedUtc == default || DateTimeOffset.UtcNow - pending.CreatedUtc > MaxPendingAge)
        {
            ClearPending(paths);
            return null;
        }
        return pending;
    }

    /// <summary>
    /// The raw pending record regardless of age, for ownership decisions such
    /// as "may stopping this profile delete it". Routing must use
    /// <see cref="ReadPendingAsync"/>, which enforces the expiry.
    /// </summary>
    public static Task<PendingCallback?> ReadPendingWithoutExpiryAsync(HubPaths paths, CancellationToken cancellationToken = default) =>
        ReadPendingCoreAsync(paths, cancellationToken);

    private static async Task<PendingCallback?> ReadPendingCoreAsync(HubPaths paths, CancellationToken cancellationToken)
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
        ValidateCallbackUri(uri);
        var start = BuildHandoff(paths, uri);
        if (start is null) return 0;
        return System.Diagnostics.Process.Start(start)?.Id ?? 0;
    }

    /// <summary>The longest callback URI the router accepts.</summary>
    public const int MaxCallbackUriLength = 8192;

    /// <summary>
    /// Validates a callback URI before it can reach <see cref="Process.Start"/>.
    /// The OS handler quotes <c>%1</c>, so a webpage already cannot inject a
    /// second argument into the relaunched desktop; this closes the rest of the
    /// gap in our own hands: only the vendor scheme, a bounded length, and no
    /// control characters are ever forwarded.
    /// </summary>
    public static void ValidateCallbackUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            throw new HubException($"The deep-link callback must be an {Scheme}:// URI.", "deeplink_callback_invalid");
        if (uri.Length > MaxCallbackUriLength)
            throw new HubException($"The deep-link callback URI exceeds {MaxCallbackUriLength} characters.", "deeplink_callback_invalid");
        if (uri.Any(char.IsControl))
            throw new HubException("The deep-link callback URI contains control characters.", "deeplink_callback_invalid");
    }

    /// <summary>
    /// The handoff the router performs: relaunch the <em>vendor desktop</em> with the waiting
    /// Profile's user-data directory and the URI. Verified vendor behaviour does the rest: the
    /// new process hands the URI to the instance that already owns that directory and exits.
    /// Starting anything else (for example this CLI) drops the callback. Returns null when no
    /// Profile is waiting.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo? BuildHandoff(HubPaths paths, string uri)
    {
        var pending = ReadPendingAsync(paths).ConfigureAwait(false).GetAwaiter().GetResult();
        if (pending is null) return null;
        var executable = ResolveHandoffExecutable(paths);
        // The ProfileId selects the waiter; the path is always the managed
        // user-data directory for that id. A tampered pending cannot point the
        // handoff at another profile's tree or an arbitrary directory.
        var userDataPath = paths.UserData(pending.ProfileId);
        var info = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        info.ArgumentList.Add($"--user-data-dir={userDataPath}");
        info.ArgumentList.Add(uri);
        // The handoff process is the desktop itself, not a Node runtime: the
        // host that started the Hub may have set ELECTRON_RUN_AS_NODE=1.
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        return info;
    }

    /// <summary>
    /// Locates the vendor executable exactly like the driver does: the configured path first,
    /// then the standard install locations, and — like the driver — only when it still matches
    /// the configured digest. Read-only; the callback path must never create or mutate Hub state.
    /// </summary>
    private static string ResolveHandoffExecutable(HubPaths paths)
    {
        string? configured = null;
        string? pinnedSha256 = null;
        if (File.Exists(paths.SettingsFile))
        {
            var settings = new AtomicJsonStore(paths.DataRoot).ReadAsync(paths.SettingsFile,
                HubJsonContext.Default.HubSettings, () => new HubSettings(), CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            configured = settings.AntigravityExecutablePath;
            pinnedSha256 = settings.ExecutableSha256;
        }
        // The callback path relaunches a binary: an unpinned install is refused
        // the same way a mismatched pin is, so a webpage-triggered handoff cannot
        // start whatever replaced Antigravity.exe.
        ExecutableAttestation.RequirePinned(pinnedSha256);
        foreach (var candidate in AntigravityDriver.DiscoveryCandidates(configured))
        {
            try
            {
                var executable = PathSafety.CanonicalizeExistingFile(candidate);
                ExecutableAttestation.VerifyAsync(executable, pinnedSha256).ConfigureAwait(false).GetAwaiter().GetResult();
                return executable;
            }
            catch (ConfigurationException) { }
        }
        throw new HubException("Cannot locate the Antigravity executable required by the deep-link router.",
            "deeplink_router_target_unknown");
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
        try
        {
            WriteRegisteredCommand(record.RouterCommand);
        }
        catch
        {
            // A record that claims ownership while the scheme was never
            // switched would let EnsureRegistered flip the scheme later as if
            // the registration had succeeded. Remove the record instead.
            File.Delete(RegistrationPath(paths));
            throw;
        }
    }

    /// <summary>
    /// Re-asserts the router registration after a vendor desktop has run: the
    /// desktop rewrites the scheme back to itself on every start, which would
    /// otherwise silently undo the router between sign-ins. Never touches the
    /// scheme when this data root never registered.
    /// </summary>
    public static void EnsureRegistered(HubPaths paths)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = RegistrationPath(paths);
        if (!File.Exists(path)) return;
        var record = ReadRegistration(paths);
        // Rebuild the command from this data root and a CLI-shaped executable
        // instead of replaying the stored string: a tampered record must not be
        // able to point the scheme at an arbitrary program.
        var expected = CanonicalizeRouterCommand(paths, record.RouterCommand);
        if (ReadRegisteredCommand() != expected)
            WriteRegisteredCommand(expected);
        // Keep the ownership record on the same string the registry holds so
        // Restore / IsRegisteredFor compare like with like after a re-assert.
        if (!string.Equals(record.RouterCommand, expected, StringComparison.Ordinal))
        {
            new AtomicJsonStore(paths.DataRoot).WriteAsync(path, record with { RouterCommand = expected },
                HubJsonContext.Default.DeepLinkRegistration, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Rebuilds the only command this data root is allowed to re-assert.
    /// The executable must look like the Hub CLI and still exist; the data-root
    /// argument is always <paramref name="paths"/>.
    /// </summary>
    public static string CanonicalizeRouterCommand(HubPaths paths, string storedCommand)
    {
        var cli = TryExtractCliExecutable(storedCommand)
            ?? throw new HubException(
                "The deep-link registration record does not name this Hub CLI; re-run configure-deeplink register.",
                "deeplink_registration_invalid");
        var fileName = Path.GetFileName(cli);
        if (!fileName.Equals("AGProfileHub.Cli.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("AGProfileHub.Cli", StringComparison.OrdinalIgnoreCase))
            throw new HubException(
                "The deep-link registration record does not name this Hub CLI; re-run configure-deeplink register.",
                "deeplink_registration_invalid");
        cli = PathSafety.CanonicalizeExistingFile(cli);
        return RouterCommand(cli, paths.DataRoot);
    }

    private static string? TryExtractCliExecutable(string storedCommand)
    {
        if (string.IsNullOrWhiteSpace(storedCommand)) return null;
        var trimmed = storedCommand.TrimStart();
        if (!trimmed.StartsWith('"')) return null;
        var end = trimmed.IndexOf('"', 1);
        if (end <= 1) return null;
        var candidate = trimmed[1..end];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
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
        Path.Combine(paths.DataRoot, "runtime", "deeplink-registration.json");

    private static DeepLinkRegistration? TryReadRegistration(HubPaths paths)
    {
        var path = RegistrationPath(paths);
        if (!File.Exists(path)) return null;
        return ReadRegistration(paths);
    }

    private static DeepLinkRegistration ReadRegistration(HubPaths paths) =>
        new AtomicJsonStore(paths.DataRoot).ReadAsync(RegistrationPath(paths), HubJsonContext.Default.DeepLinkRegistration,
            () => throw new ConfigurationException("The deep-link registration record is invalid.", "deeplink_registration_invalid"),
            CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

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
