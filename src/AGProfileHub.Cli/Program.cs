using System.Text.Json;
using System.Text.Json.Serialization;
using AGProfileHub.Core;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        var jsonRequested = arguments.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));
        try
        {
            var parsed = ParseGlobalOptions(arguments);
            if (parsed.Command.Count == 0 || IsHelp(parsed.Command[0]))
            {
                if (parsed.Json) WriteJson(new { help = HelpText() });
                else PrintHelp();
                return 0;
            }

            if (parsed.Command[0] is "version" or "--version" or "-v")
            {
                RequireNoArguments(parsed.Command.Skip(1).ToList(), "version");
                if (parsed.Json) WriteJson(new { version = HubVersion.Version, build = HubVersion.InformationalVersion, schemaVersion = SchemaVersions.Current });
                else Console.WriteLine($"AGProfileHub {HubVersion.InformationalVersion}");
                return 0;
            }

            // The internal shim runs before bootstrap and must prove it is inside
            // the requested box before any credential API can be reached.
            if (parsed.Command[0].Equals("credential-shim", StringComparison.OrdinalIgnoreCase))
            {
                var tail = parsed.Command.Skip(1).ToList();
                var expectedBox = TakeOption(tail, "--box-id");
                var force = TakeFlag(tail, "--force");
                var fresh = TakeFlag(tail, "--fresh");
                RequireNoArguments(tail, "credential-shim --box-id <box> [--force --fresh]");
                if (expectedBox is null || force != fresh)
                    throw new UsageException("credential-shim requires --box-id; --force and --fresh must be supplied together.");
                WindowsSandboxieRuntime.ValidateBoxId(expectedBox);
                var shim = AntigravityKeyring.EnsureIsolation(force && fresh, expectedBox);
                if (parsed.Json) WriteJson(shim);
                else Console.WriteLine($"credential-shim: target={shim.Target} existedBefore={shim.ExistedBefore} created={shim.Created} blobSize={shim.BlobSize}");
                // Exit codes let a caller that cannot capture sandboxed stdout
                // still tell "claimed now" from "already owned".
                return shim.Created ? 11 : 0;
            }

            var hub = HubBootstrap.CreateDefault(parsed.DataRoot);
            var result = await ExecuteAsync(hub, parsed.Command, parsed.Json).ConfigureAwait(false);
            if (parsed.Json && result is not null)
                WriteJson(result);
            if (result is AgentProbeResult probe && probe.Outcome != "success") return 1;
            return 0;
        }
        catch (UsageException ex) { return WriteError(ex.Message, "usage_error", 2, jsonRequested); }
        catch (ProfileNotFoundException ex) { return WriteError(ex.Message, ex.Code, 3, jsonRequested); }
        catch (UnsupportedCapabilityException ex) { return WriteError(ex.Message, ex.Code, 4, jsonRequested); }
        catch (ProcessOwnershipException ex) { return WriteError(ex.Message, ex.Code, 5, jsonRequested); }
        catch (HubException ex) { return WriteError(ex.Message, ex.Code, ex.Code == "profile_not_found" ? 3 : 1, jsonRequested); }
        catch (OperationCanceledException) { return WriteError("Operation cancelled.", "cancelled", 130, jsonRequested); }
        catch (Exception ex) { return WriteError($"Unexpected failure: {ex.GetType().Name}", "unexpected_error", 1, jsonRequested); }
    }

    private static async Task<object?> ExecuteAsync(HubService hub, IReadOnlyList<string> command, bool json)
    {
        var verb = command[0].ToLowerInvariant();
        var tail = command.Skip(1).ToList();
        switch (verb)
        {
            case "agent-probe":
                RequireNoArguments(tail, "agent-probe");
                using (var cancellation = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        var installation = await hub.DoctorAsync().ConfigureAwait(false);
                        if (installation.ExecutablePath is null) throw new HubException("Antigravity was not found.", "executable_not_found");
                        var probe = await AgentRuntimeProbe.RunAsync(installation.ExecutablePath, hub.DataRoot, cancellation.Token).ConfigureAwait(false);
                        if (!json) Console.WriteLine($"Agent probe: {probe.Outcome}; owned listener={probe.ListenerOwned}; CLI exit={probe.ExitCode}");
                        return probe;
                    }
                    finally { Console.CancelKeyPress -= handler; }
                }
            case "doctor":
                RequireNoArguments(tail, "doctor");
                var report = await hub.DoctorAsync().ConfigureAwait(false);
                if (!json) PrintDoctor(report);
                return report;
            case "credential-status":
                RequireNoArguments(tail, "credential-status");
                var keyring = AntigravityKeyring.BuildStatus();
                if (!json) PrintCredentialStatus(keyring);
                return keyring;
            case "list":
                RequireNoArguments(tail, "list");
                var profiles = await hub.ListProfilesAsync().ConfigureAwait(false);
                if (!json) PrintProfiles(profiles);
                return profiles;
            case "create":
                var createWorkspace = TakeOption(tail, "--workspace");
                var createBackend = TakeOption(tail, "--backend") ?? "direct";
                RequireCount(tail, 1, "create <name> [--workspace <path>] [--backend direct|sandboxie]");
                var backend = ParseBackend(createBackend);
                var created = await hub.CreateProfileAsync(tail[0], createWorkspace, backend).ConfigureAwait(false);
                if (!json)
                {
                    Console.WriteLine($"Created {created.Name} ({created.Id:D}) backend={created.Backend}" +
                        (created.BoxId is null ? "" : $" box={created.BoxId}"));
                }
                return created;
            case "rename":
                RequireCount(tail, 2, "rename <id> <new-name>");
                var renamed = await hub.RenameProfileAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false), tail[1]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Renamed {renamed.Id:D} to {renamed.Name}");
                return renamed;
            case "remove":
                RequireCount(tail, 1, "remove <id>");
                var removed = await hub.RemoveProfileAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false)).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Unregistered {removed.Name}; profile data was preserved.");
                return removed;
            case "show":
                RequireCount(tail, 1, "show <id>");
                var shown = await hub.GetProfileAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false)).ConfigureAwait(false);
                if (!json) PrintProfile(shown);
                return shown;
            case "workspace":
                RequireCount(tail, 2, "workspace <id> <path|none>");
                var workspace = tail[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : tail[1];
                var updated = await hub.SetWorkspaceAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false), workspace).ConfigureAwait(false);
                if (!json) Console.WriteLine(updated.WorkspacePath is null ? "Workspace metadata cleared." : $"Workspace metadata: {updated.WorkspacePath}");
                return updated;
            case "launch":
                var launchWorkspace = TakeOption(tail, "--workspace");
                RequireCount(tail, 1, "launch <id> [--workspace <path>]");
                var launchId = await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false);
                if (launchWorkspace is not null)
                {
                    await hub.LaunchWithWorkspaceAsync(launchId, launchWorkspace).ConfigureAwait(false);
                    return null;
                }
                var launch = await hub.LaunchAsync(launchId).ConfigureAwait(false);
                if (!json) PrintStatus(launch.Status);
                return launch;
            case "launch-all":
                RequireNoArguments(tail, "launch-all");
                var launches = await hub.LaunchAllAsync().ConfigureAwait(false);
                if (!json) foreach (var item in launches) PrintStatus(item.Status);
                return launches;
            case "stop":
                var forceStop = TakeFlag(tail, "--force");
                RequireCount(tail, 1, "stop <id> [--force]");
                var stop = await hub.StopAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false), forceStop).ConfigureAwait(false);
                if (!json) PrintStatus(stop.Status);
                EnsureStopped(stop);
                return stop;
            case "stop-all":
                var forceAll = TakeFlag(tail, "--force");
                RequireNoArguments(tail, "stop-all [--force]");
                var stops = new List<StopResult>();
                foreach (var profile in await hub.ListProfilesAsync().ConfigureAwait(false))
                    stops.Add(await hub.StopAsync(profile.Id, forceAll).ConfigureAwait(false));
                if (!json) stops.ForEach(item => PrintStatus(item.Status));
                var pending = stops.FirstOrDefault(item => item.Status.State != RuntimeState.Stopped);
                if (pending is not null) EnsureStopped(pending);
                return stops;
            case "restart":
                var forceRestart = TakeFlag(tail, "--force");
                RequireCount(tail, 1, "restart <id> [--force]");
                var restart = await hub.RestartAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false), forceRestart).ConfigureAwait(false);
                if (!json) PrintStatus(restart.Status);
                return restart;
            case "status":
                if (tail.Count > 1) throw new UsageException("Usage: status [id]");
                if (tail.Count == 1)
                {
                    var one = await hub.GetStatusAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false)).ConfigureAwait(false);
                    if (!json) PrintStatus(one);
                    return one;
                }
                var all = await hub.GetAllStatusesAsync().ConfigureAwait(false);
                if (!json) foreach (var item in all) PrintStatus(item);
                return all;
            case "open-data":
                if (tail.Count > 1) throw new UsageException("Usage: open-data [id]");
                Guid? dataId = tail.Count == 1 ? await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false) : null;
                hub.OpenDataFolder(dataId);
                var opened = dataId is null ? hub.DataRoot : hub.GetProfileDataRoot(dataId.Value);
                if (!json) Console.WriteLine($"Opened {opened}");
                return new { opened };
            case "open-logs":
                RequireNoArguments(tail, "open-logs");
                hub.OpenLogsFolder();
                if (!json) Console.WriteLine($"Opened {hub.LogsRoot}");
                return new { opened = hub.LogsRoot };
            case "settings":
                RequireNoArguments(tail, "settings");
                var currentSettings = await hub.GetSettingsAsync().ConfigureAwait(false);
                if (!json)
                {
                    Console.WriteLine($"Data root: {hub.DataRoot}");
                    Console.WriteLine($"Antigravity: {currentSettings.AntigravityExecutablePath ?? "auto"}");
                    Console.WriteLine($"Sandboxie (optional): {currentSettings.SandboxieHome ?? "auto"}");
                }
                return currentSettings;
            case "configure-sandboxie":
                RequireCount(tail, 1, "configure-sandboxie <directory|auto>");
                var sandboxSettings = await hub.ConfigureSandboxieAsync(tail[0].Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : tail[0]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Sandboxie directory: {sandboxSettings.SandboxieHome ?? "auto"}. Direct profiles do not require Sandboxie.");
                return sandboxSettings;
            case "configure-executable":
                RequireCount(tail, 1, "configure-executable <path>");
                var settings = await hub.ConfigureExecutableAsync(tail[0]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Configured {settings.AntigravityExecutablePath}");
                return settings;
            case "shortcut":
                var shortcutDirectory = TakeOption(tail, "--directory");
                RequireCount(tail, 1, "shortcut <id> [--directory <path>]");
                var shortcut = await hub.CreateShortcutAsync(await hub.ResolveProfileIdAsync(tail[0]).ConfigureAwait(false), DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory), shortcutDirectory).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Created shortcut: {shortcut}");
                return new { shortcut };
            case "help":
                if (json) return new { help = HelpText() };
                PrintHelp();
                return null;
            default:
                throw new UsageException($"Unknown command '{command[0]}'. Run 'agprofile help'.");
        }
    }

    private static (string? DataRoot, bool Json, List<string> Command) ParseGlobalOptions(string[] arguments)
    {
        string? dataRoot = null;
        var json = false;
        var command = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
            if (argument.Equals("--data-root", StringComparison.OrdinalIgnoreCase))
            {
                if (dataRoot is not null) throw new UsageException("Specify --data-root only once.");
                if (++index >= arguments.Length) throw new UsageException("--data-root requires an absolute path.");
                dataRoot = arguments[index];
                if (!Path.IsPathFullyQualified(dataRoot)) throw new UsageException("--data-root must be an absolute path.");
                continue;
            }
            command.Add(argument);
        }
        return (dataRoot, json, command);
    }

    private static string? TakeOption(List<string> values, string option)
    {
        var index = values.FindIndex(value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= values.Count) throw new UsageException($"{option} requires a value.");
        var result = values[index + 1];
        values.RemoveRange(index, 2);
        return result;
    }

    private static bool TakeFlag(List<string> values, string flag)
    {
        var found = false;
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (!values[index].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            values.RemoveAt(index);
            found = true;
        }
        return found;
    }

    private static void RequireCount(IReadOnlyCollection<string> values, int count, string usage)
    {
        if (values.Count != count) throw new UsageException($"Usage: {usage}");
    }

    private static void RequireNoArguments(IReadOnlyCollection<string> values, string usage) => RequireCount(values, 0, usage);
    private static bool IsHelp(string value) => value is "-h" or "--help" or "/?";
    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));

    private static int WriteError(string message, string code, int exitCode, bool json)
    {
        if (json) Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { code, message } }, JsonOptions));
        else Console.Error.WriteLine($"Error [{code}]: {message}");
        return exitCode;
    }

    private static void PrintProfiles(IReadOnlyList<ProfileRegistration> profiles)
    {
        if (profiles.Count == 0) { Console.WriteLine("No profiles yet. Run: agprofile create \"Work\" (Direct, no Sandboxie required)."); return; }
        foreach (var profile in profiles)
            Console.WriteLine($"{profile.Id:D}  {profile.Name}  [{profile.Backend}]  {profile.WorkspacePath ?? "(no workspace metadata)"}");
    }

    private static void PrintProfile(ProfileRegistration profile)
    {
        Console.WriteLine($"ID:        {profile.Id:D}");
        Console.WriteLine($"Name:      {profile.Name}");
        Console.WriteLine($"Backend:   {profile.Backend}");
        if (profile.BoxId is not null) Console.WriteLine($"Sandbox:   {profile.BoxId}");
        Console.WriteLine($"Workspace: {profile.WorkspacePath ?? "(none)"}");
        Console.WriteLine($"Created:   {profile.CreatedUtc:O}");
        Console.WriteLine($"Updated:   {profile.UpdatedUtc:O}");
    }

    private static void PrintStatus(ProfileStatus status) =>
        Console.WriteLine($"{status.ProfileId:D}  {status.Name}  {status.State}  PID {status.Root?.ProcessId.ToString() ?? "-"}  {status.Detail}");

    private static void PrintDoctor(DoctorReport report)
    {
        Console.WriteLine($"Antigravity executable : {(report.ExecutableFound ? report.ExecutablePath : "NOT FOUND")}");
        Console.WriteLine($"Version                 : {report.ExecutableVersion ?? "unknown"}");
        Console.WriteLine($"Experimental compatible : {report.CompatibleForExperimentalLaunch}");
        foreach (var capability in report.Capabilities)
            Console.WriteLine($"{capability.Name,-24}: {capability.State} - {capability.Detail}");
        foreach (var warning in report.Warnings) Console.WriteLine($"WARNING: {warning}");
    }

    private static void PrintCredentialStatus(KeyringStatusResult status)
    {
        Console.WriteLine($"Shared token target    : {status.Target}");
        Console.WriteLine($"Present for this user  : {status.HostEntryExists}");
        Console.WriteLine($"Entry size (bytes)     : {status.HostEntryBlobSize}");
        Console.WriteLine($"Owned by vendor client : {status.HostEntryBelongsToVendor}");
        Console.WriteLine($"Guidance               : {status.IsolationGuidance}");
        Console.WriteLine($"Note                   : {status.Note}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(HelpText());
    }

    private static string HelpText() => $"""
        Antigravity Profile Hub {HubVersion.Version}
        快速开始（普通 Windows 模式，不需要 Sandboxie）：
          agprofile doctor
          agprofile create "Work"
          agprofile launch "Work"
          agprofile stop "Work"

        Profile 参数可用 UUID 或唯一名称；同名时必须使用 UUID。
        全局选项：--data-root <绝对路径>  --json
        常用命令：
          version | --version | doctor | settings | list | status [id] | show <id>
          create <name> [--workspace <path>] [--backend direct|sandboxie]
          rename <id> <new-name> | workspace <id> <path|none> | remove <id>
          launch <id> | launch-all | stop <id> [--force] | stop-all [--force]
          restart <id> [--force] | open-data [id] | open-logs
          configure-executable <path> | configure-sandboxie <directory|auto>
          shortcut <id> [--directory <path>]
        高级诊断：
          agent-probe        Mocked LS/official CLI probe, no real account
          credential-status  Metadata only, never prints the token
        内部命令：credential-shim --box-id <box> [--force --fresh]
          Only for the Hub-created, verified Sandboxie box; never run on the host.

        默认 Direct 保留串行门禁；Sandboxie 可选且仍为实验功能，双账号须人工验收。
        登录由官方 Antigravity 完成；工作区仅元数据，remove 仅注销、不删除数据。
        """;

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State == RuntimeState.Stopped) return;
        throw new HubException(result.Status.Detail ?? "The profile is still running.", "graceful_close_pending");
    }

    private static ProfileBackend ParseBackend(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "direct" => ProfileBackend.Direct,
            "sandboxie" or "sandbox" => ProfileBackend.Sandboxie,
            _ => throw new UsageException("Unknown backend. Use --backend direct|sandboxie.")
        };

    private sealed class UsageException(string message) : Exception(message);
}
