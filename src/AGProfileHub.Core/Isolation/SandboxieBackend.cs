using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AGProfileHub.Core;

public interface IIsolationBackend
{
    bool BoxExists(string box);
    void CreateBox(string box);
    IReadOnlyList<int> ListPids(string box);
    IReadOnlyList<string> GetIndexedSettingValues(string box, string setting, int maximum = 64);
    void Terminate(string box);
    int RunInBox(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, bool wait = false);
    int LaunchDetached(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment);
}

/// <summary>
/// Thin adapter over the Sandboxie Plus command-line surface.
/// </summary>
/// <remarks>
/// The upstream interfaces used here are documented:
/// https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine/
/// Only per-box lifecycle, process listing and termination are used. The Hub
/// does not install, bundle or modify Sandboxie.
/// </remarks>
public sealed class SandboxieBackend : IIsolationBackend
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    public SandboxieBackend(string home)
    {
        Home = home;
        StartPath = Path.Combine(home, "Start.exe");
        SbieIniPath = Path.Combine(home, "SbieIni.exe");
    }

    public string Home { get; }
    public string StartPath { get; }
    public string SbieIniPath { get; }

    public static SandboxieBackend? TryDiscover()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Sandboxie-Plus"));
            candidates.Add(Path.Combine(programFiles, "Sandboxie"));
        }
        foreach (var candidate in candidates)
        {
            var start = Path.Combine(candidate, "Start.exe");
            var ini = Path.Combine(candidate, "SbieIni.exe");
            if (File.Exists(start) && File.Exists(ini)) return new SandboxieBackend(candidate);
        }
        return null;
    }

    public static bool IsCurrentProcessSandboxed() =>
        OperatingSystem.IsWindows() && GetModuleHandleW("SbieDll.dll") != IntPtr.Zero;

    public bool BoxExists(string box)
    {
        var result = Run(SbieIniPath, new[] { "query", box, "Enabled" }, wait: true);
        return result.ExitCode == 0 && result.Stdout.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    public void CreateBox(string box)
    {
        var result = Run(SbieIniPath, new[] { "set", box, "Enabled", "y" }, wait: true);
        if (result.ExitCode != 0)
            throw new HubException("Sandboxie could not create the isolation box.", "sandbox_create_failed");
        if (!BoxExists(box))
            throw new HubException("Sandboxie did not retain the isolation box.", "sandbox_verify_failed");

        // Keep the box conservative: no auto recovery, no auto delete, and do
        // not open the host credential store.
        ApplySetting(box, "ConfigLevel", "10");
        ApplySetting(box, "AutoRecover", "n");
        ApplySetting(box, "AutoDelete", "n");
        ApplySetting(box, "OpenCredentials", "n");
    }

    private void ApplySetting(string box, string key, string value)
    {
        var result = Run(SbieIniPath, new[] { "set", box, key, value }, wait: true);
        if (result.ExitCode != 0)
            throw new HubException($"Sandboxie could not set {key} for the isolation box.", "sandbox_config_failed");
    }

    public IReadOnlyList<int> ListPids(string box)
    {
        var result = Run(StartPath, new[] { $"/box:{box}", "/listpids" }, wait: true);
        if (result.ExitCode != 0)
            throw new HubException("Sandboxie could not inspect the isolation box.", "sandbox_status_failed");
        var pids = new List<int>();
        foreach (var line in result.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (int.TryParse(trimmed, out var pid)) pids.Add(pid);
        }
        return pids;
    }

    public IReadOnlyList<string> GetIndexedSettingValues(string box, string setting, int maximum = 64)
    {
        // SbieIni's indexed query form (Setting.N) cannot see bare repeated keys,
        // and the driver only honours bare keys, so parse the configuration file
        // directly and accept both spellings. SandboxieIniFile owns that parsing
        // so the read path and the write path can never disagree again.
        var file = SandboxieIniFile.TryOpenSystem(Home);
        if (file is null) return [];
        var values = file.GetValues(box, setting);
        return values.Count <= maximum ? values : [.. values.Take(maximum)];
    }

    public void Terminate(string box)
    {
        var result = Run(StartPath, new[] { $"/box:{box}", "/terminate" }, wait: true);
        if (result.ExitCode != 0)
            throw new HubException("Sandboxie could not terminate the isolation box.", "sandbox_stop_failed");
    }

    /// <summary>Runs a program inside the box. Returns the child exit code.</summary>
    public int RunInBox(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, bool wait = false)
    {
        var all = new List<string> { $"/box:{box}" };
        if (wait) all.Add("/wait");
        all.Add(executable);
        all.AddRange(arguments);
        return Run(StartPath, all, wait: true, environment: environment).ExitCode;
    }

    public int LaunchDetached(string box, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var all = new List<string> { $"/box:{box}", executable };
        all.AddRange(arguments);
        var info = new ProcessStartInfo(StartPath) { UseShellExecute = false };
        foreach (var argument in all) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new HubException("Failed to start the isolation launcher.", "sandbox_start_failed");
        return process.Id;
    }

    private sealed record RunResult(int ExitCode, string Stdout);

    private static RunResult Run(string fileName, IReadOnlyList<string> arguments, bool wait,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(info) ?? throw new HubException($"Failed to start '{fileName}'.", "sandbox_tool_missing");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!wait) return new RunResult(0, stdout);
        process.WaitForExit();
        if (stderr.Length > 0) stdout += "\n" + stderr;
        return new RunResult(process.ExitCode, stdout);
    }
}
