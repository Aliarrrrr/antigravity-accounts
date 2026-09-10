using System.Diagnostics;
using System.Text.Json;

namespace AGProfileHub.Core;

/// <summary>
/// The elevated side of isolation setup: create a box, or replace its direct-write
/// rules. Everything here runs in the elevated helper process and reports a
/// machine-readable result, so the unelevated Hub never has to guess.
/// </summary>
public static class AdminOperations
{
    /// <summary>
    /// Test hooks. <paramref name="SkipRunningCheck"/> bypasses the live
    /// Sandboxie process query so deterministic tests do not depend on an
    /// installed Sandboxie driver; production callers keep it false.
    /// </summary>
    public sealed record Options(string? SandboxieHome = null, string? ConfigurationPath = null,
        bool RestartService = true, bool SkipRunningCheck = false);

    public static ElevationResult Execute(ElevationRequest request, Options? options = null)
    {
        options ??= new Options();
        try
        {
            return request.Operation switch
            {
                AdminOperation.CreateBox => CreateBox(request, options),
                AdminOperation.WriteThrough => WriteThrough(request, options),
                _ => ElevationResult.Failed($"Unknown elevated operation '{request.Operation}'.")
            };
        }
        catch (HubException ex)
        {
            return ElevationResult.Failed($"[{ex.Code}] {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ElevationResult.Failed($"[{ex.GetType().Name}] {ex.Message}");
        }
    }

    public static int RunHelper(string requestPath, string resultPath, Options? options = null)
    {
        ElevationResult result;
        try
        {
            if (!File.Exists(requestPath)) throw new HubException("The elevation request file was not found.", "elevation_request_missing");
            var request = JsonSerializer.Deserialize(File.ReadAllBytes(requestPath), HubJsonContext.Default.ElevationRequest)
                ?? throw new HubException("The elevation request could not be parsed.", "elevation_request_invalid");
            result = Execute(request, options);
        }
        catch (HubException ex)
        {
            result = ElevationResult.Failed($"[{ex.Code}] {ex.Message}");
        }
        catch (Exception ex)
        {
            result = ElevationResult.Failed($"[{ex.GetType().Name}] {ex.Message}");
        }
        try
        {
            File.WriteAllBytes(resultPath, JsonSerializer.SerializeToUtf8Bytes(result, HubJsonContext.Default.ElevationResult));
        }
        catch (IOException) { }
        return result.Succeeded ? 0 : 1;
    }

    private static ElevationResult CreateBox(ElevationRequest request, Options options)
    {
        var backend = ResolveBackend(options.SandboxieHome);
        if (backend.BoxExists(request.BoxName)) return ElevationResult.Ok();
        backend.CreateBox(request.BoxName);
        if (!backend.BoxExists(request.BoxName))
            return ElevationResult.Failed("Sandboxie did not retain the isolation box.");
        return ElevationResult.Ok();
    }

    private static ElevationResult WriteThrough(ElevationRequest request, Options options)
    {
        if (request.Paths.Count == 0)
            return ElevationResult.Failed("No direct-write paths were supplied.");
        var home = options.SandboxieHome ?? SandboxieBackend.TryDiscover()?.Home;

        SandboxieIniFile? file = null;
        if (!string.IsNullOrWhiteSpace(options.ConfigurationPath)) file = SandboxieIniFile.Load(options.ConfigurationPath);
        file ??= SandboxieIniFile.TryOpenSystem(home)
            ?? throw new HubException("Sandboxie.ini was not found.", "sandbox_config_missing");

        if (!options.SkipRunningCheck)
        {
            var existing = SandboxieBackend.TryDiscover();
            if (existing is not null && existing.ListPids(request.BoxName).Count > 0)
                return ElevationResult.Failed($"Isolation box '{request.BoxName}' is still running. Stop every box before changing direct-write rules.");
        }

        var validated = new List<string>();
        foreach (var candidate in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            if (!Path.IsPathFullyQualified(full)) return ElevationResult.Failed($"Direct-write path '{candidate}' is not fully qualified.");
            if (!validated.Contains(full, StringComparer.OrdinalIgnoreCase)) validated.Add(full);
        }
        if (validated.Count == 0) return ElevationResult.Failed("No usable direct-write paths were supplied.");

        file.ReplaceValues(request.BoxName, "OpenFilePath", validated);
        file.Save();
        var backup = file.LastBackupPath;

        if (options.RestartService)
        {
            try
            {
                RestartSandboxie(home);
            }
            catch (HubException ex)
            {
                Restore(file.Path, backup);
                return ElevationResult.Failed($"{ex.Message} The previous configuration was restored from '{backup}'.");
            }
        }
        return ElevationResult.Ok(backup);
    }

    /// <summary>
    /// The driver caches path rules, so a configuration edit is only observed
    /// after the service restarts and the driver reloads. Both steps need
    /// elevation; without them <c>/reload</c> returns success and does nothing.
    /// </summary>
    private static void RestartSandboxie(string? home)
    {
        Run("sc.exe", ["stop", "SbieSvc"], allowFailure: true);
        var started = Run("sc.exe", ["start", "SbieSvc"], allowFailure: true);
        if (!started && !IsServiceRunning())
            throw new HubException("The Sandboxie service could not be restarted.", "sandbox_restart_failed");
        if (!string.IsNullOrWhiteSpace(home))
            Run(Path.Combine(home, "Start.exe"), ["/reload"], allowFailure: true);
    }

    private static bool IsServiceRunning()
    {
        try { return ServiceProcessHelper.IsRunning("SbieSvc"); }
        catch { return false; }
    }

    private static void Restore(string path, string? backup)
    {
        if (string.IsNullOrWhiteSpace(backup) || !File.Exists(backup)) return;
        try { File.Copy(backup, path, overwrite: true); } catch (IOException) { }
    }

    private static SandboxieBackend ResolveBackend(string? home)
    {
        var backend = string.IsNullOrWhiteSpace(home) ? SandboxieBackend.TryDiscover() : new SandboxieBackend(home);
        if (backend is null || !File.Exists(backend.StartPath))
            throw new HubException("Sandboxie Plus was not found.", "sandbox_not_found");
        return backend;
    }

    private static bool Run(string fileName, IReadOnlyList<string> arguments, bool allowFailure)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return process.ExitCode == 0 || stdout.Contains("1062", StringComparison.Ordinal) || stderr.Contains("1062", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (allowFailure) return false;
            throw new HubException($"Could not run '{Path.GetFileName(fileName)}'.", "sandbox_tool_missing", ex);
        }
    }

    public static string Describe(ElevationRequest request) =>
        request.Operation switch
        {
            AdminOperation.CreateBox => $"Create isolation box '{request.BoxName}'",
            AdminOperation.WriteThrough => $"Configure {request.Paths.Count} direct-write path(s) for '{request.BoxName}'",
            _ => $"Unknown operation '{request.Operation}'"
        };
}

internal static class ServiceProcessHelper
{
    public static bool IsRunning(string serviceName)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("query");
        info.ArgumentList.Add(serviceName);
        using var process = Process.Start(info) ?? throw new HubException("Could not query services.", "sandbox_tool_missing");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }
}
