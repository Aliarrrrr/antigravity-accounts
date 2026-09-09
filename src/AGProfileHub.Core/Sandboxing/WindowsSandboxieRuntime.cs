using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace AGProfileHub.Core;

/// <summary>
/// Sandboxie Plus adapter using Start.exe /box: and SbieIni.exe.
/// Launch success only means Start.exe accepted the request; callers must
/// re-discover the vendor process from live process snapshots.
/// </summary>
public sealed class WindowsSandboxieRuntime(string? sandboxieHomeOverride = null) : ISandboxRuntime
{
    public static readonly string[] DefaultSearchRoots =
    [
        @"C:\Program Files\Sandboxie-Plus",
        @"C:\Program Files\Sandboxie"
    ];

    public string? Home => ResolveHome();

    public bool IsAvailable
    {
        get
        {
            var home = Home;
            return home is not null && File.Exists(StartExe(home)) && File.Exists(SbieIniExe(home));
        }
    }

    public string RuntimeDescription
    {
        get
        {
            var home = Home;
            return home is null
                ? "Sandboxie-Plus was not found in the default install locations."
                : $"Sandboxie-Plus at {home}";
        }
    }

    public Task EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sandboxie isolation requires Windows.");
        ValidateBoxId(boxId);
        var home = RequireHome();
        cancellationToken.ThrowIfCancellationRequested();
        SetBox(home, boxId, "Enabled", "y");
        SetBox(home, boxId, "ConfigLevel", "10");
        SetBox(home, boxId, "AutoRecover", "n");
        SetBox(home, boxId, "AutoDelete", "n");
        SetBox(home, boxId, "BoxNameTitle", "y");
        SetBox(home, boxId, "OpenCredentials", "n");
        // Precise write-through for managed profile directories only. Never open
        // the user profile root, credential stores, or arbitrary AppData.
        var openIndex = 1;
        foreach (var path in NormalizeOpenPaths(openFilePaths))
        {
            SetBox(home, boxId, $"OpenFilePath.{openIndex}", path);
            openIndex++;
        }
        return Task.CompletedTask;
    }

    public Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default)
    {
        var home = RequireHome();
        var (exitCode, _) = RunStart(home, boxId, executablePath, arguments, environment, wait: false, cancellationToken);
        if (exitCode != 0)
            throw new LaunchNotStartedException($"Sandboxie refused to start the process (exit code {exitCode}).");
        return Task.CompletedTask;
    }

    public async Task RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var home = RequireHome();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var (exitCode, _) = await Task.Run(() => RunStart(home, boxId, executablePath, arguments, environment, wait: true, timeoutCts.Token), timeoutCts.Token)
            .ConfigureAwait(false);
        if (exitCode != 0)
            throw new HubException($"In-box command failed with exit code {exitCode}.", "sandbox_command_failed");
    }

    public Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sandboxie isolation requires Windows.");
        ValidateBoxId(boxId);
        var home = RequireHome();
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = StartExe(home),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add($"/box:{boxId}");
        psi.ArgumentList.Add("/silent");
        psi.ArgumentList.Add("/listpids");
        using var process = Process.Start(psi) ?? throw new HubException("Sandboxie listpids failed to start.", "sandbox_unavailable");
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        var snapshots = new List<SandboxRuntimeSnapshot>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(line, out var pid) && pid > 0)
            {
                string? name = null;
                string? path = null;
                try
                {
                    using var candidate = Process.GetProcessById(pid);
                    name = candidate.ProcessName;
                    path = candidate.MainModule?.FileName;
                }
                catch { }
                snapshots.Add(new SandboxRuntimeSnapshot(pid, name ?? "unknown", path));
            }
        }
        return Task.FromResult<IReadOnlyList<SandboxRuntimeSnapshot>>(snapshots);
    }

    public Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sandboxie isolation requires Windows.");
        ValidateBoxId(boxId);
        var home = RequireHome();
        cancellationToken.ThrowIfCancellationRequested();
        RunStartRaw(home, [ $"/box:{boxId}", "/silent", "/terminate" ]);
        return Task.CompletedTask;
    }

    public static void ValidateBoxId(string boxId)
    {
        if (string.IsNullOrWhiteSpace(boxId) || boxId.Length is < 3 or > 64)
            throw new ConfigurationException("Sandbox box id must be 3-64 characters.", "invalid_box_id");
        foreach (var ch in boxId)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
                throw new ConfigurationException("Sandbox box id may only contain letters, digits, underscore and hyphen.", "invalid_box_id");
        }
    }

    internal static string DefaultBoxId(Guid profileId) => "AGPH_" + profileId.ToString("N");

    private string RequireHome()
    {
        var home = Home;
        if (home is null || !File.Exists(StartExe(home)) || !File.Exists(SbieIniExe(home)))
            throw new UnsupportedCapabilityException("Sandboxie-Plus was not found. Install Sandboxie-Plus 1.18.2+ or use the Direct backend.");
        return home;
    }

    private string? ResolveHome()
    {
        if (!string.IsNullOrWhiteSpace(sandboxieHomeOverride) && Directory.Exists(sandboxieHomeOverride))
            return Path.GetFullPath(sandboxieHomeOverride);
        foreach (var candidate in DefaultSearchRoots)
        {
            if (Directory.Exists(candidate) && File.Exists(StartExe(candidate))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string StartExe(string home) => Path.Combine(home, "Start.exe");
    private static string SbieIniExe(string home) => Path.Combine(home, "SbieIni.exe");

    private static void SetBox(string home, string boxId, string setting, string value)
    {
        RunStartRaw(home, [ "set", boxId, setting, value ], throwOnError: true);
        var actual = RunStartRaw(home, [ "query", boxId, setting ], throwOnError: true);
        var trimmed = (actual.stdout ?? string.Empty).Trim();
        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
            throw new HubException($"Sandbox '{boxId}' did not retain setting '{setting}'.", "sandbox_config_failed");
    }

    private static IEnumerable<string> NormalizeOpenPaths(IReadOnlyList<string> openFilePaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in openFilePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    private static (int ExitCode, string stdout) RunStart(string home, string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string> { $"/box:{boxId}", "/silent" };
        foreach (var pair in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            args.Add($"/env:{pair.Key}={pair.Value}");
        args.Add(executablePath);
        args.AddRange(arguments);
        return RunStartRaw(home, args, throwOnError: false, wait: wait);
    }

    private static (int ExitCode, string stdout) RunStartRaw(string home, IReadOnlyList<string> arguments,
        bool throwOnError = false, bool wait = true, bool useSbieIni = false)
    {
        var isIniVerb = arguments.Count > 0 && arguments[0] is "set" or "query" or "unset" or "append" or "delete";
        var fileName = isIniVerb || useSbieIni ? SbieIniExe(home) : StartExe(home);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)
            ?? throw new HubException($"Could not start {Path.GetFileName(fileName)}.", "sandbox_unavailable");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!wait)
        {
            // Fire-and-forget launch still drains to avoid pipe deadlock.
            _ = Task.Run(async () =>
            {
                try
                {
                    await stdoutTask.ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);
                }
                catch { }
            });
            return (0, string.Empty);
        }
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new HubException("Sandboxie command timed out.", "sandbox_timeout");
        }
        var stdout = stdoutTask.Result;
        stderrTask.Wait();
        if (throwOnError && process.ExitCode != 0)
            throw new HubException($"Sandboxie command failed with exit code {process.ExitCode}.", "sandbox_command_failed");
        return (process.ExitCode, stdout);
    }
}
