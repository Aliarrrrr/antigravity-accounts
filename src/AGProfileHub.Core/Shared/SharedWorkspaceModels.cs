using System.Text.Json.Serialization;

namespace AGProfileHub.Core;

public sealed record SharedWorkspaceGroup
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public List<Guid> AccountProfileIds { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record SharedImportCatalog(Guid SourceProfileId, IReadOnlyList<string> ProjectIds,
    IReadOnlyList<string> ConversationIds, bool KnowledgeAvailable, bool MemoryAvailable = false);

public enum SharedImportDisposition { Copy, Duplicate, Conflict, Blocked }
public sealed record SharedImportItem
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public List<string> RootPaths { get; init; } = [];
    public Dictionary<string, string> RootDigests { get; init; } = [];
    public string SourceDigest { get; init; } = "";
    public string? TargetDigest { get; init; }
    public SharedImportDisposition Disposition { get; init; }
    public List<string> Issues { get; init; } = [];
    public List<string> ConversationDependencies { get; init; } = [];
}

public sealed record SharedImportPreview
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; }
    public Guid GroupId { get; init; }
    public Guid SourceProfileId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public List<string> ProjectIds { get; init; } = [];
    public List<string> ConversationIds { get; init; } = [];
    public bool IncludeKnowledge { get; init; }
    public List<SharedImportItem> Items { get; init; } = [];
    // File retention is not proof that the installed official client can resume these records.
    public bool ClientRestoreValidated => false;
    public string[] Limitations => ["conversation_index_not_imported", "official_client_restore_unvalidated"];
}

public sealed record SharedImportResult
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ImportId { get; init; }
    public Guid GroupId { get; init; }
    public string State { get; init; } = "Applying";
    public List<string> ImportedPaths { get; init; } = [];
    public List<string> SkippedPaths { get; init; } = [];
    public List<SharedImportItem> ImportedItems { get; init; } = [];
    public bool ClientRestoreValidated => false;
    public string[] Limitations => ["conversation_index_not_imported", "official_client_restore_unvalidated"];
    public DateTimeOffset UpdatedUtc { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SharedWorkspaceGroup))]
[JsonSerializable(typeof(SharedImportPreview))]
[JsonSerializable(typeof(SharedImportResult))]
internal partial class SharedWorkspaceJsonContext : JsonSerializerContext;
