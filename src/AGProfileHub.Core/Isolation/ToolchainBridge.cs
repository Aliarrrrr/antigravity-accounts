using System.Diagnostics;
using System.Text.Json;

namespace AGProfileHub.Core;

/// <summary>Marker written beside managed toolchain bridges under a Profile home.</summary>
public sealed record ToolchainBridgeMarker
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public List<ToolchainBridgeMarkerEntry> Entries { get; init; } = [];
}

public sealed record ToolchainBridgeMarkerEntry
{
    public string Name { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string Kind { get; init; } = "junction";
    public DateTimeOffset CreatedUtc { get; init; }
}

public enum ToolchainBridgeEntryState { Ready, HostMissing, Broken, Absent }

public sealed record ToolchainBridgeEntryStatus(string Name, string? LinkPath, string? TargetPath,
    ToolchainBridgeEntryState State, string Kind);

public sealed record ToolchainBridgeStatus(Guid ProfileId, bool Enabled, IReadOnlyList<ToolchainBridgeEntryStatus> Entries);

/// <summary>
/// Links a Profile home to the Windows user's real tool config (SSH, Git) so
/// Agent tooling behaves like native Antigravity. Only Hub-marked reparse
/// points under the Profile home are allowed; removal deletes the link, never
/// the host target.
/// </summary>
public static class ToolchainBridge
{
    public const string MarkerFileName = ".agph-bridge.json";

    /// <summary>Default host-relative entries. Kept small on purpose.</summary>
    public static readonly IReadOnlyList<string> DefaultEntries = [".ssh", ".gitconfig"];

    private static readonly string[] AllowedNames = [".ssh", ".gitconfig", ".git-credentials"];

    public static string MarkerPath(HubPaths paths, Guid profileId) =>
        Path.Combine(paths.Home(profileId), MarkerFileName);

    /// <summary>
    /// True when <paramref name="path"/> is a reparse point this Hub created as
    /// a toolchain bridge (the link itself, or anything under it).
    /// </summary>
    public static bool IsManagedBridgePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return false;
            var parent = Path.GetDirectoryName(full);
            if (parent is null) return false;
            var markerPath = Path.Combine(parent, MarkerFileName);
            if (File.Exists(markerPath)) return MarkerNames(markerPath).Contains(name, StringComparer.OrdinalIgnoreCase);
            // Paths under a bridge directory (home/.ssh/id_rsa): parent chain includes the link name.
            var cursor = parent;
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(cursor); i++)
            {
                var parentName = Path.GetFileName(cursor.TrimEnd(Path.DirectorySeparatorChar));
                var parentParent = Path.GetDirectoryName(cursor);
                if (parentParent is null) break;
                var parentMarker = Path.Combine(parentParent, MarkerFileName);
                if (File.Exists(parentMarker) &&
                    MarkerNames(parentMarker).Contains(parentName, StringComparer.OrdinalIgnoreCase) &&
                    IsReparse(cursor))
                    return true;
                cursor = parentParent;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<ToolchainBridgeStatus> PrepareAsync(HubPaths paths, Guid profileId,
        ToolchainBridgeSettings settings, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new UnsupportedCapabilityException("Toolchain bridge requires Windows junctions.");
        if (!settings.Enabled) return await StatusAsync(paths, profileId, settings, cancellationToken).ConfigureAwait(false);

        var entries = NormalizeEntries(settings);
        paths.EnsureProfileLayout(profileId);
        var home = paths.Home(profileId);
        var hostHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var marker = await ReadMarkerAsync(paths, profileId, cancellationToken).ConfigureAwait(false);
        var byName = marker.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = Path.Combine(home, name);
            var target = Path.Combine(hostHome, name);
            bool targetExists;
            try
            {
                targetExists = Directory.Exists(target) || File.Exists(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                byName.Remove(name);
                continue;
            }
            if (!targetExists)
            {
                byName.Remove(name);
                continue;
            }
            if (File.Exists(link) || Directory.Exists(link))
            {
                // Only adopt a pre-existing link when the marker already claims
                // it AND it still points at the host path. An unmarked reparse
                // must never be trusted as ours — that would let PathSafety
                // treat a foreign junction as a managed bridge.
                if (byName.TryGetValue(name, out var existing) &&
                    string.Equals(existing.TargetPath, target, StringComparison.OrdinalIgnoreCase) &&
                    (IsReparse(link) || IsBridgeHardlink(link, target)))
                {
                    byName[name] = existing with
                    {
                        Kind = existing.Kind,
                        CreatedUtc = existing.CreatedUtc == default ? DateTimeOffset.UtcNow : existing.CreatedUtc
                    };
                    continue;
                }
                if (IsReparse(link))
                    throw new HubException($"Cannot bridge '{name}': an unmarked reparse point already exists at {link}.", "toolchain_bridge_conflict");
                if (File.Exists(link) || Directory.Exists(link))
                    throw new HubException($"Cannot bridge '{name}': a non-link item already exists at {link}.", "toolchain_bridge_conflict");
            }

            if (Directory.Exists(target))
            {
                var start = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link);
                start.ArgumentList.Add(target);
                using var process = Process.Start(start);
                if (process is null) throw new HubException($"Failed to start mklink for '{name}'.", "toolchain_bridge_failed");
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0 || !Directory.Exists(link))
                    throw new HubException($"mklink /J failed for '{name}' (exit {process.ExitCode}).", "toolchain_bridge_failed");
                byName[name] = new ToolchainBridgeMarkerEntry { Name = name, TargetPath = target, Kind = "junction", CreatedUtc = DateTimeOffset.UtcNow };
            }
            else
            {
                // File hardlinks need a Win32 call; a same-volume copy is an
                // acceptable fallback for .gitconfig-sized files and keeps the
                // link removable without touching the host target.
                if (TryCreateHardLink(link, target))
                    byName[name] = new ToolchainBridgeMarkerEntry { Name = name, TargetPath = target, Kind = "hardlink", CreatedUtc = DateTimeOffset.UtcNow };
                else
                {
                    File.Copy(target, link, overwrite: false);
                    byName[name] = new ToolchainBridgeMarkerEntry { Name = name, TargetPath = target, Kind = "copy", CreatedUtc = DateTimeOffset.UtcNow };
                }
            }
        }

