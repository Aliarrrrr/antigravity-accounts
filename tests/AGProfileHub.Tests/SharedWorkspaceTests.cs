using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AGProfileHub.Core;

internal static class SharedWorkspaceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Shared workspace account references preserve independent profile content", MembershipAsync),
        ("Shared workspace selected imports preserve source IDs and restore imported records", ImportRestoreAsync),
        ("Shared workspace memory selection preserves source and skips conflicting memory", MemoryAsync),
        ("Shared workspace same IDs deduplicate and conflicting conversation artifacts stay together", ConflictsAsync),
        ("Shared workspace annotations stay with their conversation on import conflict and restore", AnnotationsAsync),
        ("Shared workspace projects refuse missing or conflicting conversation dependencies", ProjectDependenciesAsync),
        ("Shared workspace unknown project relationships and conversation formats are blocked", UnknownFormatsAsync),
        ("Shared workspace unsupported relationship spellings do not bypass an empty project index", RelationshipSpellingsAsync),
        ("Shared workspace unknown project directories are neither read nor copied", ProjectDirectoryAsync),
        ("Shared workspace destination database journals block duplicate and project dependency shortcuts", DestinationJournalAsync),
        ("Shared workspace changed source or target requires another preview", StaleAsync),
        ("Shared workspace empty selection does not import every project", EmptySelectionAsync),
        ("Shared workspace attached and unresolved runtime blocks import", RuntimeAsync),
        ("Shared workspace group lock cancellation does not publish content", LockCancellationAsync),
        ("Shared workspace metadata remains readable while a group mutation holds its lock", ReadWhileLockedAsync),
        ("Shared workspace partial import exposes its recovery ID and restores published roots", PartialImportAsync),
        ("Shared workspace live source database journal and unsafe IDs are refused", SourceSafetyAsync),
        ("Shared workspace restore preserves edited imports and other groups", EditedRestoreAsync),
        ("Shared workspace source links are refused without traversing them", LinksAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task MembershipAsync()
    {
        using var f = new Fixture(); var profile = await f.ProfileAsync("Account A");
        var group = await f.Service.CreateAsync(" Work ");
        Check(group.Name == "Work" && (await f.Service.ResolveAsync("wor")).Id == group.Id, "Group resolution or normalized name changed.");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.CreateAsync("work"), "shared_group_name_conflict");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.AddAccountAsync(group.Id, Guid.NewGuid()), "profile_not_found");
        await f.WriteSourceAsync(profile.Id, "private.txt", "private profile data");
        await f.Service.AddAccountAsync(group.Id, profile.Id); await f.Service.AddAccountAsync(group.Id, profile.Id);
        Check((await f.Service.ReadAsync(group.Id)).AccountProfileIds.Count == 1, "Repeated membership duplicated an account.");
        await f.Service.RemoveAccountAsync(group.Id, profile.Id);
        Check((await f.Service.ReadAsync(group.Id)).AccountProfileIds.Count == 0 &&
            await File.ReadAllTextAsync(f.Source(profile.Id, "private.txt")) == "private profile data", "Removing membership altered a profile.");
    }

    private static async Task ImportRestoreAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync();
        await f.SeedAsync(profile.Id);
        await f.WriteSourceAsync(profile.Id, "config.json", "PRIVATE-TOPLEVEL-CONFIG");
        await f.WriteSourceAsync(profile.Id, "credentials/auth.json", "PRIVATE-AUTH");
        var catalog = await f.Service.PreviewCatalogAsync(profile.Id);
        Check(catalog.ProjectIds.SequenceEqual(["project-a"]) && catalog.ConversationIds.SequenceEqual(["conversation-a"]) && catalog.KnowledgeAvailable, "Source catalog did not identify selected records.");
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], ["conversation-a"], true);
        Check(preview.Items.Count == 3 && preview.Items.All(i => i.Disposition == SharedImportDisposition.Copy), "Fresh import preview was incorrect.");
        var applied = await f.Service.ApplyImportAsync(group.Id, preview.Id);
        Check(applied.State == "Applied" && applied.ImportedPaths.Count == 4, "Selected content did not publish.");
        foreach (var relative in f.SeededFiles)
        {
            var originalBytes = await File.ReadAllBytesAsync(f.Source(profile.Id, relative));
            var importedBytes = await File.ReadAllBytesAsync(f.Target(group.Id, relative));
            Check(originalBytes.SequenceEqual(importedBytes), "Imported file changed: " + relative);
        }
        Check(!File.Exists(f.Target(group.Id, "config.json")) && !Directory.Exists(f.Target(group.Id, "credentials")), "Import crossed the configuration or authentication boundary.");
        var restored = await f.Service.RestoreImportAsync(group.Id, applied.ImportId);
        Check(restored.State == "Restored" && !File.Exists(f.Target(group.Id, f.SeededFiles[1])) &&
            !File.Exists(f.Target(group.Id, f.SeededFiles[0])), "Restore did not remove imported records.");
        Check((await f.Service.RestoreImportAsync(group.Id, applied.ImportId)).State == "Restored", "Restore was not repeatable.");
        foreach (var relative in f.SeededFiles) Check(File.Exists(f.Source(profile.Id, relative)), "Restore deleted source content.");
    }

    private static async Task ConflictsAsync()
    {
        using var f = new Fixture(); var (a, group) = await f.SetupAsync(); var b = await f.ProfileAsync("Account B");
        await f.Service.AddAccountAsync(group.Id, b.Id); await f.SeedAsync(a.Id); await f.SeedAsync(b.Id);
        var first = await f.Service.PreviewImportAsync(group.Id, a.Id, ["project-a"], ["conversation-a"], true);
        await f.Service.ApplyImportAsync(group.Id, first.Id);
        var same = await f.Service.PreviewImportAsync(group.Id, b.Id, ["project-a"], ["conversation-a"], true);
        Check(same.Items.All(i => i.Disposition == SharedImportDisposition.Duplicate), "Identical IDs and bytes did not deduplicate.");
        await f.WriteSourceAsync(b.Id, "antigravity/brain/conversation-a/result.md", "account B result");
        await f.WriteSourceAsync(b.Id, f.SeededFiles[0], "{\"id\":\"project-a\",\"name\":\"different project\"}");
        var conflict = await f.Service.PreviewImportAsync(group.Id, b.Id, ["project-a"], ["conversation-a"], false);
        Check(conflict.Items.All(i => i.Disposition == SharedImportDisposition.Conflict), "Different content for one ID was not reported as conflict.");
        var applied = await f.Service.ApplyImportAsync(group.Id, conflict.Id);
        Check(applied.ImportedPaths.Count == 0 && await File.ReadAllTextAsync(f.Target(group.Id, f.SeededFiles[0])) == await File.ReadAllTextAsync(f.Source(a.Id, f.SeededFiles[0])) &&
            await File.ReadAllTextAsync(f.Target(group.Id, "antigravity/brain/conversation-a/result.md")) == "original result", "Conflict overwrote or mixed existing content.");
        await f.Service.RestoreImportAsync(group.Id, applied.ImportId);
        Check(File.Exists(f.Target(group.Id, "antigravity/conversations/conversation-a.db")), "Restoring skipped content deleted another import.");
    }

    private static async Task AnnotationsAsync()
    {
        using var f = new Fixture(); var (a, group) = await f.SetupAsync(); var b = await f.ProfileAsync("Account B");
        await f.Service.AddAccountAsync(group.Id, b.Id); await f.SeedAsync(a.Id); await f.SeedAsync(b.Id);
        const string annotation = "antigravity/annotations/conversation-a.pbtxt";
        await f.WriteSourceAsync(a.Id, annotation, "title: \"Original title\"");
        await f.WriteSourceAsync(b.Id, annotation, "title: \"Other title\"");
        var preview = await f.Service.PreviewImportAsync(group.Id, a.Id, [], ["conversation-a"], false);
        var result = await f.Service.ApplyImportAsync(group.Id, preview.Id);
        Check(File.Exists(f.Target(group.Id, annotation)) && !preview.ClientRestoreValidated && !result.ClientRestoreValidated &&
            preview.Limitations.Contains("conversation_index_not_imported"), "Annotation import or restoration limitations were not retained.");
        var conflict = await f.Service.PreviewImportAsync(group.Id, b.Id, [], ["conversation-a"], false);
        Check(conflict.Items.Single().Disposition == SharedImportDisposition.Conflict, "Different annotations did not conflict with the entire conversation group.");
        await f.Service.ApplyImportAsync(group.Id, conflict.Id);
        Check(await File.ReadAllTextAsync(f.Target(group.Id, annotation)) == "title: \"Original title\"", "A conflicting annotation replaced an existing conversation annotation.");
        await f.Service.RestoreImportAsync(group.Id, result.ImportId);
        Check(!File.Exists(f.Target(group.Id, annotation)) && File.Exists(f.Source(a.Id, annotation)), "Annotation restore removed the source or left imported data.");
    }

    private static async Task ProjectDependenciesAsync()
    {
        using var f = new Fixture(); var (a, group) = await f.SetupAsync(); var b = await f.ProfileAsync("Account B");
        await f.Service.AddAccountAsync(group.Id, b.Id); await f.SeedAsync(a.Id); await f.SeedAsync(b.Id);
        var missing = await f.Service.PreviewImportAsync(group.Id, a.Id, ["project-a"], [], false);
        Check(missing.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            missing.Items[0].ConversationDependencies.SequenceEqual(["conversation-a"]) &&
            missing.Items[0].Issues.Contains("conversation_dependency_missing:conversation-a"), "An unselected missing conversation did not block its project.");
        Check((await f.Service.ApplyImportAsync(group.Id, missing.Id)).ImportedPaths.Count == 0, "A blocked project was imported.");
        var conversation = await f.Service.PreviewImportAsync(group.Id, a.Id, [], ["conversation-a"], false);
        await f.Service.ApplyImportAsync(group.Id, conversation.Id);
        var satisfied = await f.Service.PreviewImportAsync(group.Id, a.Id, ["project-a"], [], false);
        Check(satisfied.Items.Single().Disposition == SharedImportDisposition.Copy, "An already imported identical conversation failed to satisfy its project dependency.");
        await f.WriteSourceAsync(b.Id, "antigravity/brain/conversation-a/result.md", "different related work");
        var conflict = await f.Service.PreviewImportAsync(group.Id, b.Id, ["project-a"], ["conversation-a"], false);
        Check(conflict.Items.Single(i => i.Kind == "Project").Disposition == SharedImportDisposition.Blocked &&
            conflict.Items.Single(i => i.Kind == "Project").Issues.Contains("conversation_dependency_conflict:conversation-a"),
            "A project pointing to the conflicting source conversation was allowed to attach to the destination conversation.");
        Check((await f.Service.ApplyImportAsync(group.Id, conflict.Id)).ImportedPaths.Count == 0, "A dependency conflict published a project or changed existing work.");
        var valid = await f.Service.PreviewImportAsync(group.Id, a.Id, ["project-a"], ["conversation-a"], false);
        Check(valid.Items.FindIndex(i => i.Kind == "Conversation") < valid.Items.FindIndex(i => i.Kind == "Project"),
            "Projects would be published before their selected conversations.");
        await f.Service.ApplyImportAsync(group.Id, valid.Id);
        Check(File.Exists(f.Target(group.Id, f.SeededFiles[0])) && !File.Exists(f.Target(group.Id, "antigravity/conversation_summaries.db")),
            "Project import was incomplete or copied an entire unselected conversation index.");
    }

    private static async Task UnknownFormatsAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        foreach (var association in new[] { "\"environments\":[{\"id\":\"unmapped\"}]", "\"permissionGrants\":[{\"id\":\"unmapped\"}]" })
        {
            await f.WriteSourceAsync(profile.Id, f.SeededFiles[0], "{\"id\":\"project-a\"," + association + "}");
            var unknownAssociation = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], ["conversation-a"], false);
            Check(unknownAssociation.Items.Single(i => i.Kind == "Project").Disposition == SharedImportDisposition.Blocked &&
                unknownAssociation.Items.Single(i => i.Kind == "Project").Issues.Contains("project_environment_or_permission_format_unconfirmed"),
                "An unmapped project environment or permission association was accepted.");
        }
        await f.WriteSourceAsync(profile.Id, f.SeededFiles[0], "{\"id\":\"project-a\"}");
        File.Delete(f.Source(profile.Id, "antigravity/conversation_summaries.db"));
        var noIndex = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        Check(noIndex.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            noIndex.Items[0].Issues.Contains("project_conversation_index_unconfirmed"), "A project with unknown relationships was copied.");
        Fixture.CreateDatabase(f.Source(profile.Id, "antigravity/conversation_summaries.db"), """
            PRAGMA user_version=99;
            CREATE TABLE conversation_summaries (conversation_id TEXT, project_id TEXT, parent_conversation_id TEXT, raw_summary BLOB);
            """);
        var unknownIndex = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        Check(unknownIndex.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            unknownIndex.Items[0].Issues.Contains("project_conversation_index_unconfirmed"), "An unknown index version was accepted.");
        await f.WriteSourceAsync(profile.Id, f.SeededFiles[0], "{\"id\":\"project-a\",\"projectConversations\":{\"conversations\":[{\"conversationId\":\"conversation-a\",\"environmentId\":\"unmapped\"}]}}");
        var environment = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], ["conversation-a"], false);
        Check(environment.Items.Single(i => i.Kind == "Project").Issues.Contains("project_environment_format_unconfirmed") &&
            environment.Items.Single(i => i.Kind == "Project").Issues.Contains("project_conversation_index_unconfirmed") &&
            environment.Items.Single(i => i.Kind == "Project").Disposition == SharedImportDisposition.Blocked,
            "An unresolved environment reference or an explicit relationship bypass of an unknown index was accepted.");
        await File.WriteAllBytesAsync(f.Source(profile.Id, f.SeededFiles[1]), [0, 17, 128, 255, 10]);
        var invalid = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        Check(invalid.Items.Single().Disposition == SharedImportDisposition.Blocked && invalid.Items[0].Issues.Count > 0 &&
            (await f.Service.ApplyImportAsync(group.Id, invalid.Id)).ImportedPaths.Count == 0,
            "An arbitrary file with a .db extension was treated as a resumable conversation database.");
        f.CreateConversation(profile.Id, "conversation-a", "different-id");
        var wrongId = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        Check(wrongId.Items.Single().Disposition == SharedImportDisposition.Blocked, "Conversation metadata with a different ID was imported under the selected ID.");
        Fixture.CreateDatabase(f.Source(profile.Id, f.SeededFiles[1]), "PRAGMA user_version=1; CREATE TABLE arbitrary_table(value TEXT);");
        var unknownSchema = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        Check(unknownSchema.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            unknownSchema.Items[0].Issues.Contains("shared_conversation_format_unsupported"), "An unknown conversation schema was accepted.");
        f.CreateConversation(profile.Id, "conversation-a", "conversation-a", "CREATE TABLE sqliteXextra_account_data(value TEXT);");
        var extraTable = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        Check(extraTable.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            extraTable.Items[0].Issues.Contains("shared_conversation_format_unsupported"),
            "A non-internal extra table was ignored by the supported schema check and could be copied with the database.");
    }

    private static async Task RelationshipSpellingsAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        Fixture.CreateDatabase(f.Source(profile.Id, "antigravity/conversation_summaries.db"), """
            PRAGMA user_version=3;
            CREATE TABLE conversation_summaries (conversation_id TEXT, project_id TEXT, parent_conversation_id TEXT, raw_summary BLOB);
            """);
        foreach (var association in new[]
        {
            "\"project_conversations\":{\"conversations\":[{\"conversation_id\":\"conversation-a\"}]}",
            "\"projectConversations\":{\"conversations\":[{\"conversationId\":\"conversation-a\",\"environment_id\":\"unmapped\"}]}",
            "\"conversation_id\":\"conversation-a\"",
            "\"environment_id\":\"unmapped\"",
            "\"permission_grants\":[{\"id\":\"unmapped\"}]"
        })
        {
            await f.WriteSourceAsync(profile.Id, f.SeededFiles[0], "{\"id\":\"project-a\"," + association + "}");
            var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
            Check(preview.Items.Single().Disposition == SharedImportDisposition.Blocked &&
                preview.Items[0].Issues.Contains("project_relationship_field_unsupported"),
                "An unsupported relationship spelling bypassed dependency checks when the index returned no related conversations.");
            Check((await f.Service.ApplyImportAsync(group.Id, preview.Id)).ImportedPaths.Count == 0,
                "An unsupported relationship spelling published a project.");
        }
    }

    private static async Task ProjectDirectoryAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        File.Delete(f.Source(profile.Id, f.SeededFiles[0]));
        const string metadata = "config/projects/project-a/project.json";
        const string secret = "config/projects/project-a/oauth_creds.json";
        const string hiddenConversation = "config/projects/project-a/hidden-conversation.db";
        await f.WriteSourceAsync(profile.Id, metadata, "{\"id\":\"project-a\"}");
        await f.WriteSourceAsync(profile.Id, secret, "FAKE-SECRET: must not be read or imported");
        await f.WriteSourceAsync(profile.Id, hiddenConversation, "unselected conversation content");
        // Locks prove that neither preview nor its apply-time rebuild reads any file in the unknown layout.
        using (var metadataLock = new FileStream(f.Source(profile.Id, metadata), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var secretLock = new FileStream(f.Source(profile.Id, secret), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var conversationLock = new FileStream(f.Source(profile.Id, hiddenConversation), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
            Check(preview.Items.Single().Disposition == SharedImportDisposition.Blocked &&
                preview.Items[0].Issues.Contains("project_directory_format_unconfirmed"), "An unconfirmed project directory was not blocked before file reads.");
            Check((await f.Service.ApplyImportAsync(group.Id, preview.Id)).ImportedPaths.Count == 0 &&
                !Directory.Exists(f.Target(group.Id, "config")), "A project directory leaked its unknown files into shared content.");
        }
        var flatSource = await f.ProfileAsync("Flat source"); await f.Service.AddAccountAsync(group.Id, flatSource.Id); await f.SeedAsync(flatSource.Id);
        await f.WriteTargetAsync(group.Id, secret, "FAKE-TARGET-SECRET");
        using (var targetLock = new FileStream(f.Target(group.Id, secret), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var conflict = await f.Service.PreviewImportAsync(group.Id, flatSource.Id, ["project-a"], [], false);
            Check(conflict.Items.Single().Disposition == SharedImportDisposition.Conflict &&
                (await f.Service.ApplyImportAsync(group.Id, conflict.Id)).ImportedPaths.Count == 0 &&
                !File.Exists(f.Target(group.Id, f.SeededFiles[0])), "A flat project read or overwrote an unknown destination representation of the same ID.");
        }
        Check(await File.ReadAllTextAsync(f.Source(profile.Id, secret)) == "FAKE-SECRET: must not be read or imported" &&
            await File.ReadAllTextAsync(f.Target(group.Id, secret)) == "FAKE-TARGET-SECRET", "Blocking an unknown project directory altered its contents.");
    }

    private static async Task DestinationJournalAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        var first = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        await f.Service.ApplyImportAsync(group.Id, first.Id);
        const string journal = "antigravity/conversations/conversation-a.db-wal";
        await f.WriteTargetAsync(group.Id, journal, "destination writer");
        var project = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        Check(project.Items.Single().Disposition == SharedImportDisposition.Blocked &&
            project.Items[0].Issues.Contains("conversation_dependency_unverified:conversation-a"),
            "A destination with uncommitted data satisfied an unselected conversation dependency.");
        var duplicate = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], ["conversation-a"], false);
        Check(duplicate.Items.Single(i => i.Kind == "Conversation").Disposition == SharedImportDisposition.Blocked &&
            duplicate.Items.Single(i => i.Kind == "Project").Disposition == SharedImportDisposition.Blocked &&
            (await f.Service.ApplyImportAsync(group.Id, duplicate.Id)).ImportedPaths.Count == 0,
            "A destination with uncommitted data bypassed validation as a duplicate conversation.");
        Check(await File.ReadAllTextAsync(f.Target(group.Id, journal)) == "destination writer", "Import changed the existing database journal.");
    }

    private static async Task MemoryAsync()
    {
        using var f = new Fixture(); var (a, group) = await f.SetupAsync(); var b = await f.ProfileAsync("Account B");
        await f.Service.AddAccountAsync(group.Id, b.Id);
        await f.WriteSourceAsync(a.Id, "config/memory.txtpb", "memories { text: \"account A memory\" }");
        await f.WriteSourceAsync(b.Id, "config/memory.txtpb", "memories { text: \"account B memory\" }");
        Check((await f.Service.PreviewCatalogAsync(a.Id)).MemoryAvailable, "Catalog missed the known memory file.");
        Check((await f.Service.PreviewImportAsync(group.Id, a.Id, [], [], false)).Items.Count == 0, "Unselected memory was implicitly imported.");
        var preview = await f.Service.PreviewImportAsync(group.Id, a.Id, [], [], true);
        Check(preview.Items.Count == 1 && preview.Items[0].Kind == "Memory", "Knowledge and memory selection did not include known memory.");
        var first = await f.Service.ApplyImportAsync(group.Id, preview.Id);
        Check(await File.ReadAllTextAsync(f.Target(group.Id, "config/memory.txtpb")) == await File.ReadAllTextAsync(f.Source(a.Id, "config/memory.txtpb")), "Memory import changed source bytes.");
        var conflict = await f.Service.PreviewImportAsync(group.Id, b.Id, [], [], true);
        Check(conflict.Items.Single().Disposition == SharedImportDisposition.Conflict, "Different memory was not reported as conflict.");
        var skipped = await f.Service.ApplyImportAsync(group.Id, conflict.Id);
        Check(skipped.ImportedPaths.Count == 0 && (await File.ReadAllTextAsync(f.Target(group.Id, "config/memory.txtpb"))).Contains("account A memory"), "Conflict overwrote account A memory.");
        await f.Service.RestoreImportAsync(group.Id, first.ImportId);
        Check(!File.Exists(f.Target(group.Id, "config/memory.txtpb")) && File.Exists(f.Source(a.Id, "config/memory.txtpb")) &&
            File.Exists(f.Source(b.Id, "config/memory.txtpb")), "Memory restore removed source files or retained imported memory.");
    }

    private static async Task StaleAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        await f.WriteSourceAsync(profile.Id, f.SeededFiles[0], "changed after preview");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.ApplyImportAsync(group.Id, preview.Id), "shared_import_preview_stale");
        Check(!File.Exists(f.Target(group.Id, f.SeededFiles[0])), "Stale source preview published data.");
        preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        await f.WriteTargetAsync(group.Id, f.SeededFiles[0], "created after preview");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.ApplyImportAsync(group.Id, preview.Id), "shared_import_preview_stale");
        Check(await File.ReadAllTextAsync(f.Target(group.Id, f.SeededFiles[0])) == "created after preview", "Stale target preview overwrote an existing record.");
        var other = await f.Service.CreateAsync("Other"); await f.Service.AddAccountAsync(other.Id, profile.Id);
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.ApplyImportAsync(other.Id, preview.Id), "shared_import_preview_required");
    }

    private static async Task EmptySelectionAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], [], false);
        Check(preview.Items.Count == 0 && (await f.Service.ApplyImportAsync(group.Id, preview.Id)).ImportedPaths.Count == 0 &&
            !Directory.Exists(f.Target(group.Id, "config")), "An empty selection implicitly imported source projects.");
    }

    private static async Task RuntimeAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        var runtime = Path.Combine(f.Service.GetGroupRoot(group.Id), "runtime.json");
        foreach (var json in new[] { "{", "{}", JsonSerializer.Serialize(new { SchemaVersion = 1, GroupId = group.Id, State = "Ready", IsAttached = true, SendingBlocked = false }),
            JsonSerializer.Serialize(new { SchemaVersion = 2, GroupId = group.Id, State = "Ready", IsAttached = false, SendingBlocked = false }) })
        {
            await File.WriteAllTextAsync(runtime, json);
            await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, [], [], false), "shared_group_active");
        }
        await File.WriteAllTextAsync(runtime, JsonSerializer.Serialize(new { SchemaVersion = 1, GroupId = group.Id, State = "Ready", IsAttached = false, SendingBlocked = false }));
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false);
        await File.WriteAllTextAsync(runtime, JsonSerializer.Serialize(new { SchemaVersion = 1, GroupId = group.Id, State = "RecoveryRequired", IsAttached = false, SendingBlocked = true }));
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.ApplyImportAsync(group.Id, preview.Id), "shared_group_active");
        Check(!Directory.Exists(f.Target(group.Id, "config")), "Attached runtime allowed an import.");
    }

    private static async Task LockCancellationAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        await using var held = await FileLock.AcquireAsync(Path.Combine(f.Service.GetGroupRoot(group.Id), "group.lock"), TimeSpan.FromSeconds(1), default);
        using var cancel = new CancellationTokenSource(100);
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], [], false, cancel.Token));
        Check(!Directory.Exists(Path.Combine(f.Service.GetGroupRoot(group.Id), "previews")), "Cancelled group lock wait saved a preview.");
    }

    private static async Task SourceSafetyAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        await f.WriteSourceAsync(profile.Id, "antigravity/conversations/conversation-a.db-wal", "active writer");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false), "shared_import_source_busy");
        foreach (var invalid in new[] { "..", "../project-a", "a/b", "a\\b", "C:escape", "." })
            await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, [invalid], [], false), "shared_import_id_invalid");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, ["missing"], [], false), "shared_import_source_invalid");
    }

    private static async Task ReadWhileLockedAsync()
    {
        using var f = new Fixture(); var (_, group) = await f.SetupAsync();
        var other = await f.Service.CreateAsync("Other");
        await using var held = await FileLock.AcquireAsync(Path.Combine(f.Service.GetGroupRoot(group.Id), "group.lock"), TimeSpan.FromSeconds(1), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Check((await f.Service.ReadAsync(group.Id, timeout.Token)).Id == group.Id &&
            (await f.Service.ResolveAsync(group.Name, timeout.Token)).Id == group.Id &&
            (await f.Service.ReadAsync(other.Id, timeout.Token)).Id == other.Id &&
            (await f.Service.ResolveAsync(other.Name, timeout.Token)).Id == other.Id &&
            (await f.Service.ListAsync(timeout.Token)).Count == 2,
            "A held mutation lock blocked committed metadata for this or another shared group.");
    }

    private static async Task PartialImportAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        // The database can publish, but the second root cannot create its parent directory.
        // This exercises recovery after a real filesystem failure between root publications.
        await f.WriteTargetAsync(group.Id, "antigravity/brain", "preexisting blocking file");
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, [], ["conversation-a"], false);
        Check(preview.Items.Single().Disposition == SharedImportDisposition.Copy, "Partial publication scenario did not reach the copy operation.");
        var failure = await TestAssert.ThrowsAsync<IOException>(() => f.Service.ApplyImportAsync(group.Id, preview.Id));
        Check(failure.Data["SharedImportId"] is Guid, "Interrupted import did not expose a recovery ID.");
        var importId = (Guid)failure.Data["SharedImportId"]!;
        Check(File.Exists(f.Target(group.Id, "antigravity/conversations/conversation-a.db")), "Failure occurred before publishing the database; recovery was not exercised.");
        var restored = await f.Service.RestoreImportAsync(group.Id, importId);
        Check(restored.State == "Restored" && !File.Exists(f.Target(group.Id, "antigravity/conversations/conversation-a.db")) &&
            await File.ReadAllTextAsync(f.Target(group.Id, "antigravity/brain")) == "preexisting blocking file" &&
            File.Exists(f.Source(profile.Id, "antigravity/conversations/conversation-a.db")) &&
            await File.ReadAllTextAsync(f.Source(profile.Id, "antigravity/brain/conversation-a/result.md")) == "original result",
            "Partial import restore removed the blocking file or source content, or retained its published database.");
    }

    private static async Task EditedRestoreAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync(); await f.SeedAsync(profile.Id);
        var other = await f.Service.CreateAsync("Other"); await f.WriteTargetAsync(other.Id, "antigravity/brain/foreign/value.txt", "foreign");
        var preview = await f.Service.PreviewImportAsync(group.Id, profile.Id, ["project-a"], ["conversation-a"], true);
        var applied = await f.Service.ApplyImportAsync(group.Id, preview.Id);
        await f.WriteTargetAsync(group.Id, "antigravity/brain/conversation-a/new-work.md", "user edit");
        await TestAssert.ThrowsCodeAsync<HubException>(() => f.Service.RestoreImportAsync(group.Id, applied.ImportId), "shared_import_content_changed");
        Check(File.Exists(f.Target(group.Id, f.SeededFiles[0])) && File.Exists(f.Target(group.Id, f.SeededFiles[1])) &&
            await File.ReadAllTextAsync(f.Target(other.Id, "antigravity/brain/foreign/value.txt")) == "foreign", "Refused restore deleted shared or foreign content.");
    }

    private static async Task LinksAsync()
    {
        using var f = new Fixture(); var (profile, group) = await f.SetupAsync();
        var sourceProjects = f.Source(profile.Id, "config/projects"); Directory.CreateDirectory(sourceProjects);
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "value.txt"), "outside");
        var link = Path.Combine(sourceProjects, "linked");
        if (OperatingSystem.IsWindows()) await CreateJunctionAsync(link, outside);
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => f.Service.PreviewImportAsync(group.Id, profile.Id, ["linked"], [], false), "reparse_point_rejected");
            Check(await File.ReadAllTextAsync(Path.Combine(outside, "value.txt")) == "outside" && !Directory.Exists(f.Target(group.Id, "config")), "Source link was traversed.");
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(info) ?? throw new Exception("Could not start temporary junction creation.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(); await stdout; await stderr;
            throw new Exception("Temporary junction creation timed out.");
        }
        await stdout; var error = await stderr;
        Check(process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null, "Temporary junction creation failed: " + error);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OwnedTestDirectory _owned = new();
        public string Root => _owned.Root;
        public HubPaths Paths { get; }
        public ConfigRepository Repository { get; }
        public SharedWorkspaceService Service { get; }
        public string[] SeededFiles { get; } = ["config/projects/project-a.json", "antigravity/conversations/conversation-a.db", "antigravity/brain/conversation-a/result.md", "antigravity/knowledge/fact.md"];
        public Fixture()
        {
            Paths = new(Path.Combine(Root, "data"), Path.Combine(Root, "launch")); Repository = new(Paths); Service = new(Paths, Repository);
        }
        public Task<ProfileRegistration> ProfileAsync(string name) => new ProfileManager(Repository, Paths).CreateAsync(name, null, IsolationBackendKind.ProcessLocal);
        public async Task<(ProfileRegistration, SharedWorkspaceGroup)> SetupAsync()
        {
            var profile = await ProfileAsync("Account A"); var group = await Service.CreateAsync("Shared");
            await Service.AddAccountAsync(group.Id, profile.Id); return (profile, group);
        }
        public string Source(Guid profile, string relative) => Path.Combine(Paths.Home(profile), ".gemini", relative.Replace('/', Path.DirectorySeparatorChar));
        public string Target(Guid group, string relative) => Path.Combine(Service.GetContentRoot(group), ".gemini", relative.Replace('/', Path.DirectorySeparatorChar));
        public Task WriteSourceAsync(Guid profile, string relative, string value) => WriteAsync(Source(profile, relative), value);
        public Task WriteTargetAsync(Guid group, string relative, string value) => WriteAsync(Target(group, relative), value);
        private static Task WriteAsync(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return File.WriteAllTextAsync(path, value); }
        public async Task SeedAsync(Guid profile)
        {
            await WriteSourceAsync(profile, SeededFiles[0], "{\"id\":\"project-a\",\"memory\":\"kept verbatim\"}");
            CreateConversation(profile, "conversation-a", "conversation-a");
            CreateDatabase(Source(profile, "antigravity/conversation_summaries.db"), """
                PRAGMA user_version=3;
                CREATE TABLE conversation_summaries (conversation_id TEXT PRIMARY KEY, project_id TEXT, parent_conversation_id TEXT, raw_summary BLOB);
                INSERT INTO conversation_summaries VALUES ('conversation-a','project-a',NULL,X'0012');
                """);
            await WriteSourceAsync(profile, SeededFiles[2], "original result"); await WriteSourceAsync(profile, SeededFiles[3], "shared fact");
        }
        public void CreateConversation(Guid profile, string fileId, string cascadeId, string additionalSql = "") =>
            CreateDatabase(Source(profile, $"antigravity/conversations/{fileId}.db"), """
                PRAGMA user_version=1;
                CREATE TABLE battle_mode_infos (idx INTEGER, data BLOB);
                CREATE TABLE executor_metadata (idx INTEGER, data BLOB);
                CREATE TABLE gen_metadata (idx INTEGER, data BLOB, size INTEGER);
                CREATE TABLE parent_references (idx INTEGER, data BLOB);
                CREATE TABLE steps (idx INTEGER, step_type INTEGER, status INTEGER, has_subtrajectory INTEGER, metadata BLOB, error_details BLOB, permissions BLOB, task_details BLOB, render_info BLOB, step_payload BLOB, step_format INTEGER);
                CREATE TABLE trajectory_meta (trajectory_id TEXT, cascade_id TEXT, trajectory_type INTEGER, source INTEGER);
                CREATE TABLE trajectory_metadata_blob (id INTEGER, data BLOB);
                """ + $"INSERT INTO trajectory_meta VALUES ('separate-trajectory-id','{cascadeId.Replace("'", "''")}',0,0);" + additionalSql);

        public static void CreateDatabase(string path, string sql)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path);
            nint database = 0;
            try
            {
                Check(SqliteOpen(path, out database, 2 | 4, 0) == 0, "Could not create the synthetic SQLite database.");
                var code = SqliteExec(database, sql, 0, 0, out var error);
                if (error != 0) SqliteFree(error);
                Check(code == 0, "Could not seed the synthetic SQLite database.");
            }
            finally { if (database != 0) SqliteClose(database); }
        }
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint database, int flags, nint vfs);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteExec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint argument, out nint error);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SqliteFree(nint pointer);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SqliteClose(nint database);
        public void Dispose() => _owned.Dispose();
    }
}
