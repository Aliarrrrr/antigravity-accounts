using System.Diagnostics;
using System.Management;

namespace AGProfileHub.Core;

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

    private static Guid ProfileIdFromScope(string scope)
    {
        var name = scope.StartsWith("AGPH_", StringComparison.OrdinalIgnoreCase) ? scope[5..] : scope;
        return Guid.TryParseExact(name, "N", out var id) && id != Guid.Empty
            ? id
            : throw new ConfigurationException($"Scope '{scope}' is not a process-local scope name.", "isolation_scope_invalid");
    }

    /// <summary>
    /// A scope exists once its managed layout exists. Nothing is created here:
    /// the layout is owned by <see cref="HubPaths.EnsureProfileLayout"/>.
    /// </summary>
    public bool BoxExists(string scope) => Directory.Exists(Paths.Home(ProfileIdFromScope(scope)));

    public void CreateBox(string scope)
    {
        Paths.EnsureProfileLayout(ProfileIdFromScope(scope));
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
    public IReadOnlyList<int> ListPids(string scope)
    {
        var profileId = ProfileIdFromScope(scope);
        var expected = Paths.UserData(profileId);
        var processes = QueryProcesses();
        if (processes.Count == 0) return [];

        var roots = processes
            .Where(p => p.Name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
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

        return [.. inScope.Order()];
    }

    /// <summary>
    /// No rule has to be granted: with the operating system left alone the
    /// Profile's own directories are ordinary directories. Reporting the scope
    /// root keeps the write-through gate meaningful for both backends.
    /// </summary>
    public IReadOnlyList<string> GetIndexedSettingValues(string scope, string setting, int maximum = 64) =>
        [Paths.ProfileRoot(ProfileIdFromScope(scope))];

    public void Terminate(string scope)
    {
        foreach (var pid in ListPids(scope))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to kill; the caller's polling loop decides the outcome.
            }
        }
    }

    public int RunInBox(string scope, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, bool wait = false)
    {
        using var process = Start(executable, arguments, environment);
        if (!wait) return 0;
        process.WaitForExit();
        return process.ExitCode;
    }

    public int LaunchDetached(string scope, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        using var process = Start(executable, arguments, environment);
        return process.Id;
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        return Process.Start(info) ?? throw new HubException("Failed to start the scoped process.", "isolation_start_failed");
    }

    private sealed record ProcessRow(int ProcessId, int ParentProcessId, string Name, string? CommandLine);

    private static IReadOnlyList<ProcessRow> QueryProcesses()
    {
        // WMI process enumeration is Windows-only, and the analyzer requires the
        // guard to be visible in this method rather than at the call site.
        if (!OperatingSystem.IsWindows()) return [];

        var rows = new List<ProcessRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'Antigravity.exe' OR Name = 'language_server.exe'");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using var row = (ManagementObject)item;
                rows.Add(new ProcessRow(
                    Convert.ToInt32(row["ProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(row["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    row["Name"] as string ?? string.Empty,
                    row["CommandLine"] as string));
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
}
