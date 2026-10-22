using AGProfileHub.Core;

namespace AGProfileHub.Cli;

internal static class SharedCli
{
    public const string Help = "group list | create <name> | show/status <group> | add-account/remove-account <group> <profile> | catalog <profile> | preview-import <group> <profile> [--project <id>] [--conversation <id>] [--knowledge] | apply-import <group> <preview-id> | restore-import <group> <import-id> | capabilities <group> | switch <group> <profile> | recover <group>. Import selections are explicit; Ctrl+C cancels a pending switch. Client switching stays disabled until a real adapter is verified.";

    public static async Task<object> ExecuteAsync(NextHubService hub, IReadOnlyList<string> args, CancellationToken token)
    {
        var groups = new SharedWorkspaceService(hub.Paths, hub.Repository);
        var switches = new AccountSwitchCoordinator(hub.Paths);
        if (args.Count == 0 || args[0] is "help" or "--help") return new { help = Help };
        void Count(int count) { if (args.Count != count) throw new HubException("Invalid group arguments. Run group help.", "usage_error"); }
        async Task<SharedWorkspaceGroup> Group() => await groups.ResolveAsync(args[1], token);
        async Task<ProfileRegistration> Profile(int index) => await hub.Profiles.ResolveAsync(args[index], token);
        switch (args[0])
        {
            case "list": Count(1); return await groups.ListAsync(token);
            case "create": Count(2); return await groups.CreateAsync(args[1], token);
            case "show": Count(2); return await Group();
            case "status":
                Count(2); var statusGroup = await Group();
                return new { group = statusGroup, runtime = await switches.ReadStateAsync(statusGroup.Id, token), capability = await switches.GetCapabilitiesAsync(statusGroup.Id, token) };
            case "add-account": Count(3); return await groups.AddAccountAsync((await Group()).Id, (await Profile(2)).Id, token);
            case "remove-account": Count(3); return await groups.RemoveAccountAsync((await Group()).Id, (await Profile(2)).Id, token);
            case "catalog": Count(2); return await groups.PreviewCatalogAsync((await Profile(1)).Id, token);
            case "preview-import":
                if (args.Count < 3) throw new HubException("Group and source profile required.", "usage_error");
                var projects = new List<string>(); var conversations = new List<string>(); var knowledge = false;
                for (var i = 3; i < args.Count; i++)
                {
                    if (args[i] == "--knowledge" && !knowledge) { knowledge = true; continue; }
                    if (args[i] is not "--project" and not "--conversation" || i + 1 >= args.Count)
                        throw new HubException("Use --project <id>, --conversation <id> or --knowledge (knowledge and memory).", "usage_error");
                    var target = args[i] == "--project" ? projects : conversations;
                    target.Add(args[++i]);
                }
                return await groups.PreviewImportAsync((await Group()).Id, (await Profile(2)).Id, projects, conversations, knowledge, token);
            case "apply-import": Count(3); return await groups.ApplyImportAsync((await Group()).Id, Id(args[2]), token);
            case "restore-import": Count(3); return await groups.RestoreImportAsync((await Group()).Id, Id(args[2]), token);
            case "capabilities": Count(2); return await switches.GetCapabilitiesAsync((await Group()).Id, token);
            case "switch":
                Count(3); var group = await Group(); var profile = await Profile(2);
                if (!group.AccountProfileIds.Contains(profile.Id)) throw new HubException("Target account is not a member of this group.", "shared_account_not_member");
                return await switches.ExecuteAsync(group.Id, profile.Id, token);
            case "recover": Count(2); return await switches.RecoverAsync((await Group()).Id, token);
            default: throw new HubException("Unknown group command. Run group help.", "usage_error");
        }
    }

    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id :
        throw new HubException("A valid preview or import ID is required.", "usage_error");
}