        // Drop marker rows for configured names that no longer apply.
        foreach (var stale in byName.Keys.Where(k => !entries.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            byName.Remove(stale);

        var updated = new ToolchainBridgeMarker { Entries = byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList() };
        await new AtomicJsonStore(paths.DataRoot).WriteAsync(MarkerPath(paths, profileId), updated,
            HubJsonContext.Default.ToolchainBridgeMarker, cancellationToken).ConfigureAwait(false);
        return await StatusAsync(paths, profileId, settings, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ToolchainBridgeStatus> StatusAsync(HubPaths paths, Guid profileId,
        ToolchainBridgeSettings settings, CancellationToken cancellationToken = default)
    {
        var entries = NormalizeEntries(settings);
        var marker = await ReadMarkerAsync(paths, profileId, cancellationToken).ConfigureAwait(false);
        var byName = marker.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var home = paths.Home(profileId);
        var list = new List<ToolchainBridgeEntryStatus>(entries.Count);
        foreach (var name in entries)
        {
            var link = Path.Combine(home, name);
            byName.TryGetValue(name, out var marked);
            ToolchainBridgeEntryState state;
            if (marked is null && !File.Exists(link) && !Directory.Exists(link)) state = ToolchainBridgeEntryState.Absent;
            else if (marked is null) state = ToolchainBridgeEntryState.Broken;
            else if (!File.Exists(marked.TargetPath) && !Directory.Exists(marked.TargetPath)) state = ToolchainBridgeEntryState.HostMissing;
            else if (Directory.Exists(link) || File.Exists(link)) state = ToolchainBridgeEntryState.Ready;
            else state = ToolchainBridgeEntryState.Broken;
            list.Add(new ToolchainBridgeEntryStatus(name,
                File.Exists(link) || Directory.Exists(link) ? link : null,
                marked?.TargetPath, state, marked?.Kind ?? (Directory.Exists(link) ? "junction" : "unknown")));
        }
        return new ToolchainBridgeStatus(profileId, settings.Enabled, list);
    }

    public static async Task RemoveAsync(HubPaths paths, Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new UnsupportedCapabilityException("Toolchain bridge requires Windows.");
        var marker = await ReadMarkerAsync(paths, profileId, cancellationToken).ConfigureAwait(false);
        foreach (var entry in marker.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = Path.Combine(paths.Home(profileId), entry.Name);
            await RemoveLinkOnlyAsync(link, cancellationToken).ConfigureAwait(false);
        }
        var markerPath = MarkerPath(paths, profileId);
        if (File.Exists(markerPath)) File.Delete(markerPath);
    }

    private static async Task RemoveLinkOnlyAsync(string link, CancellationToken cancellationToken)
    {
        if (Directory.Exists(link) && IsReparse(link))
        {
            // Directory.Delete on a junction removes the link; never recurse into the target.
            Directory.Delete(link, recursive: false);
            return;
        }
        if (File.Exists(link))
        {
            File.Delete(link);
            return;
        }
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static List<string> NormalizeEntries(ToolchainBridgeSettings settings)
    {
        var source = settings.Entries.Count > 0 ? settings.Entries : DefaultEntries.ToList();
        var result = new List<string>(source.Count);
        foreach (var raw in source)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..") || !AllowedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ConfigurationException($"Toolchain bridge entry '{raw}' is not on the allowlist.", "toolchain_bridge_entry_invalid");
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        return result;
    }

    private static async Task<ToolchainBridgeMarker> ReadMarkerAsync(HubPaths paths, Guid profileId, CancellationToken cancellationToken)
    {
        var path = MarkerPath(paths, profileId);
        if (!File.Exists(path)) return new ToolchainBridgeMarker();
        return await new AtomicJsonStore(paths.DataRoot).ReadAsync(path, HubJsonContext.Default.ToolchainBridgeMarker,
            () => new ToolchainBridgeMarker(), cancellationToken).ConfigureAwait(false);
    }

    private static HashSet<string> MarkerNames(string markerPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return [];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries.EnumerateArray())
                if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    names.Add(name.GetString()!);
            return names;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    private static bool TryCreateHardLink(string link, string target)
    {
        try { return CreateHardLink(link, target, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static bool IsBridgeHardlink(string link, string target)
    {
        try
        {
            if (!File.Exists(link) || !File.Exists(target)) return false;
            return new FileInfo(link).Length == new FileInfo(target).Length && File.GetLastWriteTimeUtc(link) == File.GetLastWriteTimeUtc(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
