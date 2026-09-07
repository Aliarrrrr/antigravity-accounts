using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AGProfileHub.Core;

public interface IProcessCatalog
{
    Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default);
}

public interface IProcessLauncher
{
    Task<LaunchedProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken = default);
}

public interface IProcessController
{
    bool TryFocus(ProcessSnapshot expected);
    bool TryCloseMainWindow(ProcessSnapshot expected);
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default);
    bool Terminate(ProcessSnapshot expected);
}

public sealed class WindowsProcessCatalog : IProcessCatalog
{
    public Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(string rootExecutablePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process reconciliation requires Windows.");
#pragma warning disable CA1416
        return Task.Run<IReadOnlyList<ProcessSnapshot>>(() => SnapshotWindows(rootExecutablePath, cancellationToken), cancellationToken);
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ProcessSnapshot> SnapshotWindows(string rootExecutablePath, CancellationToken cancellationToken)
    {
        var rootName = Path.GetFileName(rootExecutablePath);
        var processes = new Dictionary<int, (int Parent, DateTimeOffset Started, string Path)>();
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,CreationDate,ExecutablePath FROM Win32_Process"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var pid = Convert.ToInt32((uint)item["ProcessId"], CultureInfo.InvariantCulture);
                    var parent = Convert.ToInt32((uint)item["ParentProcessId"], CultureInfo.InvariantCulture);
                    var path = item["ExecutablePath"] as string ?? string.Empty;
                    var start = ParseCreationDate(item["CreationDate"] as string);
                    processes[pid] = (parent, start, path);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or ManagementException) { }
            }
        }

        var commandLines = new Dictionary<int, string>();
        using (var searcher = new ManagementObjectSearcher($"SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='{rootName.Replace("'", "''", StringComparison.Ordinal)}'"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                try { commandLines[Convert.ToInt32((uint)item["ProcessId"], CultureInfo.InvariantCulture)] = item["CommandLine"] as string ?? string.Empty; }
                catch (Exception ex) when (ex is InvalidCastException or ManagementException) { }
            }
        }

        return processes.Select(entry => new ProcessSnapshot(entry.Key, entry.Value.Parent, entry.Value.Started,
            entry.Value.Path, commandLines.GetValueOrDefault(entry.Key, string.Empty), TryGetMainWindow(entry.Key))).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset ParseCreationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.MinValue;
        try { return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    private static nint TryGetMainWindow(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.MainWindowHandle; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }
}

public sealed class WindowsProcessLauncher : IProcessLauncher
{
    private static readonly string[] DangerousInheritedEnvironment =
        ["ELECTRON_OZONE_PLATFORM_HINT", "DEV_URL", "JETSKI_LS_PORT", "ELECTRON_RUN_AS_NODE", "NODE_OPTIONS"];

    public Task<LaunchedProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Antigravity launch requires Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LaunchWindows(plan));
    }

    [SupportedOSPlatform("windows")]
    private static LaunchedProcess LaunchWindows(LaunchPlan plan)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var variable in DangerousInheritedEnvironment) environment.Remove(variable);
        foreach (var item in plan.Environment) environment[item.Key] = item.Value;

        var startup = new NativeLaunchMethods.STARTUPINFO
        {
            cb = Marshal.SizeOf<NativeLaunchMethods.STARTUPINFO>(),
            dwFlags = 0,
        };
        var commandLine = new StringBuilder(string.Join(' ', new[] { plan.ExecutablePath }.Concat(plan.Arguments).Select(QuoteWindowsArgument)));
        var environmentBlock = BuildEnvironmentBlock(environment);
        bool created;
        NativeLaunchMethods.PROCESS_INFORMATION processInfo;
        try
        {
            // Do not inherit arbitrary Hub handles. The GUI child does not need stdio,
            // and inheriting file/lock handles could keep metadata locked or leak state.
            created = NativeLaunchMethods.CreateProcess(plan.ExecutablePath, commandLine, 0, 0, false,
                NativeLaunchMethods.CREATE_UNICODE_ENVIRONMENT | NativeLaunchMethods.CREATE_NEW_PROCESS_GROUP,
                environmentBlock, Path.GetDirectoryName(plan.ExecutablePath), ref startup, out processInfo);
        }
        finally { Marshal.FreeHGlobal(environmentBlock); }
        if (!created)
        {
            throw new HubException("Antigravity failed to start.", "launch_failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        try
        {
            if (!NativeLaunchMethods.GetProcessTimes(processInfo.hProcess, out var creationFileTime, out _, out _, out _))
                throw new HubException("Antigravity started but its creation time could not be read.", "launch_identity_failed");
            return new LaunchedProcess((int)processInfo.dwProcessId, DateTimeOffset.FromFileTime(creationFileTime).ToUniversalTime());
        }
        finally
        {
            NativeLaunchMethods.CloseHandle(processInfo.hThread);
            NativeLaunchMethods.CloseHandle(processInfo.hProcess);
        }
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { result.Append('\\', slashes * 2 + 1).Append(ch); slashes = 0; continue; }
            result.Append('\\', slashes).Append(ch); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var text = string.Join('\0', environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(text);
    }
}

public sealed class WindowsProcessController : IProcessController
{
    public bool TryFocus(ProcessSnapshot expected)
    {
        try
        {
            using var process = GetVerifiedProcess(expected);
            return process is not null && process.MainWindowHandle != 0 && NativeMethods.SetForegroundWindow(process.MainWindowHandle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public bool TryCloseMainWindow(ProcessSnapshot expected)
    {
        try
        {
            using var process = GetVerifiedProcess(expected);
            return process is not null && process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
        catch (ArgumentException) { return true; }
    }

    public bool Terminate(ProcessSnapshot expected)
    {
        try
        {
            using var process = GetVerifiedProcess(expected);
            if (process is null) return false;
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static Process? GetVerifiedProcess(ProcessSnapshot expected)
    {
        var process = Process.GetProcessById(expected.ProcessId);
        try
        {
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var actualPath = process.MainModule?.FileName;
            if (expected.StartTimeUtc == DateTimeOffset.MinValue || Math.Abs((actualStart - expected.StartTimeUtc).TotalSeconds) >= 1 ||
                string.IsNullOrWhiteSpace(actualPath) ||
                !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint hWnd);
    }
}

internal static class NativeLaunchMethods
{
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        internal int cb; internal string? lpReserved; internal string? lpDesktop; internal string? lpTitle;
        internal int dwX; internal int dwY; internal int dwXSize; internal int dwYSize; internal int dwXCountChars; internal int dwYCountChars;
        internal int dwFillAttribute; internal int dwFlags; internal short wShowWindow; internal short cbReserved2;
        internal nint lpReserved2; internal nint hStdInput; internal nint hStdOutput; internal nint hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION { internal nint hProcess; internal nint hThread; internal uint dwProcessId; internal uint dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcess(string applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint access, uint share, ref SECURITY_ATTRIBUTES security,
        uint creation, uint flags, nint template);
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { internal int Length; internal nint SecurityDescriptor; internal bool InheritHandle; }

    internal static Microsoft.Win32.SafeHandles.SafeFileHandle OpenNull(bool read)
    {
        var security = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), InheritHandle = true };
        var handle = CreateFile("NUL", read ? GENERIC_READ : GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref security, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid) throw new HubException("Could not open the Windows null device for child I/O.", "launch_io_failed");
        return handle;
    }
}
