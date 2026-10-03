using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGProfileHub.Core;

return await NextCli.RunAsync(args);
internal static class NextCli
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private const string Help = "agprofile-next: new/create <name> [workspace], ls/list/status [profile], open/launch <profile>, close/stop <profile> [--force], restart <profile> [--force], rename <profile> <name>, workspace <profile> [path], remove <profile>, configure-executable <exe>, probe, compatibility/doctor, compatibility accept --manual-passed, begin-login/cancel-login <profile>, configure-deeplink register/restore/status, bridge <profile> [on/off], inherit-config <profile> <User-dir> [--apply], migrate <legacy-root> --dry-run/--apply, shortcut <profile> [directory], open-data [profile], open-logs, rollback [--force], version. Global: --data-root <absolute-path>, --json. Login is manual. Workspace opening is unsupported pending a verified vendor interface.";
    public static async Task<int> RunAsync(string[] args)
    {
        var json = args.Contains("--json"); var list = args.Where(a => a != "--json").ToList(); string? root = null;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            var index = list.IndexOf("--data-root");
            if (index >= 0)
            {
                if (index + 1 >= list.Count || !Path.IsPathFullyQualified(list[index + 1])) throw new HubException("Data root must be absolute.", "usage_error");
                root = list[index + 1]; list.RemoveRange(index, 2);
            }
            if (list.Count == 0 || list[0] is "help" or "--help" or "-h") { Console.WriteLine(json ? JsonSerializer.Serialize(new { help = Help }, Json) : Help); return 0; }
            if (list[0] is "version" or "--version") { Console.WriteLine(JsonSerializer.Serialize(new { version = HubVersion.Version, application = "AntigravityProfileHubNext" }, Json)); return 0; }
            var result = await ExecuteAsync(new NextHubService(root), list, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, Json)); return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; inspect status before retrying."); return 130; }
        catch (Exception ex)
        {
            var code = ex is HubException known ? known.Code : "operation_failed";
            var message = ex is HubException ? ex.Message : "Operation failed: " + ex.GetType().Name + ". Files preserved; inspect diagnostics.";
            Console.Error.WriteLine(json ? JsonSerializer.Serialize(new { error = new { code, message } }, Json) : code + ": " + message);
            return code == "usage_error" ? 2 : 1;
        }
    }
    private static void Count(IReadOnlyList<string> args, int min, int max)
    { if (args.Count < min || args.Count > max) throw new HubException("Invalid arguments. Run help.", "usage_error"); }
    private static async Task<object> ExecuteAsync(NextHubService hub, List<string> command, CancellationToken token)
    {
        var verb = command[0].ToLowerInvariant(); var tail = command.Skip(1).ToList(); var force = tail.Remove("--force");
        async Task<ProfileRegistration> Profile() { if (tail.Count == 0) throw new HubException("Profile required.", "usage_error"); return await hub.Profiles.ResolveAsync(tail[0], token); }
        switch (verb)
        {
            case "new": case "create": Count(tail, 1, 2); return await hub.CreateAsync(tail[0], tail.Count == 2 ? tail[1] : null, token);
            case "ls": case "list": case "status": Count(tail, 0, 1); return tail.Count == 0 ? await hub.StatusAllAsync(token) : await hub.StatusAsync((await Profile()).Id, token);
            case "open": case "launch": case "iso-launch": Count(tail, 1, 1); return await hub.LaunchAsync((await Profile()).Id, token);
            case "close": case "stop": case "iso-stop": Count(tail, 1, 1); return new { stopped = await hub.StopAsync((await Profile()).Id, force, token), force };
            case "restart": case "iso-restart": Count(tail, 1, 1); return await hub.RestartAsync((await Profile()).Id, force, token);
            case "rename": Count(tail, 2, 2); return await hub.Profiles.RenameAsync((await Profile()).Id, tail[1], token);
            case "workspace": Count(tail, 1, 2); if (tail.Count == 1) return new { profile = await Profile(), opening = "unsupported" }; return await hub.Profiles.SetWorkspaceAsync((await Profile()).Id, tail[1], token);
            case "launch-workspace": throw new UnsupportedCapabilityException("No verified Workspace forwarding interface exists; saved metadata only.");
            case "remove": case "rm": Count(tail, 1, 1); var remove = await Profile(); if ((await hub.StatusAsync(remove.Id, token)).State is not RuntimeState.Stopped and not RuntimeState.Failed) throw new HubException("Stop the profile first.", "profile_not_stopped"); return await hub.Profiles.RemoveAsync(remove.Id, token);
            case "configure-executable": Count(tail, 1, 1); await hub.ConfigureExecutableAsync(tail[0], token); return new { configured = true, next = "probe" };
            case "probe": Count(tail, 0, 0); return await hub.ProbeAsync(token);
            case "diagnose": Count(tail, 0, 0); var diagnosticInstall = await hub.CurrentInstallationAsync(token);
                try { var snapshot = await new WindowsProcessCatalog().SnapshotAsync(diagnosticInstall.Executable, token); return new { processEnumeration = "available", observedProcesses = snapshot.Count, profiles = await hub.StatusAllAsync(token) }; }
                catch (Exception ex) { return new { processEnumeration = "unavailable", errorType = ex.GetType().Name, innerType = ex.InnerException?.GetType().Name }; }
            case "doctor": case "compatibility":
                if (tail.SequenceEqual(new[] { "accept", "--manual-passed" })) { await hub.AcceptAsync(token); return new { accepted = true }; }
                Count(tail, 0, 0); return await hub.CompatibilityAsync(token);
            case "begin-login": Count(tail, 1, 1); await hub.BeginLoginAsync((await Profile()).Id, token); return new { waiting = true };
            case "cancel-login": Count(tail, 1, 1); await hub.EndLoginAsync((await Profile()).Id, token); return new { cancelled = true };
            case "callback": Count(tail, 1, 1); return new { handoffPid = await hub.RouteCallbackAsync(tail[0], token) };
            case "configure-deeplink":
                Count(tail, 1, 1);
                if (tail[0] == "register") { await hub.RegisterProtocolAsync(Environment.ProcessPath!, token); return new { registered = true }; }
                if (tail[0] == "restore") return new { restored = await hub.RestoreProtocolAsync(token) };
                if (tail[0] == "status") return new { registered = await hub.IsProtocolRegisteredAsync(token) };
                throw new HubException("Use register, restore or status.", "usage_error");
            case "bridge": Count(tail, 1, 2); var bridge = await Profile(); if (tail.Count == 1) return await hub.Bridge.ReadAsync(bridge.Id, token);
                if (tail[1] is not "on" and not "off") throw new HubException("Bridge policy must be on or off.", "usage_error");
                return await hub.Bridge.ApplyAsync(bridge.Id, tail[1] == "on", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), token);
            case "inherit-config": var apply = tail.Remove("--apply"); Count(tail, 2, 2); return await NextConfiguration.InheritAsync(hub.Paths, (await Profile()).Id, tail[1], apply, token);
            case "migrate": var dry = tail.Remove("--dry-run"); var migrate = tail.Remove("--apply"); Count(tail, 1, 1);
                if (dry == migrate) throw new HubException("Choose --dry-run or --apply.", "usage_error"); return await NextMigration.RunAsync(hub, tail[0], dry, token);
            case "shortcut": if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); Count(tail, 1, 2); return new { path = DesktopShortcut.Create(await Profile(), hub.Paths.DataRoot, Environment.ProcessPath!, tail.Count == 2 ? tail[1] : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) };
            case "open-data": Count(tail, 0, 1); OpenFolder(tail.Count == 0 ? hub.Paths.DataRoot : hub.Paths.ProfileRoot((await Profile()).Id)); return new { opened = true };
            case "open-logs": Count(tail, 0, 0); OpenFolder(hub.Paths.LogsRoot); return new { opened = true };
            case "rollback": Count(tail, 0, 0); return new { protocolRestored = await hub.RollbackAsync(force, token), legacy2191Support = "not-guaranteed" };
            default: throw new HubException("Unknown command. Run help.", "usage_error");
        }
    }
    private static void OpenFolder(string path)
    { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(path); Process.Start(info)?.Dispose(); }
}
