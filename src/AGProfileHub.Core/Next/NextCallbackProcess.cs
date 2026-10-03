using System.ComponentModel;
using System.Diagnostics;

namespace AGProfileHub.Core;

internal interface ICallbackProcessLauncher
{
    ICallbackProcess Start(ProcessStartInfo info);
}

internal interface ICallbackProcess : IDisposable
{
    int Id { get; }
    DateTimeOffset StartTimeUtc { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    Task WaitForExitAsync(CancellationToken token);
}

internal sealed class WindowsCallbackProcessLauncher : ICallbackProcessLauncher
{
    public ICallbackProcess Start(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new HubException("Callback handoff failed.", "callback_failed");
        try { return new CallbackProcess(process); }
        catch { process.Dispose(); throw; }
    }

    private sealed class CallbackProcess(Process process) : ICallbackProcess
    {
        // Keep the original process handle: exit/PID reuse must not change this identity.
        private readonly Microsoft.Win32.SafeHandles.SafeProcessHandle _handle = process.SafeHandle;
        public int Id => process.Id;
        public DateTimeOffset StartTimeUtc
        {
            get
            {
                var added = false;
                try
                {
                    _handle.DangerousAddRef(ref added);
                    if (!NativeLaunchMethods.GetProcessTimes(_handle.DangerousGetHandle(), out var creation, out _, out _, out _))
                        throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    return new DateTimeOffset(DateTime.FromFileTimeUtc(creation));
                }
                finally { if (added) _handle.DangerousRelease(); }
            }
        }
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public StreamReader StandardOutput => process.StandardOutput;
        public StreamReader StandardError => process.StandardError;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void Dispose() => process.Dispose();
    }
}
