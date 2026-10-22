using System.Text.Json;

namespace AGProfileHub.Core;

/// <summary>Local shared content. Account membership stores profile IDs, never authentication material.</summary>
public sealed partial class SharedWorkspaceService
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly HubPaths _paths;
    private readonly ConfigRepository _repository;
    private readonly AtomicJsonStore _json;
    private string GroupsRoot => Path.Combine(_paths.DataRoot, "shared-groups");

    public SharedWorkspaceService(HubPaths paths, ConfigRepository repository)
    {
        _paths = paths; _repository = repository; _json = new AtomicJsonStore(paths.DataRoot);
    }

    public string GetGroupRoot(Guid groupId)
    {
        PathSafety.RejectExistingPathAncestors(_paths.DataRoot);
        RequireId(groupId);
        var root = Path.Combine(GroupsRoot, groupId.ToString("D"));
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, root);
        return root;
    }

    public string GetContentRoot(Guid groupId)
    {
        var path = Path.Combine(GetGroupRoot(groupId), "content");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        return path;
    }

    private string GroupFile(Guid id) => Path.Combine(GetGroupRoot(id), "group.json");
    private Task<FileLock> LockAsync(Guid id, CancellationToken token) =>
        FileLock.AcquireAsync(Path.Combine(GetGroupRoot(id), "group.lock"), LockTimeout, token);

    public async Task<IReadOnlyList<SharedWorkspaceGroup>> ListAsync(CancellationToken cancellationToken = default)
    {
        PathSafety.RejectExistingPathAncestors(_paths.DataRoot);
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, GroupsRoot);
        if (!Directory.Exists(GroupsRoot)) return [];
        var groups = new List<SharedWorkspaceGroup>();
        foreach (var directory in Directory.EnumerateDirectories(GroupsRoot).Order(StringComparer.Ordinal))
        {
            PathSafety.RejectExistingReparsePoints(_paths.DataRoot, directory);
            if (Guid.TryParse(Path.GetFileName(directory), out var id) && File.Exists(GroupFile(id)))
                groups.Add(await ReadAsync(id, cancellationToken));
        }
        return groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SharedWorkspaceGroup> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var held = await FileLock.AcquireAsync(Path.Combine(_paths.LocksRoot, "shared-groups.lock"), LockTimeout, cancellationToken);
        if ((await ListAsync(cancellationToken)).Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new HubException("A shared group with that name already exists.", "shared_group_name_conflict");
        var now = DateTimeOffset.UtcNow;
        var group = new SharedWorkspaceGroup { Id = Guid.NewGuid(), Name = name, CreatedUtc = now, UpdatedUtc = now };
        await using var groupLock = await LockAsync(group.Id, cancellationToken);
        await SaveGroupAsync(group, cancellationToken);
        return group;
    }

    public async Task<SharedWorkspaceGroup> ReadAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        // Missing groups must not create a directory or a lock file.
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        // group.json is atomically replaced. Status and account selection can read the last
        // committed metadata while a switch/import holds the group's mutation lock.
        return await ReadUnlockedAsync(groupId, cancellationToken);
    }

    public async Task<SharedWorkspaceGroup> ResolveAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) throw MissingGroup();
        var groups = await ListAsync(cancellationToken);
        var exact = groups.Where(g => string.Equals(g.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.Id.ToString("D"), idOrName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var matches = groups.Where(g => g.Name.StartsWith(idOrName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            g.Id.ToString("D").StartsWith(idOrName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch { 1 => matches[0], 0 => throw MissingGroup(), _ => throw new HubException("Shared group reference is ambiguous.", "shared_group_ambiguous") };
    }

    public async Task<SharedWorkspaceGroup> AddAccountAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await RequireProfileAsync(profileId, cancellationToken);
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        await using var held = await LockAsync(groupId, cancellationToken);
        var group = await ReadUnlockedAsync(groupId, cancellationToken);
        await EnsureInactiveAsync(groupId, cancellationToken);
        if (group.AccountProfileIds.Contains(profileId)) return group;
        group = group with { AccountProfileIds = [.. group.AccountProfileIds, profileId], UpdatedUtc = DateTimeOffset.UtcNow };
        await SaveGroupAsync(group, cancellationToken);
        return group;
    }

    public async Task<SharedWorkspaceGroup> RemoveAccountAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        await using var held = await LockAsync(groupId, cancellationToken);
        var group = await ReadUnlockedAsync(groupId, cancellationToken);
        await EnsureInactiveAsync(groupId, cancellationToken);
        if (!group.AccountProfileIds.Contains(profileId)) return group;
        group = group with { AccountProfileIds = group.AccountProfileIds.Where(id => id != profileId).ToList(), UpdatedUtc = DateTimeOffset.UtcNow };
        await SaveGroupAsync(group, cancellationToken);
        return group;
    }

    private async Task<SharedWorkspaceGroup> ReadUnlockedAsync(Guid id, CancellationToken token)
    {
        var group = await _json.ReadAsync(GroupFile(id), SharedWorkspaceJsonContext.Default.SharedWorkspaceGroup, () => throw MissingGroup(), token);
        if (group.SchemaVersion != 1 || group.Id != id || group.AccountProfileIds is null ||
            group.AccountProfileIds.Any(x => x == Guid.Empty) || group.AccountProfileIds.Distinct().Count() != group.AccountProfileIds.Count ||
            group.CreatedUtc == default || group.UpdatedUtc == default || ValidateName(group.Name) != group.Name)
            throw new ConfigurationException("Shared group metadata is invalid.", "shared_group_invalid");
        return group;
    }

    private Task SaveGroupAsync(SharedWorkspaceGroup group, CancellationToken token) =>
        _json.WriteAsync(GroupFile(group.Id), group, SharedWorkspaceJsonContext.Default.SharedWorkspaceGroup, token);

    private async Task RequireProfileAsync(Guid profileId, CancellationToken token)
    {
        RequireId(profileId);
        if (!(await _repository.ReadProfilesAsync(token)).Profiles.Any(p => p.Id == profileId))
            throw new HubException("Source account profile is not registered.", "profile_not_found");
    }

    private async Task EnsureInactiveAsync(Guid id, CancellationToken token)
    {
        var path = Path.Combine(GetGroupRoot(id), "runtime.json");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            var fields = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (fields.TryGetValue("SchemaVersion", out var schema) && schema.GetInt32() == 1 &&
                fields.TryGetValue("GroupId", out var group) && group.GetGuid() == id &&
                fields.TryGetValue("IsAttached", out var attached) && attached.ValueKind == JsonValueKind.False &&
                fields.TryGetValue("SendingBlocked", out var blocked) && blocked.ValueKind == JsonValueKind.False &&
                fields.TryGetValue("State", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "Ready") return;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException) { }
        throw new HubException("The shared group has an attached or unresolved runtime; detach it before importing or changing account membership.", "shared_group_active");
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl))
            throw new HubException("Shared group names must contain 1-80 printable characters.", "shared_group_name_invalid");
        return name.Trim();
    }
    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new HubException("An ID cannot be empty.", "shared_id_invalid"); }
    private static HubException MissingGroup() => new("Shared group was not found.", "shared_group_not_found");
}
