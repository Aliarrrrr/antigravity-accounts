namespace AGProfileHub.Core;

public sealed class ProfileManager(ConfigRepository repository, HubPaths paths)
{
    public async Task<IReadOnlyList<ProfileRegistration>> ListAsync(CancellationToken cancellationToken = default) =>
        (await repository.ReadProfilesAsync(cancellationToken)).Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();

    public async Task<ProfileRegistration> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await repository.ReadProfilesAsync(cancellationToken)).Profiles.SingleOrDefault(p => p.Id == id) ?? throw new ProfileNotFoundException(id);

    /// <summary>
    /// Resolves a profile by exact UUID, exact display name, or a unique
    /// prefix of either, so everyday commands do not force the 32-character
    /// UUID on the user. Ambiguity fails closed and lists the candidates.
    /// </summary>
    public async Task<ProfileRegistration> ResolveAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) throw new HubException("A profile name or UUID is required.", "invalid_profile_name");
        var term = nameOrId.Trim();
        var profiles = await ListAsync(cancellationToken).ConfigureAwait(false);

        if (Guid.TryParse(term, out var id) && id != Guid.Empty)
        {
            var exact = profiles.FirstOrDefault(p => p.Id == id);
            if (exact is not null) return exact;
        }

        var byName = profiles.Where(p => string.Equals(p.Name, term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];

        var candidates = profiles.Where(p =>
            p.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
            p.Id.ToString("D").StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
            p.Id.ToString("N").StartsWith(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) throw new HubException($"No profile matches '{term}'.", "profile_not_found");
        var match = byName.Count > 0 ? byName : candidates;
        if (match.Count == 1) return match[0];
        var listed = string.Join(", ", match.Take(6).Select(p => $"{p.Name} ({p.Id.ToString("D")[..8]})"));
        throw new HubException($"'{term}' matches multiple profiles: {listed}. Use more characters or the UUID.", "profile_name_ambiguous");
    }

    public async Task<ProfileRegistration> CreateAsync(string name, string? workspacePath = null,
        IsolationBackendKind backend = IsolationBackendKind.Sandboxie, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        workspacePath = ValidateWorkspace(workspacePath);
        if (!Enum.IsDefined(backend))
            throw new ConfigurationException("Unknown isolation backend.", "invalid_isolation_backend");
        var now = DateTimeOffset.UtcNow;
        var profile = new ProfileRegistration(Guid.NewGuid(), name, workspacePath, now, now) { Backend = backend };
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
