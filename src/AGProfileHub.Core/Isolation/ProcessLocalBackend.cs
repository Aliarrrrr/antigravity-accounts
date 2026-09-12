using System.Diagnostics;
using System.Management;

namespace AGProfileHub.Core;

/// <summary>Marker that a Profile's process-local scope has been prepared.</summary>
public sealed record IsolationScopeMarker
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public string ScopeName { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// Isolation that gives each Profile its own identity without virtualising the
/// operating system, so no sandbox product is required.
/// </summary>
/// <remarks>
/// <para>
/// A Profile's "scope" is its own managed data root. The vendor language server
/// keeps its OAuth token in a per-user file, and the desktop already resolves
/// that file from the process home, so pointing <c>USERPROFILE</c> at the scoped
/// home is enough to keep instances apart - see
/// <see cref="AntigravityDriver.BuildLaunchPlan"/>, which also asks the language
/// server to prefer its file store over the shared Windows credential entry.
/// </para>
/// <para>
/// Scope names reuse the box naming so one state file and one code path can
/// describe both backends.
/// </para>
/// </remarks>
public sealed class ProcessLocalBackend(HubPaths paths) : IIsolationBackend
{
    public HubPaths Paths { get; } = paths;

    /// <summary>Scope name for a Profile, in the same shape the sandbox backend uses.</summary>
    public static string ScopeNameFor(Guid profileId) => "AGPH_" + profileId.ToString("N");

    /// <summary>
    /// The marker that says "this Profile's scope has been prepared". It cannot
    /// be the home directory: the profile layout already exists the moment the
    /// Profile is created, so a directory-based check would make every fresh
    /// scope look like a pre-existing, unowned one.
    /// </summary>
    public static string ScopeMarkerPath(HubPaths paths, Guid profileId) =>
        Path.Combine(paths.ProfileRoot(profileId), "scope.json");

    private static Guid ProfileIdFromScope(string scope)
    {
        var name = scope.StartsWith("AGPH_", StringComparison.OrdinalIgnoreCase) ? scope[5..] : scope;
        return Guid.TryParseExact(name, "N", out var id) && id != Guid.Empty
            ? id
            : throw new ConfigurationException($"Scope '{scope}' is not a process-local scope name.", "isolation_scope_invalid");
    }

    /// <summary>A scope exists once <see cref="CreateBox"/> has written its marker.</summary>
    public bool BoxExists(string scope) => File.Exists(ScopeMarkerPath(Paths, ProfileIdFromScope(scope)));

    public void CreateBox(string scope)
    {
        var profileId = ProfileIdFromScope(scope);
        Paths.EnsureProfileLayout(profileId);
        new AtomicJsonStore(Paths.DataRoot).WriteAsync(ScopeMarkerPath(Paths, profileId),
            new IsolationScopeMarker { ScopeName = scope, CreatedUtc = DateTimeOffset.UtcNow },
            HubJsonContext.Default.IsolationScopeMarker, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (!BoxExists(scope)) throw new HubException("The scoped profile layout could not be created.", "isolation_scope_create_failed");
    }

    /// <summary>
    /// Processes belonging to this scope: the desktop roots launched with this
    /// Profile's user-data directory, plus everything they spawned.
    /// </summary>
    /// <remarks>
    /// The scope is matched through <c>--user-data-dir</c> rather than through
    /// the environment because WMI does not expose the environment block. A
    /// process that was started by a deep link without that argument cannot be
    /// attributed this way; the callback router makes sure such launches do not
    /// happen, and the absence is reported rather than guessed.
    /// </remarks>
    public IReadOnlyList<int> ListPids(string scope) =>
        [.. ListProcesses(scope).Select(row => row.ProcessId).Order()];

    private IReadOnlyList<ProcessRow> ListProcesses(string scope)
    {
        var profileId = ProfileIdFromScope(scope);
        var expected = Paths.UserData(profileId);
        var processes = QueryProcesses();
        if (processes.Count == 0) return [];

        var roots = processes
            // Win32_Process.Name carries the extension; Process.ProcessName strips
            // it, so a check against "Antigravity" here would never match.
            .Where(p => p.Name.Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.CommandLine?.Contains($"--user-data-dir=\"{expected}\"", StringComparison.OrdinalIgnoreCase) == true ||
                        p.CommandLine?.Contains($"--user-data-dir={expected}", StringComparison.OrdinalIgnoreCase) == true)
            .Select(p => p.ProcessId)
            .ToHashSet();

        var inScope = new HashSet<int>(roots);
        bool grew;
        do
        {
            grew = false;
            foreach (var process in processes)
            {
                if (inScope.Contains(process.ProcessId) || !inScope.Contains(process.ParentProcessId)) continue;
                inScope.Add(process.ProcessId);
                grew = true;
            }
        }
        while (grew);

        return [.. processes.Where(p => inScope.Contains(p.ProcessId))];
    }

    public void Terminate(string scope)
    {
        foreach (var row in ListProcesses(scope))
        {
            try
            {
                using var process = Process.GetProcessById(row.ProcessId);
                // A reused PID belongs to someone else's process tree; kill only
                // what still matches the snapshot's creation time. Unverifiable
                // processes are left running and the caller's polling loop
                // reports the scope as not stopped instead of killing blindly.
                if (!StartedSameProcess(process, row)) continue;
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to kill; the caller's polling loop decides the outcome.
            }
        }
    }

    private static bool StartedSameProcess(Process process, ProcessRow row)
    {
        if (row.StartedUtc == DateTimeOffset.MinValue) return false;
        try
        {
            return Math.Abs((new DateTimeOffset(process.StartTime.ToUniversalTime()) - row.StartedUtc).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public int LaunchDetached(string scope, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        // The Hub and CLI are frequently started from an Electron host. Anything
        // they launch directly lands in that host's job object, which kills the
        // vendor desktop's Chromium children and forbids breaking away. Routing
        // the start through the shell (explorer.exe) with a small wrapper moves
        // the desktop into the interactive session, which is the only context in
        // which the vendor desktop survives.
        var wrapper = WriteLaunchWrapper(scope, executable, arguments, environment);
        var info = new ProcessStartInfo("explorer.exe", $"\"{wrapper}\"") { UseShellExecute = false };
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        var process = Process.Start(info)
            ?? throw new HubException("Failed to start the isolation launcher.", "isolation_start_failed");
        return process.Id;
    }

    private string WriteLaunchWrapper(string scope, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var profileId = ProfileIdFromScope(scope);
        var wrapper = Path.Combine(Paths.ProfileRoot(profileId), "launch.cmd");
        // UTF-8 without BOM plus an early chcp: paths may contain non-ASCII
        // (for example a Chinese Windows user name), which ASCII encoding
        // would silently corrupt into '?'.
        File.WriteAllText(wrapper, BuildLaunchWrapperContent(environment, executable, arguments),
            new System.Text.UTF8Encoding(false));
        return wrapper;
    }

    /// <summary>
    /// The wrapper script body. Pure so the encoding and escaping contract can
    /// be tested without launching anything.
    /// </summary>
    public static string BuildLaunchWrapperContent(IReadOnlyDictionary<string, string> environment,
        string executable, IReadOnlyList<string> arguments)
    {
        var lines = new List<string> { "@echo off", "@chcp 65001>nul" };
        // The shell does not reliably hand a custom environment block down to the
        // wrapper's child, so the wrapper sets the instance's home itself. This
        // mirrors the exact wrapper that was proven against the real desktop.
        foreach (var pair in environment)
        {
            if (string.Equals(pair.Key, "ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"set \"{pair.Key}={EscapeBatch(pair.Value)}\"");
        }
        lines.Add("set \"ELECTRON_RUN_AS_NODE=\"");
        var quoted = string.Join(" ", arguments.Select(argument => $"\"{EscapeBatch(argument)}\""));
        lines.Add($"start \"\" \"{EscapeBatch(executable)}\" {quoted}");
        return string.Join("\r\n", lines) + "\r\n";
    }

    // cmd expands %VAR% even inside quoted set statements; %% is the batch-file
    // escape for a literal percent sign.
    private static string EscapeBatch(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    private sealed record ProcessRow(int ProcessId, int ParentProcessId, string Name, string? CommandLine, DateTimeOffset StartedUtc);

    private static IReadOnlyList<ProcessRow> QueryProcesses()
    {
        // WMI process enumeration is Windows-only, and the analyzer requires the
        // guard to be visible in this method rather than at the call site.
        if (!OperatingSystem.IsWindows()) return [];

        var rows = new List<ProcessRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process WHERE Name = 'Antigravity.exe' OR Name = 'language_server.exe'");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using var row = (ManagementObject)item;
                rows.Add(new ProcessRow(
                    Convert.ToInt32(row["ProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(row["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    row["Name"] as string ?? string.Empty,
                    row["CommandLine"] as string,
                    ParseCreationDate(row["CreationDate"] as string)));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Without a usable process list the caller must not conclude
            // "nothing is running"; an empty list is safe here because every
            // caller either starts something or reports absence.
            return [];
        }
        return rows;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DateTimeOffset ParseCreationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.MinValue;
        try { return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime(); }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }
}
