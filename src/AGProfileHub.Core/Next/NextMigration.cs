namespace AGProfileHub.Core;

public sealed record MigrationItem(Guid Id, string Name, string? Workspace, bool AlreadyPresent, ConfigPreview? Configuration, string? Warning);
public static class NextMigration
{
    public static async Task<IReadOnlyList<MigrationItem>> RunAsync(NextHubService destination, string legacyRoot, bool dryRun, CancellationToken token = default)
    {
        legacyRoot = Path.GetFullPath(legacyRoot);
        PathSafety.RejectExistingPathAncestors(legacyRoot);
        if (NextHubService.EqualPath(legacyRoot, destination.Paths.DataRoot)) throw new ConfigurationException("Source and destination must differ.");
        var sourceFile = Path.Combine(legacyRoot, "config", "profiles.json");
        if (!File.Exists(sourceFile)) throw new ConfigurationException("Source profile registry not found.");
        var receiptPath = Path.Combine(destination.Paths.DataRoot, "migration-preview.json");
        var store = new AtomicJsonStore(destination.Paths.DataRoot);
        var registryHash = await ExecutableAttestation.ComputeSha256Async(sourceFile, token);
        if (!dryRun)
        {
            var receipt = await store.ReadAsync(receiptPath, NextJsonContext.Default.MigrationPreviewReceipt, () => null!, token);
            if (receipt is null || !NextHubService.EqualPath(receipt.SourceRoot, legacyRoot) || receipt.RegistrySha256 != registryHash)
                throw new HubException("Run migrate --dry-run for this unchanged source registry before --apply.", "migration_preview_required");
        }
        // Read only the registry; never bootstrap or mutate the source or inspect credential/runtime files.
        var source = await new AtomicJsonStore(legacyRoot).ReadAsync(sourceFile, HubJsonContext.Default.ProfilesDocument,
            () => throw new ConfigurationException("Source registry missing."), token);
        if (source.Profiles.Any(p => p.Id == Guid.Empty) || source.Profiles.Select(p => p.Id).Distinct().Count() != source.Profiles.Count)
            throw new ConfigurationException("Source registry has invalid IDs.");
        var result = new List<MigrationItem>();
        foreach (var item in source.Profiles)
        {
            var existing = (await destination.Profiles.ListAsync(token)).FirstOrDefault(p => p.Id == item.Id);
            if (existing is not null && (existing.Name != item.Name || existing.WorkspacePath != item.WorkspacePath))
            { result.Add(new MigrationItem(item.Id, item.Name, item.WorkspacePath, true, null, "Destination metadata differs; preserved.")); continue; }
            if ((await destination.Profiles.ListAsync(token)).Any(p => p.Id != item.Id && string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
            { result.Add(new MigrationItem(item.Id, item.Name, item.WorkspacePath, false, null, "Name conflict; skipped.")); continue; }
            if (!dryRun && existing is null)
            {
                destination.Paths.EnsureProfileLayout(item.Id);
                await destination.Repository.UpdateProfilesAsync(document =>
                {
                    if (document.Profiles.Any(p => p.Id == item.Id)) return (document, false);
                    return (document with { Profiles = [.. document.Profiles, item with { Backend = IsolationBackendKind.ProcessLocal }] }, true);
                }, token);
            }
            ConfigPreview? preview = null; string? warning = null;
            try
            {
                preview = await NextConfiguration.InheritAsync(destination.Paths, item.Id,
                    Path.Combine(legacyRoot, "profiles", item.Id.ToString("D"), "user-data", "User"), !dryRun, token);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or HubException)
            { warning = "Configuration preserved/skipped: " + ex.GetType().Name; }
            result.Add(new MigrationItem(item.Id, item.Name, item.WorkspacePath, existing is not null, preview, warning));
        }
        if (dryRun) await store.WriteAsync(receiptPath, new MigrationPreviewReceipt { SourceRoot = legacyRoot,
            RegistrySha256 = registryHash, PreviewedUtc = DateTimeOffset.UtcNow }, NextJsonContext.Default.MigrationPreviewReceipt, token);
        return result;
    }
}
