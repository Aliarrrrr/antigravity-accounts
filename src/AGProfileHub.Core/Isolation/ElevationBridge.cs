using System.Diagnostics;
using System.Text.Json;

namespace AGProfileHub.Core;

/// <summary>Names of the operations that need elevation. They are performed by the Hub itself, never by a script the user has to copy.</summary>
public static class AdminOperation
{
    public const string CreateBox = "create-box";
    public const string WriteThrough = "write-through";
}

public sealed record ElevationRequest(string Operation, string BoxName, IReadOnlyList<string> Paths);

public sealed record ElevationResult(bool Succeeded, string? Detail, string? BackupPath)
{
    public static ElevationResult Ok(string? backupPath = null) => new(true, null, backupPath);
    public static ElevationResult Failed(string detail) => new(false, detail, null);
}

/// <summary>
/// Runs a short, auditable operation with elevation. Sandboxie keeps its
/// configuration in <c>%WINDIR%\Sandboxie.ini</c>, which standard users cannot
/// write, so box creation and direct-write rules need one elevated hop.
/// </summary>
public interface IElevationBridge
{
    bool IsAvailable { get; }
    bool IsElevated { get; }
    Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken cancellationToken = default);
}

public sealed class WindowsElevationBridge : IElevationBridge
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly string _helperExecutable;
    private readonly string _workDirectory;
    private readonly TimeSpan _timeout;

    public WindowsElevationBridge(string helperExecutable, string workDirectory, TimeSpan? timeout = null)
    {
        _helperExecutable = helperExecutable;
        _workDirectory = workDirectory;
        _timeout = timeout ?? DefaultTimeout;
    }

    public bool IsAvailable => File.Exists(_helperExecutable);

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return ElevationResult.Failed($"The elevation helper was not found at '{_helperExecutable}'.");
        if (string.IsNullOrWhiteSpace(request.BoxName))
            return ElevationResult.Failed("An isolation box name is required.");

        Directory.CreateDirectory(_workDirectory);
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(_workDirectory, $"elevate-{id}.json");
        var resultPath = Path.Combine(_workDirectory, $"elevate-{id}.result.json");
        await File.WriteAllBytesAsync(
            requestPath,
            JsonSerializer.SerializeToUtf8Bytes(request, HubJsonContext.Default.ElevationRequest),
            cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var info = BuildHelperStartInfo(requestPath, resultPath);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1223 or 5)
        {
            // 1223 = the user dismissed the consent prompt, 5 = consent refused.
            return ElevationResult.Failed("Elevation was declined, so no Sandboxie configuration was changed.");
        }
        if (process is null)
            return ElevationResult.Failed("The elevation helper could not be started.");

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
            return ElevationResult.Failed("The elevated operation timed out.");
        }

        var result = await TryReadResultAsync(resultPath, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return ElevationResult.Failed(process.ExitCode == 0
                ? "The elevated operation did not report a result."
                : $"The elevated operation failed with exit code {process.ExitCode}.");
        return result;
    }

    private static async Task<ElevationResult?> TryReadResultAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Deserialize(bytes, HubJsonContext.Default.ElevationResult);
                }
                catch (JsonException) { return null; }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // UAC-elevated children do not inherit this process's environment, so a
    // framework-dependent helper cannot rely on DOTNET_ROOT reaching it. When
    // the helper has no bundled runtime, launch it through an explicit muxer
    // (dotnet.exe <dll>) resolved from this machine instead of the apphost.
    private ProcessStartInfo BuildHelperStartInfo(string requestPath, string resultPath)
    {
        var muxer = ResolveDotnetMuxer(_helperExecutable);
        var info = new ProcessStartInfo(muxer ?? _helperExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WorkingDirectory = _workDirectory
        };
        if (muxer is not null) info.ArgumentList.Add(Path.ChangeExtension(_helperExecutable, ".dll"));
        info.ArgumentList.Add("admin-helper");
        info.ArgumentList.Add(requestPath);
        info.ArgumentList.Add(resultPath);
        return info;
    }

    internal static string? ResolveDotnetMuxer(string helperExecutable) =>
        ResolveDotnetMuxer(helperExecutable,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("ProgramFiles"));

    internal static string? ResolveDotnetMuxer(string helperExecutable, string? hostPath, string? dotnetRootX64, string? dotnetRoot, string? programFiles)
    {
        // A self-contained helper carries its runtime and keeps the direct path.
        var helperDirectory = Path.GetDirectoryName(helperExecutable);
        if (helperDirectory is null || !File.Exists(Path.ChangeExtension(helperExecutable, ".dll"))) return null;
        if (File.Exists(Path.Combine(helperDirectory, "coreclr.dll"))) return null;

        foreach (var candidate in MuxerCandidates(helperDirectory, hostPath, dotnetRootX64, dotnetRoot, programFiles))
        {
            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
            if (HasNet10SharedRuntime(Path.GetDirectoryName(candidate)!)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string?> MuxerCandidates(string helperDirectory, string? hostPath, string? dotnetRootX64, string? dotnetRoot, string? programFiles)
    {
        // The host that started this process, when it is a real muxer (not an apphost).
        if (string.Equals(Path.GetFileName(hostPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) yield return hostPath;
        foreach (var root in new[] { dotnetRootX64, dotnetRoot })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Directory.Exists(root) ? Path.Combine(root, "dotnet.exe") : root;
        }
        // Repo-local SDK used by development builds.
        var directory = new DirectoryInfo(helperDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            yield return Path.Combine(directory.FullName, ".tools", "dotnet", "dotnet.exe");
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
    }

    private static bool HasNet10SharedRuntime(string dotnetDirectory)
    {
        var shared = Path.Combine(dotnetDirectory, "shared", "Microsoft.NETCore.App");
        try
        {
            return Directory.Exists(shared) && Directory.EnumerateDirectories(shared)
                .Any(path => Path.GetFileName(path).StartsWith("10.", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Used when elevation is impossible or explicitly disabled; it never silently pretends to succeed.</summary>
public sealed class UnavailableElevationBridge(string? reason = null) : IElevationBridge
{
    public bool IsAvailable => false;
    public bool IsElevated => false;
    public Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ElevationResult.Failed(reason ?? "Elevation is not available in this environment."));
}
