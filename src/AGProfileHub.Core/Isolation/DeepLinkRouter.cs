using Microsoft.Win32;

namespace AGProfileHub.Core;

/// <summary>Describes one Profile that is currently allowed to receive a deep-link callback.</summary>
public sealed record PendingCallback
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Guid ProfileId { get; init; }
    public string UserDataPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string? ExpectedState { get; init; }
}

/// <summary>
/// Multi-slot pending map. An older single-callback document (profileId at the
/// root) is migrated into a one-entry map on first write.
/// </summary>
public sealed record PendingCallbackMap
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Dictionary<Guid, PendingCallback> Entries { get; init; } = [];
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

    /// <summary>How many Profiles may wait for a callback at once.</summary>
    public const int MaxPendingEntries = 4;

    /// <summary>The registered command that routes a callback to the waiting Profile.</summary>
    public static string RouterCommand(string cliExecutable, string dataRoot) =>
        $"\"{cliExecutable}\" --data-root \"{dataRoot}\" callback \"%1\"";

    public static string PendingPath(HubPaths paths) =>
        Path.Combine(paths.DataRoot, "runtime", "deeplink-pending.json");

    /// <summary>Live (non-expired) pending entries, oldest-expired dropped and rewritten.</summary>
    public static async Task<IReadOnlyList<PendingCallback>> ReadLivePendingAsync(HubPaths paths, CancellationToken cancellationToken = default)
    {
        var map = await ReadMapAsync(paths, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var live = map.Values
            .Where(p => p.CreatedUtc != default && now - p.CreatedUtc <= MaxPendingAge)
            .OrderBy(p => p.CreatedUtc)
            .ToList();
        if (live.Count != map.Count)
            await WriteMapAsync(paths, ToMap(live), cancellationToken).ConfigureAwait(false);
        return live;
    }

    /// <summary>The single waiter, or null when none. Prefer the multi-slot readers for routing.</summary>
    public static async Task<PendingCallback?> ReadPendingAsync(HubPaths paths, CancellationToken cancellationToken = default)
    {
        var live = await ReadLivePendingAsync(paths, cancellationToken).ConfigureAwait(false);
        return live.Count == 1 ? live[0] : null;
    }

    /// <summary>Raw map regardless of age, for ownership decisions such as "may stopping this profile delete it".</summary>
    public static async Task<IReadOnlyDictionary<Guid, PendingCallback>> ReadPendingWithoutExpiryAsync(HubPaths paths, CancellationToken cancellationToken = default) =>
        await ReadMapAsync(paths, cancellationToken).ConfigureAwait(false);

    public static async Task WritePendingAsync(HubPaths paths, Guid profileId, string userDataPath,
        CancellationToken cancellationToken = default, string? expectedState = null)
    {
        if (profileId == Guid.Empty) throw new ConfigurationException("Pending callback profile id cannot be empty.", "deeplink_pending_invalid");
        if (string.IsNullOrWhiteSpace(userDataPath)) throw new ConfigurationException("Pending callback user-data path cannot be empty.", "deeplink_pending_invalid");
        var map = await ReadMapAsync(paths, cancellationToken).ConfigureAwait(false);
        map[profileId] = new PendingCallback
        {
            ProfileId = profileId,
            UserDataPath = userDataPath,
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpectedState = expectedState
        };
        if (map.Count > MaxPendingEntries)
            throw new HubException($"At most {MaxPendingEntries} profiles may wait for a deep-link callback.", "deeplink_pending_full");
        await WriteMapAsync(paths, map, cancellationToken).ConfigureAwait(false);
    }

    public static void ClearPending(HubPaths paths, Guid profileId)
    {
        var path = PendingPath(paths);
        if (!File.Exists(path)) return;
        try
        {
            var map = ReadMapAsync(paths, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!map.Remove(profileId)) return;
            if (map.Count == 0)
            {
                File.Delete(path);
                return;
            }
            WriteMapAsync(paths, map, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail closed on the next read; do not delete another profile's slot.
        }
    }

    /// <summary>Clears every waiter. Only used when retiring the whole pending file.</summary>
    public static void ClearPending(HubPaths paths)
    {
        var path = PendingPath(paths);
        if (File.Exists(path)) File.Delete(path);
    }

    private static PendingCallbackMap ToMap(IEnumerable<PendingCallback> entries)
    {
        var map = new PendingCallbackMap();
        foreach (var entry in entries) map.Entries[entry.ProfileId] = entry;
        return map;
    }

    private static async Task<Dictionary<Guid, PendingCallback>> ReadMapAsync(HubPaths paths, CancellationToken cancellationToken)
    {
        var path = PendingPath(paths);
        if (!File.Exists(path)) return [];
        // Peek the shape first: older builds wrote a single PendingCallback at the root.
        using (var stream = File.OpenRead(path))
        using (var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                schemaElement.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schema))
                throw new ConfigurationException($"Configuration '{path}' has no valid schemaVersion.", "schema_missing");
            if (schema > SchemaVersions.Current)
                throw new ConfigurationException($"Configuration schema {schema} is newer than supported schema {SchemaVersions.Current}.", "future_schema");
            var hasEntries = document.RootElement.TryGetProperty("entries", out _);
            var hasLegacyProfile = document.RootElement.TryGetProperty("profileId", out _);
            if (!hasEntries && hasLegacyProfile)
            {
                var legacy = System.Text.Json.JsonSerializer.Deserialize(document.RootElement, HubJsonContext.Default.PendingCallback);
                if (legacy is null || legacy.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(legacy.UserDataPath)) return [];
                return new Dictionary<Guid, PendingCallback> { [legacy.ProfileId] = legacy };
            }
        }

        var map = await new AtomicJsonStore(paths.DataRoot)
            .ReadAsync(path, HubJsonContext.Default.PendingCallbackMap,
                () => new PendingCallbackMap(), cancellationToken).ConfigureAwait(false);
        return map.Entries
            .Where(kv => kv.Key != Guid.Empty && !string.IsNullOrWhiteSpace(kv.Value.UserDataPath))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Task WriteMapAsync(HubPaths paths, Dictionary<Guid, PendingCallback> entries, CancellationToken cancellationToken) =>
        WriteMapAsync(paths, ToMap(entries.Values), cancellationToken);

    private static async Task WriteMapAsync(HubPaths paths, PendingCallbackMap map, CancellationToken cancellationToken)
    {
        var path = PendingPath(paths);
        if (map.Entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        await new AtomicJsonStore(paths.DataRoot).WriteAsync(path, map,
            HubJsonContext.Default.PendingCallbackMap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the waiter for a callback. One live waiter always wins; two or more
    /// require a matching recorded OAuth state, otherwise fail closed.
    /// </summary>
    public static PendingCallback SelectPending(IReadOnlyList<PendingCallback> live, string uri)
    {
        if (live.Count == 0) throw new HubException("No profile is waiting for a deep-link callback.", "deeplink_pending_missing");
        if (live.Count == 1) return live[0];
        var state = TryExtractState(uri);
        if (state is not null)
        {
            var matched = live.Where(p => p.ExpectedState is not null &&
                string.Equals(p.ExpectedState, state, StringComparison.Ordinal)).ToList();
            if (matched.Count == 1) return matched[0];
        }
        throw new HubException(
            $"{live.Count} profiles are waiting for a deep-link callback and the URI cannot be attributed. Finish one sign-in before starting the next.",
            "deeplink_pending_ambiguous");
    }

    /// <summary>Extracts the OAuth <c>state</c> query/fragment parameter without logging the URI.</summary>
    public static string? TryExtractState(string uri)
    {
        try
        {
            var question = uri.IndexOf('?');
            var hash = uri.IndexOf('#');
            var queryStart = question >= 0 ? question + 1 : -1;
            var queryEnd = hash > question ? hash : uri.Length;
            if (queryStart >= 0 && queryStart < queryEnd)
            {
                var state = FindParam(uri[queryStart..queryEnd], "state");
                if (state is not null) return state;
            }
            if (hash >= 0 && hash + 1 < uri.Length)
                return FindParam(uri[(hash + 1)..], "state");
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? FindParam(string segment, string name)
    {
        foreach (var part in segment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!part[..eq].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = part[(eq + 1)..];
            return Uri.UnescapeDataString(value);
        }
        return null;
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
        var live = ReadLivePendingAsync(paths).ConfigureAwait(false).GetAwaiter().GetResult();
        if (live.Count == 0) return null;
        var pending = SelectPending(live, uri);
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
