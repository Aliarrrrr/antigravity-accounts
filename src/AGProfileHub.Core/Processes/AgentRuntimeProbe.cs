using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AGProfileHub.Core;

public sealed record AgentProbeResult(bool ListenerOwned, int ExitCode, string Outcome);

/// <summary>Experimental, mocked-data only. Never opens a registered profile.</summary>
public static class AgentRuntimeProbe
{
    private const string VerifiedHash = "424ED4EF6FC9CB470CE785D8F4B392DE3FA94FD16D030E6FF83E043440659F94";

    public static string Classify(string output, int exitCode)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) || !root.TryGetProperty("response", out _))
                return "invalid-envelope";
            if (exitCode == 0 && error.ValueKind == JsonValueKind.Null) return "success";
            // No free-text matching: an arbitrary 'not found' message is not readiness proof.
            return "remote-error";
        }
        catch (JsonException) { return "invalid-envelope"; }
    }

    public static async Task<AgentProbeResult> RunAsync(string executable, string dataRoot,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new UnsupportedCapabilityException("Agent probe requires Windows.");
        executable = PathSafety.CanonicalizeExistingFile(executable);
        var server = PathSafety.CanonicalizeExistingFile(Path.Combine(Path.GetDirectoryName(executable)!, "resources", "bin", "language_server.exe"));
        // Keep the verified binary open without write/delete sharing throughout this session.
        using var binary = new FileStream(server, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(binary, cancellationToken)) != VerifiedHash)
            throw new UnsupportedCapabilityException("Language server hash is not verified; probe refused.");
        var root = Path.GetFullPath(dataRoot);
        var scratch = Path.Combine(root, "agent-probes", Guid.NewGuid().ToString("N"));
        PathSafety.EnsureSafeDirectory(root, scratch);
        foreach (var folder in new[] { "home", "roaming", "local", "temp", "gemini" })
            PathSafety.EnsureSafeDirectory(root, Path.Combine(scratch, folder));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // The socket reservation is released before launch. Ownership validation below fails closed on collision.
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var ls = new Process();
        ls.StartInfo = StartInfo(server, scratch);
        foreach (var arg in new[] { "--standalone", "--headless", "--override_ide_name", "antigravity",
                     "--subclient_type", "hub", "--override_ide_version", "2.12.2", "--override_user_agent_name", "antigravity",
                     "--http_server_port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--csrf_token", token, "--gemini_dir", Path.Combine(scratch, "gemini"),
                     "--app_data_dir", "antigravity", "--disable_telemetry", "--use_mocked_data" })
            ls.StartInfo.ArgumentList.Add(arg);
        Task<string>? stdout = null, stderr = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!ls.Start()) throw new LaunchNotStartedException("Language server did not start.");
            var started = ls.StartTime.ToUniversalTime();
            ls.StandardInput.Close();
            stdout = DrainAsync(ls.StandardOutput, false, deadline.Token);
            stderr = DrainAsync(ls.StandardError, false, deadline.Token);
            while (!OwnsListener(ls, started, port))
            {
                if (ls.HasExited) throw new HubException("Language server exited before readiness.", "agent_start_failed");
                await Task.Delay(100, deadline.Token);
            }
            using var cli = new Process { StartInfo = StartInfo(server, scratch) };
            foreach (var arg in new[] { "agentapi", "get-conversation-metadata", "00000000-0000-0000-0000-000000000000" })
                cli.StartInfo.ArgumentList.Add(arg);
            cli.StartInfo.Environment["ANTIGRAVITY_LS_ADDRESS"] = $"127.0.0.1:{port}";
            cli.StartInfo.Environment["ANTIGRAVITY_CSRF_TOKEN"] = token;
            if (!OwnsListener(ls, started, port)) throw new ProcessOwnershipException("Agent listener ownership changed.");
            Task<string>? cliOut = null, cliErr = null;
            try
            {
                if (!cli.Start()) throw new LaunchNotStartedException("Agent CLI did not start.");
                cli.StandardInput.Close();
                cliOut = DrainAsync(cli.StandardOutput, true, deadline.Token);
                cliErr = DrainAsync(cli.StandardError, false, deadline.Token);
                await cli.WaitForExitAsync(deadline.Token);
                var output = await cliOut;
                await cliErr;
                if (!OwnsListener(ls, started, port)) throw new ProcessOwnershipException("Agent listener ownership changed during probe.");
                return new AgentProbeResult(true, cli.ExitCode, Classify(output, cli.ExitCode));
            }
            finally
            {
                await StopAsync(cli);
                await ObserveAsync(cliOut, cliErr);
            }
        }
        finally
        {
            deadline.Cancel();
            await StopAsync(ls);
            await ObserveAsync(stdout, stderr);
            // Preserve vendor-generated scratch for local inspection. The hub never writes captured output or CSRF.
        }
    }

    private static ProcessStartInfo StartInfo(string server, string scratch)
    {
        var info = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = scratch };
        foreach (var key in new[] { "ANTIGRAVITY_LS_ADDRESS", "ANTIGRAVITY_CSRF_TOKEN", "NODE_OPTIONS", "ELECTRON_RUN_AS_NODE", "DEV_URL", "JETSKI_LS_PORT" })
            info.Environment.Remove(key);
        info.Environment["USERPROFILE"] = info.Environment["HOME"] = Path.Combine(scratch, "home");
        info.Environment["APPDATA"] = Path.Combine(scratch, "roaming");
        info.Environment["LOCALAPPDATA"] = Path.Combine(scratch, "local");
        info.Environment["TEMP"] = info.Environment["TMP"] = Path.Combine(scratch, "temp");
        return info;
    }

    private static async Task<string> DrainAsync(StreamReader reader, bool retain, CancellationToken token)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        var overflow = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (retain && result.Length + count <= 65536 && !overflow) result.Append(buffer, 0, count);
            else if (retain) { overflow = true; result.Clear(); }
        }
        return overflow ? string.Empty : result.ToString();
    }

    private static async Task ObserveAsync(params Task<string>?[] tasks)
    {
        foreach (var task in tasks)
            if (task is not null)
                try { await task; } catch (OperationCanceledException) { } catch (IOException) { }
    }

    private static async Task StopAsync(Process process)
    {
        try { _ = process.Id; } catch (InvalidOperationException) { return; }
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static bool OwnsListener(Process process, DateTime started, int port)
    {
        if (process.HasExited || process.StartTime.ToUniversalTime() != started) return false;
        var size = 0;
        var status = GetExtendedTcpTable(0, ref size, false, 2, 3, 0);
        if (status != 122) throw new HubException("Cannot query TCP owners.", "agent_owner_query_failed");
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(memory, ref size, false, 2, 3, 0);
            if (status == 122) return false;
            if (status != 0) throw new HubException("Cannot query TCP owners.", "agent_owner_query_failed");
            var count = Marshal.ReadInt32(memory);
            for (var i = 0; i < count; i++)
            {
                var row = memory + 4 + i * 24;
                var localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                if (Marshal.ReadInt32(row) == 2 && Marshal.ReadInt32(row, 4) == 0x0100007f &&
                    localPort == port && Marshal.ReadInt32(row, 20) == process.Id) return !process.HasExited;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
