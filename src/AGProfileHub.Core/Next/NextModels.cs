using System.Text.Json.Serialization;

namespace AGProfileHub.Core;

public sealed record InstallationFingerprint(string Executable, string Version, Dictionary<string, string> Components);
public sealed record CompatibilityProof
{
    public int SchemaVersion { get; init; } = 1;
    public int StrategyRevision { get; init; } = NextHubService.StrategyRevision;
    public InstallationFingerprint Installation { get; init; } = null!;
    public DateTimeOffset ProbedUtc { get; init; }
    public bool FileTokenStore { get; init; }
    public bool IndependentHome { get; init; }
    public bool IndependentUserData { get; init; }
    public bool ProcessesStopped { get; init; }
    public bool ProtocolRestored { get; init; }
    public DateTimeOffset? ManuallyAcceptedUtc { get; init; }
}
public sealed record NextRuntime
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProfileId { get; init; }
    public RuntimeState State { get; init; }
    public ProcessIdentity? Root { get; init; }
    public List<ProcessIdentity> Processes { get; init; } = [];
    public InstallationFingerprint? Installation { get; init; }
    public DateTimeOffset AttemptUtc { get; init; }
    public bool FileTokenStore { get; init; }
    public string? Detail { get; init; }
}
public sealed record BridgeDocument
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public Dictionary<string, string> ManagedLinks { get; init; } = [];
}
public sealed record CallbackQueue
{
    public int SchemaVersion { get; init; } = 1;
    public List<PendingCallback> Waiting { get; init; } = [];
}
public sealed record ConfigPreview(string Source, string Destination, IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> Conflicts);
public sealed record MigrationPreviewReceipt
{
    public int SchemaVersion { get; init; } = 1;
    public string SourceRoot { get; init; } = "";
    public string RegistrySha256 { get; init; } = "";
    public DateTimeOffset PreviewedUtc { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CompatibilityProof))]
[JsonSerializable(typeof(NextRuntime))]
[JsonSerializable(typeof(BridgeDocument))]
[JsonSerializable(typeof(CallbackQueue))]
[JsonSerializable(typeof(MigrationPreviewReceipt))]
internal partial class NextJsonContext : JsonSerializerContext;
