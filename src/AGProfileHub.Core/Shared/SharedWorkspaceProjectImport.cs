using System.Text.Json;

namespace AGProfileHub.Core;

public sealed partial class SharedWorkspaceService
{
    private static async Task<SharedImportItem> CheckProjectDependenciesAsync(SharedImportItem item,
        string source, string target, string metadataPath, IReadOnlyList<SharedImportItem> selectedItems, CancellationToken token)
    {
        var issues = new List<string>();
        var dependencies = new List<string>();
        var path = Safe(source, metadataPath);
        if (!File.Exists(path)) issues.Add("project_metadata_missing");
        else
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !UniqueProperty(root, "id", out var identity) ||
                    identity.ValueKind != JsonValueKind.String || identity.GetString() != item.Id)
                    issues.Add("project_identity_unconfirmed");
                if (root.ValueKind == JsonValueKind.Object && HasUnmappedAssociation(root))
                    issues.Add("project_environment_or_permission_format_unconfirmed");
                if (HasUnsupportedRelationshipField(root))
                    issues.Add("project_relationship_field_unsupported");
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("projectConversations", out _))
                {
                    if (!UniqueProperty(root, "projectConversations", out var links) || links.ValueKind != JsonValueKind.Object ||
                        !UniqueProperty(links, "conversations", out var entries) || entries.ValueKind != JsonValueKind.Array)
                        issues.Add("project_conversation_format_unconfirmed");
                    else
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object || !UniqueProperty(entry, "conversationId", out var id) ||
                                id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                            {
                                issues.Add("project_conversation_format_unconfirmed");
                                continue;
                            }
                            try { dependencies.Add(ValidateSelectionId(id.GetString()!)); }
                            catch (HubException) { issues.Add("project_conversation_format_unconfirmed"); }
                            // Environment records have not yet been mapped to an importable disk format.
                            // Retaining an arbitrary environment ID could connect this project to different content.
                            if (entry.TryGetProperty("environmentId", out var environment) &&
                                (environment.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(environment.GetString())))
                                issues.Add("project_environment_format_unconfirmed");
                        }
                    }
                }
            }
            catch (JsonException) { issues.Add("project_metadata_invalid"); }
        }

        var indexed = SharedConversationDatabase.ReadProjectConversationIds(Safe(source, "antigravity/conversation_summaries.db"), item.Id);
        if (indexed is null)
        {
            issues.Add("project_conversation_index_unconfirmed");
        }
        else foreach (var id in indexed)
        {
            try { dependencies.Add(ValidateSelectionId(id)); }
            catch (HubException) { issues.Add("project_conversation_format_unconfirmed"); }
        }

        foreach (var id in dependencies.Distinct(StringComparer.Ordinal))
        {
            var selected = selectedItems.FirstOrDefault(candidate => candidate.Kind == "Conversation" && candidate.Id == id);
            if (selected is not null)
            {
                if (selected.Disposition is SharedImportDisposition.Conflict or SharedImportDisposition.Blocked)
                    issues.Add("conversation_dependency_conflict:" + id);
                continue;
            }
            // A matching ID at the destination is insufficient. Compare it with this source's known
            // file group before allowing an unselected dependency to satisfy the project reference.
            var roots = ConversationRoots(id);
            if (!File.Exists(Safe(source, roots[0])) || !File.Exists(Safe(target, roots[0])))
                issues.Add("conversation_dependency_missing:" + id);
            else
            {
                if (SharedConversationDatabase.ValidateConversation(Safe(source, roots[0]), id) is not null ||
                    SharedConversationDatabase.ValidateConversation(Safe(target, roots[0]), id) is not null)
                {
                    issues.Add("conversation_dependency_unverified:" + id);
                    continue;
                }
                var existing = await DescribeItemAsync("Conversation", id, roots, source, target, token);
                if (existing.Disposition != SharedImportDisposition.Duplicate)
                    issues.Add("conversation_dependency_conflict:" + id);
            }
        }
        return item with
        {
            ConversationDependencies = dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Issues = issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Disposition = issues.Count != 0 && item.Disposition != SharedImportDisposition.Conflict ?
                SharedImportDisposition.Blocked : item.Disposition
        };
    }

    private static bool HasUnmappedAssociation(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "environment" or "environments" or "environmentId" or "environmentIds" or
                    "projectEnvironments" or "permissionGrants" or "permissionGrantIds")
                    if (HasContent(property.Value)) return true;
                if (HasUnmappedAssociation(property.Value)) return true;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray()) if (HasUnmappedAssociation(entry)) return true;
        return false;
    }

    private static bool HasUnsupportedRelationshipField(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                // Only the inspected App spelling is handled. Do not interpret an unfamiliar spelling
                // as an empty relationship set, even when the summary index has no rows for this ID.
                if (property.Name is "project_conversations" or "project_conversation_ids" or "conversation_id" or
                    "conversation_ids" or "environment_id" or "environment_ids" or "project_environments" or
                    "permission_grants" or "permission_grant_ids" or "parent_conversation_id") return true;
                if (HasUnsupportedRelationshipField(property.Value)) return true;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray()) if (HasUnsupportedRelationshipField(entry)) return true;
        return false;
    }

    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() != 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };

    private static bool UniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in element.EnumerateObject())
            if (property.NameEquals(name)) { value = property.Value; count++; }
        return count == 1;
    }
}
