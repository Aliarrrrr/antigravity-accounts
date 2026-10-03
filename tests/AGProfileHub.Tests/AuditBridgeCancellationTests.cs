using System.Diagnostics;
using System.Text;
using AGProfileHub.Core;

internal static class AuditBridgeCancellationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Audit bridge cancellation reaps only its owned child", CancelAsync),
        ("Audit bridge cancellation normal exit drains large output", () => ExitAsync(0)),
        ("Audit bridge cancellation nonzero exit drains large output", () => ExitAsync(7)),
        ("Audit bridge cancellation handles an already exited child", ExitedAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static Process Start(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script)) }) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private static async Task Reap(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
    }
    private static async Task Ready(string marker, Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
        {
            Check(!process.HasExited, "Controlled child exited before readiness.");
            await Task.Delay(20, deadline.Token);
        }
        Check(!process.HasExited, "Controlled child was not alive at readiness.");
    }
    private static async Task CancelAsync()
    {
        using var owned = new OwnedTestDirectory();
        var ready = Path.Combine(owned.Root, "target.ready"); var otherReady = Path.Combine(owned.Root, "neighbor.ready");
        static string Script(string marker) => "[Console]::Out.WriteLine('ready-out'); [Console]::Error.WriteLine('ready-error'); [Console]::Out.Flush(); [Console]::Error.Flush(); [IO.File]::WriteAllText(" + Quote(marker) + ",'ready'); while ($true) { Start-Sleep -Milliseconds 100 }";
        using var target = Start(Script(ready)); using var neighbor = Start(Script(otherReady));
        using var cancellation = new CancellationTokenSource();
        var waiting = NextBridge.WaitForBridgeProcessAsync(target, cancellation.Token);
        try
        {
            await Ready(ready, target); await Ready(otherReady, neighbor);
            cancellation.Cancel();
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(12)); throw new Exception("Cancellation unexpectedly succeeded."); }
            catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancellation.Token, "Original cancellation token lost."); }
            // Assert before teardown: this is the regression, not a test cleanup side effect.
            Check(target.HasExited, "Cancelled bridge child is still alive.");
            Check(!neighbor.HasExited, "Neighbor was terminated.");
        }
        finally
        {
            cancellation.Cancel();
            await Reap(target); await Reap(neighbor);
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(12)); } catch (OperationCanceledException) { }
        }
    }
    private static async Task ExitAsync(int code)
    {
        using var owned = new OwnedTestDirectory(); var marker = Path.Combine(owned.Root, "drained");
        using var process = Start("$chunk='x'*8192; for($i=0;$i -lt 128;$i++){ [Console]::Out.WriteLine($chunk); [Console]::Error.WriteLine($chunk) }; [Console]::Out.Flush(); [Console]::Error.Flush(); [IO.File]::WriteAllText(" + Quote(marker) + ",'complete'); exit " + code);
        try
        {
            var waiting = NextBridge.WaitForBridgeProcessAsync(process, default);
            if (code == 0) await waiting.WaitAsync(TimeSpan.FromSeconds(15));
            else await TestAssert.ThrowsCodeAsync<HubException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(15)), "bridge_create_failed");
            Check(process.HasExited && process.ExitCode == code && File.Exists(marker), "Output did not finish before exit validation.");
        }
        finally { await Reap(process); }
    }
    private static async Task ExitedAsync()
    {
        using var process = Start("[Console]::Out.WriteLine('done'); [Console]::Error.WriteLine('done'); exit 0");
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await NextBridge.WaitForBridgeProcessAsync(process, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(12)); throw new Exception("Cancellation unexpectedly succeeded."); }
            catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancellation.Token && process.HasExited, "Exit race lost cancellation or exit."); }
        }
        finally { await Reap(process); }
    }
}
