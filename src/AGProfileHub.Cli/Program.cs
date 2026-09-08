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

            var hub = HubBootstrap.CreateDefault(parsed.DataRoot);
            var result = await ExecuteAsync(hub, parsed.Command, parsed.Json).ConfigureAwait(false);
            if (parsed.Json && result is not null)
                WriteJson(result);
            return 0;
        }
        catch (UsageException ex) { return WriteError(ex.Message, "usage_error", 2, jsonRequested); }
        catch (ProfileNotFoundException ex) { return WriteError(ex.Message, ex.Code, 3, jsonRequested); }
        catch (UnsupportedCapabilityException ex) { return WriteError(ex.Message, ex.Code, 4, jsonRequested); }
        catch (ProcessOwnershipException ex) { return WriteError(ex.Message, ex.Code, 5, jsonRequested); }
        catch (HubException ex) { return WriteError(ex.Message, ex.Code, 1, jsonRequested); }
        catch (OperationCanceledException) { return WriteError("Operation cancelled.", "cancelled", 130, jsonRequested); }
        catch (Exception ex) { return WriteError($"Unexpected failure: {ex.GetType().Name}", "unexpected_error", 1, jsonRequested); }
    }

    private static async Task<object?> ExecuteAsync(HubService hub, IReadOnlyList<string> command, bool json)
    {
        var verb = command[0].ToLowerInvariant();
        var tail = command.Skip(1).ToList();
        switch (verb)
        {
            case "doctor":
                RequireNoArguments(tail, "doctor");
                var report = await hub.DoctorAsync().ConfigureAwait(false);
                if (!json) PrintDoctor(report);
                return report;
            case "list":
                RequireNoArguments(tail, "list");
                var profiles = await hub.ListProfilesAsync().ConfigureAwait(false);
                if (!json) PrintProfiles(profiles);
                return profiles;
            case "create":
                var createWorkspace = TakeOption(tail, "--workspace");
                RequireCount(tail, 1, "create <name> [--workspace <path>]");
                var created = await hub.CreateProfileAsync(tail[0], createWorkspace).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Created {created.Name} ({created.Id:D})");
                return created;
            case "rename":
                RequireCount(tail, 2, "rename <id> <new-name>");
                var renamed = await hub.RenameProfileAsync(ParseId(tail[0]), tail[1]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Renamed {renamed.Id:D} to {renamed.Name}");
                return renamed;
            case "remove":
                RequireCount(tail, 1, "remove <id>");
                var removed = await hub.RemoveProfileAsync(ParseId(tail[0])).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Unregistered {removed.Name}; profile data was preserved.");
                return removed;
            case "show":
                RequireCount(tail, 1, "show <id>");
                var shown = await hub.GetProfileAsync(ParseId(tail[0])).ConfigureAwait(false);
                if (!json) PrintProfile(shown);
                return shown;
            case "workspace":
                RequireCount(tail, 2, "workspace <id> <path|none>");
                var workspace = tail[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : tail[1];
                var updated = await hub.SetWorkspaceAsync(ParseId(tail[0]), workspace).ConfigureAwait(false);
                if (!json) Console.WriteLine(updated.WorkspacePath is null ? "Workspace metadata cleared." : $"Workspace metadata: {updated.WorkspacePath}");
                return updated;
            case "launch":
                var launchWorkspace = TakeOption(tail, "--workspace");
                RequireCount(tail, 1, "launch <id> [--workspace <path>]");
                var launchId = ParseId(tail[0]);
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
                var launches = new List<LaunchResult>();
                foreach (var profile in await hub.ListProfilesAsync().ConfigureAwait(false))
                    launches.Add(await hub.LaunchAsync(profile.Id).ConfigureAwait(false));
                if (!json) launches.ForEach(item => PrintStatus(item.Status));
                return launches;
            case "stop":
                var forceStop = TakeFlag(tail, "--force");
                RequireCount(tail, 1, "stop <id> [--force]");
                var stop = await hub.StopAsync(ParseId(tail[0]), forceStop).ConfigureAwait(false);
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
                var restart = await hub.RestartAsync(ParseId(tail[0]), forceRestart).ConfigureAwait(false);
                if (!json) PrintStatus(restart.Status);
                return restart;
            case "status":
                if (tail.Count > 1) throw new UsageException("Usage: status [id]");
                if (tail.Count == 1)
                {
                    var one = await hub.GetStatusAsync(ParseId(tail[0])).ConfigureAwait(false);
                    if (!json) PrintStatus(one);
                    return one;
                }
                var all = await hub.GetAllStatusesAsync().ConfigureAwait(false);
                if (!json) foreach (var item in all) PrintStatus(item);
                return all;
            case "open-data":
                if (tail.Count > 1) throw new UsageException("Usage: open-data [id]");
                Guid? dataId = tail.Count == 1 ? ParseId(tail[0]) : null;
                hub.OpenDataFolder(dataId);
                var opened = dataId is null ? hub.DataRoot : hub.GetProfileDataRoot(dataId.Value);
                if (!json) Console.WriteLine($"Opened {opened}");
                return new { opened };
            case "open-logs":
                RequireNoArguments(tail, "open-logs");
                hub.OpenLogsFolder();
                if (!json) Console.WriteLine($"Opened {hub.LogsRoot}");
                return new { opened = hub.LogsRoot };
            case "configure-executable":
                RequireCount(tail, 1, "configure-executable <path>");
                var settings = await hub.ConfigureExecutableAsync(tail[0]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Configured {settings.AntigravityExecutablePath}");
                return settings;
            case "shortcut":
                var shortcutDirectory = TakeOption(tail, "--directory");
                RequireCount(tail, 1, "shortcut <id> [--directory <path>]");
                var shortcut = await hub.CreateShortcutAsync(ParseId(tail[0]), DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory), shortcutDirectory).ConfigureAwait(false);
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

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new UsageException($"Invalid profile ID '{value}'. Expected a UUID.");

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
        if (profiles.Count == 0) { Console.WriteLine("No profiles."); return; }
        foreach (var profile in profiles)
            Console.WriteLine($"{profile.Id:D}  {profile.Name}  {profile.WorkspacePath ?? "(no workspace metadata)"}");
    }

    private static void PrintProfile(ProfileRegistration profile)
    {
        Console.WriteLine($"ID:        {profile.Id:D}");
        Console.WriteLine($"Name:      {profile.Name}");
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

    private static void PrintHelp()
    {
        Console.WriteLine(HelpText());
    }

    private static string HelpText() => """
        Antigravity Profile Hub CLI (experimental)
        Global options: --data-root <absolute-path>  --json
        Commands:
          doctor | list | status [id] | show <id>
          create <name> [--workspace <path>] | rename <id> <new-name>
          workspace <id> <path|none> | remove <id>
          launch <id> [--workspace <path>] | launch-all
          stop <id> [--force] | stop-all [--force] | restart <id> [--force]
          open-data [id] | open-logs | configure-executable <path>
          shortcut <id> [--directory <path>]
        Authentication is performed only by official Antigravity; account isolation requires manual verification.
        """;

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State == RuntimeState.Stopped) return;
        throw new HubException(result.Status.Detail ?? "The profile is still running.", "graceful_close_pending");
    }

    private sealed class UsageException(string message) : Exception(message);
}
