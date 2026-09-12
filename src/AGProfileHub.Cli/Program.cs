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
                if (parsed.Json) WriteJson(new { version = HubVersion.Version, build = HubVersion.InformationalVersion });
                else Console.WriteLine($"AGProfileHub {HubVersion.InformationalVersion}");
                return 0;
            }

            // Runs before any Hub bootstrap: this verb is what the vendor's deep
            // link is pointed at. It hands the URI to the Profile that is waiting
            // for it, and must never create a Hub data root.
            if (parsed.Command[0].Equals("callback", StringComparison.OrdinalIgnoreCase))
            {
                var tail = parsed.Command.Skip(1).ToList();
                RequireCount(tail, 1, "callback <uri>");
                var routed = DeepLinkRouter.Route(new HubPaths(parsed.DataRoot), tail[0]);
                if (parsed.Json) WriteJson(new { routed });
                else Console.WriteLine(routed == 0
                    ? "callback: no profile is waiting for a deep-link callback."
                    : $"callback: handed the URI to the waiting profile (handoff pid {routed}).");
                return 0;
            }

            var hub = HubBootstrap.CreateDefault(parsed.DataRoot);
            var result = await ExecuteAsync(hub, parsed.Command, parsed.Json).ConfigureAwait(false);
            if (parsed.Json && result is not null)
                WriteJson(result);
            if (result is AgentProbeResult probe && probe.Outcome != "success") return 1;
            if (result is DoctorReport doctor && (!doctor.ExecutableFound || !doctor.CompatibleForExperimentalLaunch)) return 1;
            return 0;
        }
        catch (UsageException ex) { return WriteError(ex.Message, "usage_error", 2, jsonRequested); }
        catch (ProfileNotFoundException ex) { return WriteError(ex.Message, ex.Code, 3, jsonRequested); }
        catch (UnsupportedCapabilityException ex) { return WriteError(ex.Message, ex.Code, 4, jsonRequested); }
        catch (ProcessOwnershipException ex) { return WriteError(ex.Message, ex.Code, 5, jsonRequested); }
        catch (HubException ex) { return WriteError(ex.Message, ex.Code, 1, jsonRequested); }
        catch (OperationCanceledException) { return WriteError("Operation cancelled.", "cancelled", 130, jsonRequested); }
        catch (Exception ex)
        {
            return WriteError($"Unexpected failure: {ex.GetType().Name}: {ex.Message}. " +
                "Run 'agprofile open-logs' and keep the log when reporting this.",
                "unexpected_error", 1, jsonRequested);
        }
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
            case "iso-prepare":
                var isoBox = TakeOption(tail, "--box");
                var isoFresh = TakeFlag(tail, "--fresh");
                RequireCount(tail, 1, "iso-prepare <id> [--box <existing-box-name>] [--fresh]");
                var isoPrepare = await hub.PrepareIsolationAsync(await ResolveIdAsync(hub, tail[0]), isoBox, isoFresh).ConfigureAwait(false);
                if (!json)
                    Console.WriteLine($"isolation: scope={isoPrepare.BoxName} scopeCreated={isoPrepare.BoxCreated} mappingCreated={isoPrepare.MappingCreated}");
                return isoPrepare;
            case "iso-launch":
                RequireCount(tail, 1, "iso-launch <id>");
                var isoLaunchId = await ResolveIdAsync(hub, tail[0]);
                if (await hub.GetIsolationStatusAsync(isoLaunchId).ConfigureAwait(false) is null)
                    throw new HubException("This profile has no isolation scope yet. Run iso-prepare first.", "isolation_not_prepared");
                var isoLaunch = await hub.LaunchAsync(isoLaunchId).ConfigureAwait(false);
                if (!json) PrintStatus(isoLaunch.Status);
                return isoLaunch;
            case "iso-stop":
                RequireCount(tail, 1, "iso-stop <id>");
                var isoStopId = await ResolveIdAsync(hub, tail[0]);
                if (await hub.GetIsolationStatusAsync(isoStopId).ConfigureAwait(false) is null)
                    throw new HubException("This profile has no isolation scope yet. Run iso-prepare first.", "isolation_not_prepared");
                var isoStopped = await hub.StopAsync(isoStopId, force: true).ConfigureAwait(false);
                if (!json) PrintStatus(isoStopped.Status);
                return isoStopped;
            case "iso-status":
                RequireNoArguments(tail, "iso-status");
                var isoStatus = await hub.GetIsolationStatusesAsync().ConfigureAwait(false);
                if (!json)
                    foreach (var item in isoStatus)
                        Console.WriteLine($"{item.BoxName}  profile={item.ProfileId}  scopeExists={item.BoxExists}  processes={item.ProcessCount}");
                return isoStatus;
            case "list":
            case "ls":
                RequireNoArguments(tail, "list");
                var profiles = await hub.ListProfilesAsync().ConfigureAwait(false);
                if (!json) PrintProfiles(profiles);
                return profiles;
            case "setup":
                RequireNoArguments(tail, "setup");
                var setupDoctor = await hub.DoctorAsync().ConfigureAwait(false);
                if (!json) PrintDoctor(setupDoctor);
                return setupDoctor;
            case "new":
            case "create":
                // 'create' is kept as an alias: it used to skip isolation
                // preparation, which left profiles on the legacy serial path.
                var newWorkspace = TakeOption(tail, "--workspace");
                var newFresh = TakeFlag(tail, "--fresh");
                var newDirect = TakeFlag(tail, "--direct");
                var newBackendText = TakeOption(tail, "--backend");
                RequireCount(tail, 1, $"{verb} <name> [--workspace <path>] [--backend <process-local>] [--direct] [--fresh]");
                var newProfile = await hub.CreateProfileAsync(tail[0], newWorkspace,
                    newBackendText is null ? null : ParseBackend(newBackendText)).ConfigureAwait(false);
                if (newDirect)
                {
                    if (!json) Console.WriteLine($"Created {newProfile.Name} ({newProfile.Id:D}) backend=direct");
                    return newProfile;
                }
                try
                {
                    var ready = await hub.EnsureIsolationReadyAsync(newProfile.Id, newFresh).ConfigureAwait(false);
                    if (!json) Console.WriteLine($"Created {newProfile.Name} ({newProfile.Id:D}); isolation {ready.Stage}.");
                    return new { profile = newProfile, setup = ready };
                }
                catch (UnsupportedCapabilityException ex)
                {
                    if (!json) Console.WriteLine($"Created {newProfile.Name} ({newProfile.Id:D}); isolation unavailable: {ex.Message}");
                    return newProfile;
                }
            case "open":
                var openDirect = TakeFlag(tail, "--direct");
                RequireCount(tail, 1, "open <name-or-id> [--direct]");
                var openId = await ResolveIdAsync(hub, tail[0]).ConfigureAwait(false);
                if (!openDirect && await hub.DetectIsolationStageAsync(openId).ConfigureAwait(false) != IsolationStage.NotStarted)
                {
                    var ready = await hub.EnsureIsolationReadyAsync(openId, allowFreshBox: false).ConfigureAwait(false);
                    if (!ready.Ready) throw new HubException($"Isolation is not ready: {ready.Stage}. Run 'fix {tail[0]}' to diagnose.", "isolation_not_ready");
                }
                var openResult = await hub.LaunchAsync(openId).ConfigureAwait(false);
                if (!json) PrintStatus(openResult.Status);
                return openResult;
            case "close":
                var closeForce = TakeFlag(tail, "--force");
                RequireCount(tail, 1, "close <name-or-id> [--force]");
                var closeId = await ResolveIdAsync(hub, tail[0]).ConfigureAwait(false);
                var closed = await hub.StopAsync(closeId, closeForce).ConfigureAwait(false);
                if (!json) PrintStatus(closed.Status);
                // Same contract as 'stop': a graceful close that did not finish
                // is an error the caller must see, not a silent exit 0.
                EnsureStopped(closed);
                return closed;
            case "fix":
                RequireCount(tail, 1, "fix <name-or-id>");
                var fixId = await ResolveIdAsync(hub, tail[0]).ConfigureAwait(false);
                var repaired = await hub.EnsureIsolationReadyAsync(fixId, allowFreshBox: false).ConfigureAwait(false);
                if (!json)
                {
                    foreach (var step in repaired.Steps)
                        Console.WriteLine($"  {(step.Applied ? "fixed" : "ok   ")}  {step.Stage} {(step.Detail ?? "")}");
                    Console.WriteLine($"Isolation: {repaired.Stage}");
                }
                return repaired;
            case "rename":
                RequireCount(tail, 2, "rename <id> <new-name>");
                var renamed = await hub.RenameProfileAsync(await ResolveIdAsync(hub, tail[0]), tail[1]).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Renamed {renamed.Id:D} to {renamed.Name}");
                return renamed;
            case "remove":
                RequireCount(tail, 1, "remove <id>");
                var removed = await hub.RemoveProfileAsync(await ResolveIdAsync(hub, tail[0])).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Unregistered {removed.Name}; profile data was preserved.");
                return removed;
            case "show":
                RequireCount(tail, 1, "show <id>");
                var shown = await hub.GetProfileAsync(await ResolveIdAsync(hub, tail[0])).ConfigureAwait(false);
                if (!json) PrintProfile(shown);
                return shown;
            case "workspace":
                RequireCount(tail, 2, "workspace <id> <path|none>");
                var workspace = tail[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : tail[1];
                var updated = await hub.SetWorkspaceAsync(await ResolveIdAsync(hub, tail[0]), workspace).ConfigureAwait(false);
                if (!json) Console.WriteLine(updated.WorkspacePath is null ? "Workspace metadata cleared." : $"Workspace metadata: {updated.WorkspacePath}");
                return updated;
            case "launch":
                var launchWorkspace = TakeOption(tail, "--workspace");
                RequireCount(tail, 1, "launch <id> [--workspace <path>]");
                var launchId = await ResolveIdAsync(hub, tail[0]);
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
                var stopId = await ResolveIdAsync(hub, tail[0]);
                var stop = await hub.StopAsync(stopId, forceStop).ConfigureAwait(false);
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
                var restart = await hub.RestartAsync(await ResolveIdAsync(hub, tail[0]), forceRestart).ConfigureAwait(false);
                if (!json) PrintStatus(restart.Status);
                return restart;
            case "status":
                if (tail.Count > 1) throw new UsageException("Usage: status [id]");
                if (tail.Count == 1)
                {
                    var one = await hub.GetStatusAsync(await ResolveIdAsync(hub, tail[0])).ConfigureAwait(false);
                    if (!json) PrintStatus(one);
                    return one;
                }
                var all = await hub.GetAllStatusesAsync().ConfigureAwait(false);
                if (!json) foreach (var item in all) PrintStatus(item);
                return all;
            case "open-data":
                if (tail.Count > 1) throw new UsageException("Usage: open-data [id]");
                Guid? dataId = tail.Count == 1 ? await ResolveIdAsync(hub, tail[0]) : null;
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
            case "configure-isolation":
                RequireCount(tail, 1, "configure-isolation <process-local>");
                var configuredBackend = await hub.ConfigureDefaultBackendAsync(ParseBackend(tail[0])).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Default isolation backend for new profiles: {configuredBackend.DefaultBackend}");
                return configuredBackend;
            case "configure-deeplink":
                RequireCount(tail, 1, "configure-deeplink <register|restore>");
                var deeplinkAction = tail[0].ToLowerInvariant();
                if (deeplinkAction is not ("register" or "restore"))
                    throw new UsageException("configure-deeplink expects register or restore.");
                if (deeplinkAction == "register")
                {
                    DeepLinkRouter.Register(hub.Paths, DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory));
                    if (!json) Console.WriteLine("The antigravity:// scheme now hands its callback to the Hub.");
                }
                else
                {
                    var restored = DeepLinkRouter.Restore(hub.Paths);
                    if (!json) Console.WriteLine(restored
                        ? "The antigravity:// scheme was restored."
                        : "The Hub does not own the antigravity:// registration, so nothing was changed.");
                }
                return new { action = deeplinkAction, registered = DeepLinkRouter.IsRegisteredFor(hub.Paths) };
            case "shortcut":
                var shortcutDirectory = TakeOption(tail, "--directory");
                RequireCount(tail, 1, "shortcut <id> [--directory <path>]");
                var shortcut = await hub.CreateShortcutAsync(await ResolveIdAsync(hub, tail[0]), DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory), shortcutDirectory).ConfigureAwait(false);
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

    private static IsolationBackendKind ParseBackend(string value) => value.Trim().ToLowerInvariant() switch
    {
        // The enum value still exists so old records load, but a removed
        // backend must not be selectable for new profiles or new defaults.
        "sandboxie" or "sandbox" => throw new UsageException(
            "The sandboxie backend was removed in 0.4.0. Use process-local; existing sandboxie-era profiles already resolve to it."),
        "process-local" or "processlocal" or "local" => IsolationBackendKind.ProcessLocal,
        _ => throw new UsageException("Unknown isolation backend. Use process-local.")
    };

    private static async Task<Guid> ResolveIdAsync(HubService hub, string nameOrId)
    {
        var profile = await hub.ResolveProfileAsync(nameOrId).ConfigureAwait(false);
        return profile.Id;
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
        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles. Create one with: agprofile new \"<name>\"");
            return;
        }
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
        Console.WriteLine($"{status.ProfileId:D}  {status.Name}  {status.State}  PID {status.Root?.ProcessId.ToString() ?? "-"}  SCOPE {status.IsolationBoxName ?? "-"}  {status.Detail}");

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
          version | --version                 Print the product version and exit
        Exit codes: 0 ok · 1 operation failed · 2 usage · 3 not found · 4 unsupported · 5 ownership · 130 cancelled

        Everyday commands (a profile may be given as a name, a unique prefix, or a UUID):
          setup                                 First-run environment check (exit 1 when checks fail)
          new <name> [--workspace <path>] [--backend <process-local>] [--direct] [--fresh]
                                                Create a profile and prepare its isolation in one step
          ls                                    List profiles
          open <name-or-id> [--direct]          Prepare what is missing, then launch
          close <name-or-id> [--force]          Stop a profile (exit 1 while a graceful close is pending)
          status [name-or-id] | show <name-or-id> | doctor
          fix <name-or-id>                      Diagnose and repair isolation readiness

        Advanced commands (kept for scripting and recovery):
          launch <name-or-id> | stop <name-or-id> [--force] | restart <name-or-id> [--force] | launch-all | stop-all [--force]
          create <name> [--workspace <path>]    Alias of new (always prepared isolation since 0.4.2)
          rename <name-or-id> <new-name> | remove <name-or-id> | workspace <name-or-id> <path|none>
          iso-prepare <name-or-id> [--box <name>] [--fresh]
                                                Prepare a dedicated scope; --fresh acknowledges a takeover
          iso-launch <name-or-id>               Launch the unmodified desktop inside the profile's scope
          iso-stop <name-or-id>                 Terminate everything inside the profile's scope
          iso-status                            Inspect scope readiness
          open-data [name-or-id] | open-logs | configure-executable <path> | shortcut <name-or-id> [--directory <path>]
          configure-isolation <process-local>   Backend newly created profiles use (existing ones keep theirs)
          configure-deeplink <register|restore>
                                      Route the antigravity:// callback to the profile that is waiting
          callback <uri>              Internal: hand a deep-link callback to the waiting profile
          agent-probe                           Mocked LS/official CLI diagnostic; no real account

        Authentication is performed only by official Antigravity. Concurrent accounts require a prepared per-profile isolation scope; the legacy direct path remains serial-only.
        """;

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State == RuntimeState.Stopped) return;
        throw new HubException(result.Status.Detail ?? "The profile is still running.", "graceful_close_pending");
    }

    private sealed class UsageException(string message) : Exception(message);
}
