namespace AGProfileHub.Core;

public sealed class ProfileManager(ConfigRepository repository, HubPaths paths)
{
    public async Task<IReadOnlyList<ProfileRegistration>> ListAsync(CancellationToken cancellationToken = default) =>
        (await repository.ReadProfilesAsync(cancellationToken)).Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();

    public async Task<ProfileRegistration> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await repository.ReadProfilesAsync(cancellationToken)).Profiles.SingleOrDefault(p => p.Id == id) ?? throw new ProfileNotFoundException(id);

    public async Task<ProfileRegistration> CreateAsync(string name, string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        workspacePath = ValidateWorkspace(workspacePath);
        var now = DateTimeOffset.UtcNow;
        var profile = new ProfileRegistration(Guid.NewGuid(), name, workspacePath, now, now);
        paths.EnsureProfileLayout(profile.Id);
        return await repository.UpdateProfilesAsync(document =>
        {
            var profiles = document.Profiles.ToList();
            profiles.Add(profile);
            return (document with { Profiles = profiles }, profile);
        }, cancellationToken);
    }

    public Task<ProfileRegistration> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        return repository.UpdateProfilesAsync(document =>
        {
            var index = document.Profiles.FindIndex(p => p.Id == id);
            if (index < 0) throw new ProfileNotFoundException(id);
            var updated = document.Profiles[index] with { Name = name, UpdatedUtc = DateTimeOffset.UtcNow };
            var profiles = document.Profiles.ToList();
            profiles[index] = updated;
            return (document with { Profiles = profiles }, updated);
        }, cancellationToken);
    }

    public Task<ProfileRegistration> SetWorkspaceAsync(Guid id, string? workspacePath, CancellationToken cancellationToken = default)
    {
        workspacePath = ValidateWorkspace(workspacePath);
        return repository.UpdateProfilesAsync(document =>
        {
            var index = document.Profiles.FindIndex(p => p.Id == id);
            if (index < 0) throw new ProfileNotFoundException(id);
            var updated = document.Profiles[index] with { WorkspacePath = workspacePath, UpdatedUtc = DateTimeOffset.UtcNow };
            var profiles = document.Profiles.ToList();
            profiles[index] = updated;
            return (document with { Profiles = profiles }, updated);
        }, cancellationToken);
    }

    public Task<ProfileRegistration> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.UpdateProfilesAsync(document =>
        {
            var profile = document.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new ProfileNotFoundException(id);
            var profiles = document.Profiles.Where(p => p.Id != id).ToList();
            return (document with { Profiles = profiles }, profile);
        }, cancellationToken);

    private static string ValidateName(string name)
    {
        var result = name?.Trim() ?? string.Empty;
        if (result.Length is < 1 or > 80 || result.Any(char.IsControl)) throw new HubException("Profile name must contain 1-80 printable characters.", "invalid_profile_name");
        return result;
    }

    private static string? ValidateWorkspace(string? workspacePath)
    {
        var result = PathSafety.NormalizeWorkspace(workspacePath);
        if (result is not null && !Directory.Exists(result)) throw new HubException($"Workspace directory does not exist: {result}", "workspace_not_found");
        return result;
    }
}
