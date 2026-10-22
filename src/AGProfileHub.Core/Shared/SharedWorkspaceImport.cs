using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AGProfileHub.Core;

public sealed partial class SharedWorkspaceService
{
    private string GeminiRoot(Guid groupId) => Path.Combine(GetContentRoot(groupId), ".gemini");
    private string SourceRoot(Guid profileId)
    {
        PathSafety.RejectExistingPathAncestors(_paths.DataRoot);
        var root = Path.Combine(_paths.Home(profileId), ".gemini");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, root);
        return root;
    }
    private string PreviewFile(Guid groupId, Guid previewId) => Path.Combine(GetGroupRoot(groupId), "previews", ValidGuid(previewId) + ".json");
    private string ImportFile(Guid groupId, Guid importId) => Path.Combine(GetGroupRoot(groupId), "imports", ValidGuid(importId), "import.json");
    private static string ValidGuid(Guid id) { RequireId(id); return id.ToString("D"); }

    public async Task<SharedImportCatalog> PreviewCatalogAsync(Guid sourceProfileId, CancellationToken cancellationToken = default)
    {
        await RequireProfileAsync(sourceProfileId, cancellationToken);
        var root = SourceRoot(sourceProfileId);
        var projects = new List<string>(); var conversations = new List<string>();
        var projectRoot = Safe(root, "config/projects");
        if (Directory.Exists(projectRoot))
            foreach (var entry in Directory.EnumerateFileSystemEntries(projectRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathSafety.RejectExistingReparsePoints(root, entry);
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry)) projects.Add(ValidateSelectionId(name));
                else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) projects.Add(ValidateSelectionId(name[..^5]));
            }
        var conversationRoot = Safe(root, "antigravity/conversations");
        if (Directory.Exists(conversationRoot))
            foreach (var entry in Directory.EnumerateFiles(conversationRoot, "*.db"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathSafety.RejectExistingReparsePoints(root, entry);
                conversations.Add(ValidateSelectionId(Path.GetFileNameWithoutExtension(entry)));
            }
        return new(sourceProfileId, projects.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            conversations.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            Directory.Exists(Safe(root, "antigravity/knowledge")), File.Exists(Safe(root, "config/memory.txtpb")));
    }

    public async Task<SharedImportPreview> PreviewImportAsync(Guid groupId, Guid profileId, IReadOnlyList<string> projectIds,
        IReadOnlyList<string> conversationIds, bool includeKnowledge, CancellationToken cancellationToken = default)
    {
        await RequireProfileAsync(profileId, cancellationToken);
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        await using var held = await LockAsync(groupId, cancellationToken);
        var group = await ReadUnlockedAsync(groupId, cancellationToken);
        RequireMember(group, profileId);
        await EnsureInactiveAsync(groupId, cancellationToken);
        var preview = await BuildPreviewAsync(groupId, profileId, projectIds, conversationIds, includeKnowledge, cancellationToken);
        await _json.WriteAsync(PreviewFile(groupId, preview.Id), preview, SharedWorkspaceJsonContext.Default.SharedImportPreview, cancellationToken);
        return preview;
    }

    public async Task<SharedImportResult> ApplyImportAsync(Guid groupId, Guid previewId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        await using var held = await LockAsync(groupId, cancellationToken);
        var group = await ReadUnlockedAsync(groupId, cancellationToken);
        await EnsureInactiveAsync(groupId, cancellationToken);
        var preview = await _json.ReadAsync(PreviewFile(groupId, previewId), SharedWorkspaceJsonContext.Default.SharedImportPreview,
            () => throw new HubException("Create an import preview first.", "shared_import_preview_required"), cancellationToken);
        if (preview.SchemaVersion != 1 || preview.GroupId != groupId || preview.Id != previewId)
            throw new ConfigurationException("Import preview identity is invalid.", "shared_import_preview_invalid");
        await RequireProfileAsync(preview.SourceProfileId, cancellationToken);
        RequireMember(group, preview.SourceProfileId);
        var current = await BuildPreviewAsync(groupId, preview.SourceProfileId, preview.ProjectIds, preview.ConversationIds, preview.IncludeKnowledge, cancellationToken);
        EnsureSameItems(preview.Items, current.Items);
        var importId = Guid.NewGuid();
        var stage = Path.Combine(GetGroupRoot(groupId), "imports", importId.ToString("D"), "stage");
        PathSafety.EnsureSafeDirectory(_paths.DataRoot, stage);
        var result = new SharedImportResult { ImportId = importId, GroupId = groupId, UpdatedUtc = DateTimeOffset.UtcNow,
            SkippedPaths = preview.Items.Where(i => i.Disposition != SharedImportDisposition.Copy).SelectMany(i => i.RootPaths).ToList() };
        await SaveImportAsync(result, cancellationToken);
        try
        {
            var source = SourceRoot(preview.SourceProfileId);
            foreach (var item in preview.Items.Where(i => i.Disposition == SharedImportDisposition.Copy))
            {
                foreach (var relative in item.RootPaths)
                    await CopyTreeAsync(source, stage, relative, cancellationToken);
                if ((await DescribeAsync(stage, item.RootPaths, cancellationToken)).Digest != item.SourceDigest)
                    throw StalePreview();
            }
            // A source or destination changed while staging: leave all destination records untouched.
            var final = await BuildPreviewAsync(groupId, preview.SourceProfileId, preview.ProjectIds, preview.ConversationIds, preview.IncludeKnowledge, cancellationToken);
            EnsureSameItems(preview.Items, final.Items);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in preview.Items.Where(i => i.Disposition == SharedImportDisposition.Copy))
            {
                var published = new List<string>();
                try
                {
                    foreach (var relative in item.RootPaths)
                    {
                        var staged = Safe(stage, relative);
                        if (!File.Exists(staged) && !Directory.Exists(staged)) continue;
                        var target = Safe(GeminiRoot(groupId), relative);
                        PathSafety.EnsureSafeDirectory(_paths.DataRoot, Path.GetDirectoryName(target)!);
                        if (Directory.Exists(staged)) Directory.Move(staged, target); else File.Move(staged, target, overwrite: false);
                        published.Add(relative);
                        // Once a root has moved, persist ownership before publishing the next root.
                        result = result with { ImportedPaths = [.. result.ImportedPaths, relative], UpdatedUtc = DateTimeOffset.UtcNow };
                        await SaveImportAsync(result, CancellationToken.None);
                    }
                    result = result with { ImportedItems = [.. result.ImportedItems, item], UpdatedUtc = DateTimeOffset.UtcNow };
                    await SaveImportAsync(result, CancellationToken.None);
                }
                catch
                {
                    // Partial multi-root publication is retained in the journal for safe restore.
                    if (published.Count > 0 && !result.ImportedItems.Any(i => i.Kind == item.Kind && i.Id == item.Id))
                    {
                        var description = await DescribeAsync(GeminiRoot(groupId), published, CancellationToken.None);
                        result = result with { ImportedItems = [.. result.ImportedItems, item with { RootPaths = published, SourceDigest = description.Digest,
                            RootDigests = item.RootDigests.Where(p => published.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) }] };
                    }
                    throw;
                }
            }
            result = result with { State = "Applied", UpdatedUtc = DateTimeOffset.UtcNow };
            await SaveImportAsync(result, CancellationToken.None);
            DeleteTree(stage);
            return result;
        }
        catch (Exception failure)
        {
            try { await SaveImportAsync(result with { State = "Interrupted", UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None); }
            catch (Exception saveFailure) { failure.Data["SharedImportJournalFailure"] = saveFailure; }
            failure.Data["SharedImportId"] = importId;
            throw;
        }
    }

    public async Task<SharedImportResult> RestoreImportAsync(Guid groupId, Guid importId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(GroupFile(groupId))) throw MissingGroup();
        await using var held = await LockAsync(groupId, cancellationToken);
        await ReadUnlockedAsync(groupId, cancellationToken);
        await EnsureInactiveAsync(groupId, cancellationToken);
        var result = await _json.ReadAsync(ImportFile(groupId, importId), SharedWorkspaceJsonContext.Default.SharedImportResult,
            () => throw new HubException("Import record was not found.", "shared_import_not_found"), cancellationToken);
        if (result.SchemaVersion != 1 || result.ImportId != importId || result.GroupId != groupId ||
            result.ImportedItems is null || result.ImportedPaths is null || result.State is not ("Applying" or "Applied" or "Interrupted" or "Restoring" or "Restored"))
            throw new ConfigurationException("Import record is invalid.", "shared_import_invalid");
        if (result.State == "Restored") return result;
        var owned = result.ImportedItems.SelectMany(i => i.RootPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (result.ImportedPaths.Any(p => !owned.Contains(p)))
            throw new HubException("Import publication was interrupted before ownership was recorded; preserve the content and inspect the import directory.", "shared_import_recovery_required");
        foreach (var item in result.ImportedItems)
        {
            foreach (var path in item.RootPaths)
            {
                ValidateContentPath(path);
                var actual = await DescribeAsync(GeminiRoot(groupId), [path], cancellationToken);
                if (!item.RootDigests.TryGetValue(path, out var digest))
                    throw new ConfigurationException("Import ownership metadata is incomplete.", "shared_import_invalid");
                if (actual.Digest != digest && (actual.Any || result.State != "Restoring"))
                    throw new HubException("Imported content has changed; restore cannot remove it. Preserve those edits before restoring.", "shared_import_content_changed");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        result = result with { State = "Restoring", UpdatedUtc = DateTimeOffset.UtcNow };
        await SaveImportAsync(result, CancellationToken.None);
        foreach (var relative in result.ImportedPaths.OrderByDescending(p => p.Length))
            DeleteTree(Safe(GeminiRoot(groupId), relative));
        var stage = Path.Combine(GetGroupRoot(groupId), "imports", importId.ToString("D"), "stage");
        DeleteTree(stage);
        result = result with { State = "Restored", UpdatedUtc = DateTimeOffset.UtcNow };
        await SaveImportAsync(result, CancellationToken.None);
        return result;
    }

    private Task SaveImportAsync(SharedImportResult result, CancellationToken token) =>
        _json.WriteAsync(ImportFile(result.GroupId, result.ImportId), result, SharedWorkspaceJsonContext.Default.SharedImportResult, token);

    private async Task<SharedImportPreview> BuildPreviewAsync(Guid groupId, Guid profileId, IReadOnlyList<string> projectIds,
        IReadOnlyList<string> conversationIds, bool includeKnowledge, CancellationToken token)
    {
        var source = SourceRoot(profileId); var target = GeminiRoot(groupId);
        var projects = projectIds.Select(ValidateSelectionId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var conversations = conversationIds.Select(ValidateSelectionId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var items = new List<SharedImportItem>();
        foreach (var id in conversations)
        {
            var database = $"antigravity/conversations/{id}.db";
            if (!File.Exists(Safe(source, database))) throw new HubException("Selected conversation is missing.", "shared_import_source_invalid");
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(Safe(source, database + suffix)) && new FileInfo(Safe(source, database + suffix)).Length > 0)
                    throw new HubException("A selected conversation has a live database journal; close the source client before previewing it.", "shared_import_source_busy");
            var item = await DescribeItemAsync("Conversation", id, ConversationRoots(id), source, target, token);
            var issue = SharedConversationDatabase.ValidateConversation(Safe(source, database), id);
            if (issue is null && item.Disposition == SharedImportDisposition.Duplicate)
                issue = SharedConversationDatabase.ValidateConversation(Safe(target, database), id);
            items.Add(issue is null ? item : item with
            {
                Disposition = item.Disposition == SharedImportDisposition.Conflict ? SharedImportDisposition.Conflict : SharedImportDisposition.Blocked,
                Issues = [issue]
            });
        }
        // Publish referenced conversations before their project records. A partial publication must not
        // create a project whose selected conversations have not yet been published.
        foreach (var id in projects)
        {
            var directory = $"config/projects/{id}"; var file = directory + ".json";
            var hasDirectory = Directory.Exists(Safe(source, directory)); var hasFile = File.Exists(Safe(source, file));
            if (hasDirectory == hasFile) throw new HubException("Selected project is missing or has conflicting source representations.", "shared_import_source_invalid");
            // Only the inspected flat JSON layout is supported. Do not enumerate an unknown project
            // directory: it may contain credentials or unrelated conversations, even beside project.json.
            if (hasDirectory)
            {
                items.Add(new SharedImportItem { Kind = "Project", Id = id, RootPaths = [directory, file],
                    Disposition = SharedImportDisposition.Blocked, Issues = ["project_directory_format_unconfirmed"] });
                continue;
            }
            // An unconfirmed destination representation still occupies this ID. Neither read its
            // contents nor overwrite it with the supported flat representation.
            if (Directory.Exists(Safe(target, directory)))
            {
                items.Add(new SharedImportItem { Kind = "Project", Id = id, RootPaths = [directory, file],
                    Disposition = SharedImportDisposition.Conflict, Issues = ["project_directory_format_unconfirmed"] });
                continue;
            }
            // Include both representations so a different representation of the same ID conflicts.
            var item = await DescribeItemAsync("Project", id, [directory, file], source, target, token);
            items.Add(await CheckProjectDependenciesAsync(item, source, target, file, items, token));
        }
        if (includeKnowledge && Directory.Exists(Safe(source, "antigravity/knowledge")))
            items.Add(await DescribeItemAsync("Knowledge", "knowledge", ["antigravity/knowledge"], source, target, token));
        if (includeKnowledge && File.Exists(Safe(source, "config/memory.txtpb")))
            items.Add(await DescribeItemAsync("Memory", "memory", ["config/memory.txtpb"], source, target, token));
        return new() { Id = Guid.NewGuid(), GroupId = groupId, SourceProfileId = profileId, CreatedUtc = DateTimeOffset.UtcNow,
            ProjectIds = projects, ConversationIds = conversations, IncludeKnowledge = includeKnowledge, Items = items };
    }

    private static List<string> ConversationRoots(string id) =>
        [$"antigravity/conversations/{id}.db", $"antigravity/brain/{id}", $"antigravity/annotations/{id}.pbtxt"];

    private static async Task<SharedImportItem> DescribeItemAsync(string kind, string id, List<string> roots, string source, string target, CancellationToken token)
    {
        var from = await DescribeAsync(source, roots, token); var to = await DescribeAsync(target, roots, token);
        var digests = new Dictionary<string, string>();
        foreach (var root in roots) digests[root] = (await DescribeAsync(source, [root], token)).Digest;
        return new() { Kind = kind, Id = id, RootPaths = roots, RootDigests = digests, SourceDigest = from.Digest, TargetDigest = to.Any ? to.Digest : null,
            Disposition = !to.Any ? SharedImportDisposition.Copy : from.Digest == to.Digest ? SharedImportDisposition.Duplicate : SharedImportDisposition.Conflict };
    }

    private static async Task<(string Digest, bool Any)> DescribeAsync(string root, IReadOnlyList<string> roots, CancellationToken token)
    {
        var records = new List<string>(); var any = false;
        foreach (var relative in roots.Order(StringComparer.Ordinal)) await Visit(relative);
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', records)))), any);
        async Task Visit(string relative)
        {
            token.ThrowIfCancellationRequested();
            var path = Safe(root, relative);
            if (Directory.Exists(path))
            {
                any = true; records.Add("D:" + relative);
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                    await Visit(relative + "/" + Path.GetFileName(child));
            }
            else if (File.Exists(path))
            {
                any = true;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                records.Add("F:" + relative + ":" + stream.Length + ":" + Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
            }
            else records.Add("M:" + relative);
        }
    }

    private static async Task CopyTreeAsync(string sourceRoot, string targetRoot, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = Safe(sourceRoot, relative); var target = Safe(targetRoot, relative);
        if (Directory.Exists(source))
        {
            PathSafety.EnsureSafeDirectory(targetRoot, target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                await CopyTreeAsync(sourceRoot, targetRoot, relative + "/" + Path.GetFileName(child), token);
        }
        else if (File.Exists(source))
        {
            PathSafety.EnsureSafeDirectory(targetRoot, Path.GetDirectoryName(target)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, token); await output.FlushAsync(token);
        }
    }

    private static void DeleteTree(string path)
    {
        PathSafety.RejectExistingPathAncestors(path);
        if (Directory.Exists(path))
        {
            // Validate the whole tree before deleting any part; do not traverse replacement links.
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path)) File.Delete(path);
        static void CheckTree(string child)
        {
            PathSafety.RejectExistingPathAncestors(child);
            if (Directory.Exists(child)) foreach (var nested in Directory.EnumerateFileSystemEntries(child)) CheckTree(nested);
        }
    }
    private static string Safe(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Split('/','\\').Any(p => p is "" or "." or ".."))
            throw new ConfigurationException("Import content path is invalid.", "shared_import_path_invalid");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        PathSafety.RejectExistingReparsePoints(root, path);
        return path;
    }
    private static void ValidateContentPath(string path)
    {
        if (!(path.StartsWith("config/projects/", StringComparison.Ordinal) || path.StartsWith("antigravity/conversations/", StringComparison.Ordinal) ||
            path.StartsWith("antigravity/brain/", StringComparison.Ordinal) || path.StartsWith("antigravity/annotations/", StringComparison.Ordinal) ||
            path is "antigravity/knowledge" or "config/memory.txtpb"))
            throw new ConfigurationException("Import record includes an unsupported content path.", "shared_import_invalid");
    }
    private static string ValidateSelectionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "." or ".." || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new HubException("Select a project or conversation ID from the source catalog.", "shared_import_id_invalid");
        return id;
    }
    private static void RequireMember(SharedWorkspaceGroup group, Guid profileId)
    {
        if (!group.AccountProfileIds.Contains(profileId)) throw new HubException("Add the source account to this shared group before importing.", "shared_account_not_member");
    }
    private static void EnsureSameItems(List<SharedImportItem> expected, List<SharedImportItem> actual)
    {
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual)) throw StalePreview();
    }
    private static HubException StalePreview() => new("Source or shared content changed. Create a new import preview before applying it.", "shared_import_preview_stale");
}
