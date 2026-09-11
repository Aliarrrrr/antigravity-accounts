using System.Diagnostics;

namespace AGProfileHub.Core;

/// <summary>
/// Optional Sandboxie adapter. Launcher acceptance is not proof that the vendor
/// process belongs to a box; the supervisor verifies both PID identity and box membership.
/// </summary>
public sealed class WindowsSandboxieRuntime(string? sandboxieHomeOverride = null) : ISandboxRuntime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    public static readonly string[] DefaultSearchRoots = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    }.Where(path => !string.IsNullOrWhiteSpace(path))
        .SelectMany(path => new[] { Path.Combine(path, "Sandboxie-Plus"), Path.Combine(path, "Sandboxie") })
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public string? Home => ResolveHome();
    public bool IsAvailable => OperatingSystem.IsWindows() && Home is not null;
    public string RuntimeDescription => Home is { } home ? $"Sandboxie at {home}" :
        "Sandboxie was not found. Configure its directory with 'configure-sandboxie <path>', or create a Direct profile (no Sandboxie required).";

    /// <returns>True only if this invocation created a previously nonexistent box.</returns>
    public async Task<bool> EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default)
    {
        ValidateBoxId(boxId);
        var home = RequireHome();
        var paths = NormalizeOpenPaths(openFilePaths);
        if (paths.Count == 0) throw new ConfigurationException("Managed profile paths are required.", "sandbox_path_invalid");
        var sections = await IniAsync(home, ["query", "*"], cancellationToken).ConfigureAwait(false);
        var fresh = !Lines(sections).Contains(boxId, StringComparer.OrdinalIgnoreCase);
        if (!fresh)
        {
            var owner = (await IniAsync(home, ["query", boxId, "AGPHProfilePath"], cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.Equals(owner, paths[0], StringComparison.OrdinalIgnoreCase))
                throw new HubException("This sandbox is not owned by this profile, or was created by an older experimental build. Its settings and credentials were left unchanged. Create a new Sandboxie profile instead.", "sandbox_not_owned");
        }
        // Never open host credentials or the vendor installation directory.
        await SetAsync(home, boxId, "AGPHProfilePath", paths[0], cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in new[] { ("Enabled", "y"), ("ConfigLevel", "10"), ("AutoRecover", "n"),
            ("AutoDelete", "n"), ("BoxNameTitle", "y"), ("OpenCredentials", "n") })
            await SetAsync(home, boxId, key, value, cancellationToken).ConfigureAwait(false);

        // Repeated INI values use append, not synthetic OpenFilePath.1 keys.
        await IniAsync(home, ["set", boxId, "OpenFilePath", paths[0]], cancellationToken).ConfigureAwait(false);
        foreach (var path in paths.Skip(1))
            await IniAsync(home, ["append", boxId, "OpenFilePath", path], cancellationToken).ConfigureAwait(false);
        var retained = Lines(await IniAsync(home, ["query", boxId, "OpenFilePath"], cancellationToken).ConfigureAwait(false));
        if (!new HashSet<string>(retained, StringComparer.OrdinalIgnoreCase).SetEquals(paths))
            throw new HubException("Sandboxie did not retain the managed path configuration.", "sandbox_config_failed");
        return fresh;
    }

    public async Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default)
    {
        var command = BuildStartArguments(boxId, executablePath, arguments, environment, wait: false);
        var result = await RunAsync(Path.Combine(RequireHome(), "Start.exe"), command, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new LaunchStateUncertainException($"Sandboxie launcher exited with code {result.ExitCode}; inspect the box before retrying.");
    }

    public async Task<int> RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var command = BuildStartArguments(boxId, executablePath, arguments, environment, wait: true);
        var result = await RunAsync(Path.Combine(RequireHome(), "Start.exe"), command, timeout, cancellationToken).ConfigureAwait(false);
        // /wait forwards the child exit code. The shim caller accepts 0 and 11,
        // not all nonzero codes and not only zero.
        return result.ExitCode;
    }

    public async Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default)
    {
        ValidateBoxId(boxId);
        var result = await RunAsync(Path.Combine(RequireHome(), "Start.exe"), [$"/box:{boxId}", "/silent", "/listpids"],
            CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new HubException("Sandboxie process listing failed.", "sandbox_query_failed");
        return ParseProcessIds(result.Output).Select(pid => new SandboxRuntimeSnapshot(pid, "sandbox-process", null)).ToArray();
    }

    public async Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default)
    {
        ValidateBoxId(boxId);
        var result = await RunAsync(Path.Combine(RequireHome(), "Start.exe"), [$"/box:{boxId}", "/silent", "/terminate"],
            CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new HubException("Sandboxie could not stop the verified box.", "sandbox_command_failed");
    }

    public static IReadOnlyList<int> ParseProcessIds(string output)
    {
        // The first line is the COUNT, not a process ID (official Start.exe contract).
        var lines = Lines(output);
        if (lines.Length == 0 || !int.TryParse(lines[0], out var count) || count < 0 || lines.Length != count + 1)
            throw new HubException("Sandboxie returned an invalid PID list; ownership cannot be verified.", "sandbox_query_failed");
        var pids = new List<int>(count);
        foreach (var line in lines.Skip(1))
        {
            if (!int.TryParse(line, out var pid) || pid <= 0 || pids.Contains(pid))
                throw new HubException("Sandboxie returned an invalid process ID.", "sandbox_query_failed");
            pids.Add(pid);
        }
        return pids;
    }

    public static IReadOnlyList<string> BuildStartArguments(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool wait)
    {
        ValidateBoxId(boxId);
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ConfigurationException("Sandbox executable must use an absolute path.", "executable_path_invalid");
        var result = new List<string> { $"/box:{boxId}", "/silent" };
        if (wait) result.Add("/wait");
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.IndexOfAny(['=', '\0', '"']) >= 0 || pair.Value.Contains('\0'))
                throw new ConfigurationException("Invalid sandbox environment variable.", "sandbox_environment_invalid");
            result.Add($"/env:{pair.Key}={pair.Value}");
        }
        result.Add(executablePath);
        result.AddRange(arguments);
        return result;
    }

    public static void ValidateBoxId(string boxId)
    {
        if (string.IsNullOrWhiteSpace(boxId) || boxId.Length is < 3 or > 64 ||
            boxId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            throw new ConfigurationException("Sandbox box id must be 3-64 letters, digits, underscores or hyphens.", "invalid_box_id");
    }

    internal static string DefaultBoxId(Guid profileId) => "AGPH_" + profileId.ToString("N");

    private string RequireHome() => IsAvailable ? Home! : throw new UnsupportedCapabilityException(RuntimeDescription);
    private string? ResolveHome()
    {
        var configured = sandboxieHomeOverride;
        if (string.IsNullOrWhiteSpace(configured)) configured = Environment.GetEnvironmentVariable("AGPH_SANDBOXIE_HOME");
        var candidates = string.IsNullOrWhiteSpace(configured) ? DefaultSearchRoots : new[] { configured };
        foreach (var path in candidates)
        {
            if (!Path.IsPathFullyQualified(path)) continue;
            if (File.Exists(Path.Combine(path, "Start.exe")) && File.Exists(Path.Combine(path, "SbieIni.exe")))
                return Path.GetFullPath(path);
        }
        return null;
    }

    private static List<string> NormalizeOpenPaths(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['*', '?']) >= 0)
                throw new ConfigurationException("Only exact managed directories may be opened through Sandboxie.", "sandbox_path_invalid");
            var full = Path.GetFullPath(path);
            PathSafety.RejectExistingPathAncestors(full);
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return result;
    }

    private static string[] Lines(string output) => output.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task SetAsync(string home, string box, string key, string value, CancellationToken cancellationToken)
    {
        await IniAsync(home, ["set", box, key, value], cancellationToken).ConfigureAwait(false);
        var actual = await IniAsync(home, ["query", box, key], cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual.Trim(), value, StringComparison.Ordinal))
            throw new HubException($"Sandboxie did not retain '{key}'.", "sandbox_config_failed");
    }

    private static async Task<string> IniAsync(string home, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(Path.Combine(home, "SbieIni.exe"), arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new HubException($"Sandboxie configuration command failed ({result.ExitCode}).", "sandbox_config_failed");
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new HubException("Sandboxie helper was not created.", "sandbox_unavailable");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new HubException("Sandboxie could not run in this Windows environment. Check its installation and permissions.", "sandbox_unavailable", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            // Stop only our own helper. Never kill an unverified vendor process tree.
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new HubException("Sandboxie helper timed out. Inspect the box before retrying.", "sandbox_timeout");
        }
    }
}
