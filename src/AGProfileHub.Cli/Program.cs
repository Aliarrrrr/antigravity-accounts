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

            // Runs before any Hub bootstrap: this verb is executed inside
            // isolation scopes (for example a sandbox) where the Hub data root
            // must not be created or touched.
            if (parsed.Command[0].Equals("credential-shim", StringComparison.OrdinalIgnoreCase))
            {
                var tail = parsed.Command.Skip(1).ToList();
                var forceFresh = TakeFlag(tail, "--force") & TakeFlag(tail, "--fresh");
                RequireNoArguments(tail, "credential-shim --force --fresh");
                if (!forceFresh) throw new UsageException("credential-shim is an internal command and requires both --force and --fresh.");
                var shim = AntigravityKeyring.EnsureIsolation(forceFresh);
                if (parsed.Json) WriteJson(shim);
                else Console.WriteLine($"credential-shim: target={shim.Target} existedBefore={shim.ExistedBefore} created={shim.Created} blobSize={shim.BlobSize}");
                // Exit codes let a caller that cannot capture sandboxed stdout
                // still tell "claimed now" from "already owned".
                return shim.Created ? 11 : 0;
            }

            // Runs before any Hub bootstrap: this verb is the elevated helper
            // launched by WindowsElevationBridge. It touches only the two paths
            // it is given and never creates a Hub data root.
            if (parsed.Command[0].Equals("admin-helper", StringComparison.OrdinalIgnoreCase))
            {
                var tail = parsed.Command.Skip(1).ToList();
                if (tail.Count != 2)
                    return WriteError("Usage: admin-helper <request-path> <result-path>", "usage_error", 2, parsed.Json);
                return AdminOperations.RunHelper(tail[0], tail[1]);
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
            case "iso-prepare":
                var isoBox = TakeOption(tail, "--box");
                var isoFresh = TakeFlag(tail, "--fresh");
                RequireCount(tail, 1, "iso-prepare <id> [--box <existing-box-name>] [--fresh]");
                var isoPrepare = await hub.PrepareIsolationAsync(await ResolveIdAsync(hub, tail[0]), isoBox, isoFresh).ConfigureAwait(false);
                if (!json)
                    Console.WriteLine($"isolation: box={isoPrepare.BoxName} boxCreated={isoPrepare.BoxCreated} mappingCreated={isoPrepare.MappingCreated} keyringClaimed={isoPrepare.KeyringClaimed} shimExit={isoPrepare.ShimExitCode}");
                return isoPrepare;
            case "iso-launch":
                RequireCount(tail, 1, "iso-launch <id>");
                var isoLaunchId = await ResolveIdAsync(hub, tail[0]);
                if (await hub.GetIsolationStatusAsync(isoLaunchId).ConfigureAwait(false) is null)
                    throw new HubException("This profile has no isolation box yet. Run iso-prepare first.", "isolation_not_prepared");
                var isoLaunch = await hub.LaunchAsync(isoLaunchId).ConfigureAwait(false);
                if (!json) PrintStatus(isoLaunch.Status);
                return isoLaunch;
            case "iso-stop":
                RequireCount(tail, 1, "iso-stop <id>");
                var isoStopId = await ResolveIdAsync(hub, tail[0]);
                if (await hub.GetIsolationStatusAsync(isoStopId).ConfigureAwait(false) is null)
                    throw new HubException("This profile has no isolation box yet. Run iso-prepare first.", "isolation_not_prepared");
                var isoStopped = await hub.StopAsync(isoStopId, force: true).ConfigureAwait(false);
                if (!json) PrintStatus(isoStopped.Status);
                return isoStopped;
            case "iso-writethrough":
                RequireCount(tail, 1, "iso-writethrough <id>");
                var wtId = await ResolveIdAsync(hub, tail[0]);
                var wt = await hub.GetWriteThroughAsync(wtId).ConfigureAwait(false);
                if (!json)
                {
                    Console.WriteLine($"Box: {wt.BoxName}");
                    Console.WriteLine("These directories need direct-write rules, otherwise edits stay inside the sandbox:");
                    foreach (var item in wt.Paths) Console.WriteLine($"  {(wt.MissingPaths.Contains(item, StringComparer.OrdinalIgnoreCase) ? "MISSING" : "OK     ")}  {item}");
                    if (wt.MissingPaths.Count > 0)
                        Console.WriteLine($"Missing rules can be applied with elevation by the Hub itself: fix {tail[0]}");
                }
                return new { boxName = wt.BoxName, paths = wt.Paths, missingPaths = wt.MissingPaths, ready = wt.MissingPaths.Count == 0 };
            case "iso-status":
                RequireNoArguments(tail, "iso-status");
                var isoStatus = await hub.GetIsolationStatusesAsync().ConfigureAwait(false);
                if (!json)
                    foreach (var item in isoStatus)
                        Console.WriteLine($"{item.BoxName}  profile={item.ProfileId}  boxExists={item.BoxExists}  keyringClaimed={item.KeyringClaimed}  writeThrough={item.WriteThroughReady}  processes={item.ProcessCount}");
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
                var newWorkspace = TakeOption(tail, "--workspace");
                var newFresh = TakeFlag(tail, "--fresh");
                var newDirect = TakeFlag(tail, "--direct");
                RequireCount(tail, 1, "new <name> [--workspace <path>] [--direct] [--fresh]");
                var newProfile = await hub.CreateProfileAsync(tail[0], newWorkspace).ConfigureAwait(false);
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
                if (!openDirect && await hub.DetectIsolationStageAsync(openId).ConfigureAwait(false) != IsolationStage.BackendUnavailable)
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
            case "create":
                var createWorkspace = TakeOption(tail, "--workspace");
                RequireCount(tail, 1, "create <name> [--workspace <path>]");
                var created = await hub.CreateProfileAsync(tail[0], createWorkspace).ConfigureAwait(false);
                if (!json) Console.WriteLine($"Created {created.Name} ({created.Id:D})");
                return created;
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

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new UsageException($"Invalid profile ID '{value}'. Expected a UUID.");

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
        Console.WriteLine($"{status.ProfileId:D}  {status.Name}  {status.State}  PID {status.Root?.ProcessId.ToString() ?? "-"}  BOX {status.IsolationBoxName ?? "-"}  {status.Detail}");

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

    private static string HelpText() => """
        Antigravity Profile Hub CLI (experimental)
        Global options: --data-root <absolute-path>  --json

        Everyday commands (a profile may be given as a name, a unique prefix, or a UUID):
          setup                                 First-run environment check
          new <name> [--workspace <path>] [--direct] [--fresh]
                                                Create a profile and prepare its isolation in one step
          ls                                    List profiles
          open <name-or-id> [--direct]          Prepare what is missing, then launch
          close <name-or-id> [--force]          Stop a profile
          status [name-or-id] | show <name-or-id> | doctor
          fix <name-or-id>                      Diagnose and repair isolation readiness

        Advanced commands (kept for scripting and recovery):
          launch <name-or-id> | stop <name-or-id> [--force] | restart <name-or-id> [--force] | launch-all | stop-all [--force]
          create <name> [--workspace <path>] | rename <name-or-id> <new-name> | remove <name-or-id> | workspace <name-or-id> <path|none>
          iso-prepare <name-or-id> [--box <name>] [--fresh]
                                                Prepare a dedicated box; --fresh acknowledges safe overwrite
          iso-launch <name-or-id>               Launch the unmodified desktop inside the profile's box
          iso-stop <name-or-id>                 Terminate everything inside the profile's box
          iso-status | iso-writethrough <name-or-id>
                                                Inspect box and direct-write readiness
          open-data [name-or-id] | open-logs | configure-executable <path> | shortcut <name-or-id> [--directory <path>]
          agent-probe                           Mocked LS/official CLI diagnostic; no real account
          credential-status                     Report the shared token entry (existence/size only)
          credential-shim --force --fresh       Internal: claim the keyring entry inside a fresh box

        Authentication is performed only by official Antigravity. Concurrent accounts require a prepared per-profile isolation box; the legacy direct path remains serial-only.
        """;

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State == RuntimeState.Stopped) return;
        throw new HubException(result.Status.Detail ?? "The profile is still running.", "graceful_close_pending");
    }

    private sealed class UsageException(string message) : Exception(message);
}
