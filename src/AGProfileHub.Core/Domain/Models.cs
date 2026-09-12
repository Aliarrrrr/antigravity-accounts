using System.Text.Json.Serialization;

namespace AGProfileHub.Core;

public static class SchemaVersions
{
    public const int Current = 1;
}

/// <summary>
/// Which mechanism isolates a Profile's instance.
/// </summary>
/// <remarks>
/// The backend is an additive, optional field rather than a schema-version bump:
/// a document written by an older build simply deserialises to
/// <see cref="Sandboxie"/>, so no migration is needed and unknown future schemas
/// are still rejected. The one hazard is a downgrade: an older build reading
/// <see cref="ProcessLocal"/> would treat the Profile as Sandboxie, so the field
/// is only written when it differs from the default.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<IsolationBackendKind>))]
public enum IsolationBackendKind { Sandboxie = 0, ProcessLocal = 1 }

public sealed record ProfileRegistration(Guid Id, string Name, string? WorkspacePath, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc)
{
    /// <summary>Isolation backend for this Profile. Absent in older documents means Sandboxie.</summary>
    public IsolationBackendKind Backend { get; init; } = IsolationBackendKind.Sandboxie;
}

public sealed record ProfilesDocument
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public List<ProfileRegistration> Profiles { get; init; } = [];
}

public sealed record HubSettings
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public string? AntigravityExecutablePath { get; init; }
    public string? LastTestedExecutablePath { get; init; }
    public string? LastTestedVersion { get; init; }
    public int ProbeStrategyRevision { get; init; } = 1;
    public DateTimeOffset? AuthIsolationAttestedUtc { get; init; }
    public string? AuthIsolationAttestedVersion { get; init; }

    /// <summary>Backend applied to newly created Profiles. Absent in older documents means Sandboxie.</summary>
    public IsolationBackendKind DefaultBackend { get; init; } = IsolationBackendKind.Sandboxie;
}

public enum RuntimeState { Stopped, Running, Unknown }

public sealed record ProcessIdentity(int ProcessId, DateTimeOffset StartTimeUtc, string ExecutablePath, string UserDataPath);

public sealed record RuntimeRecord
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Guid ProfileId { get; init; }
    public ProcessIdentity Root { get; init; } = null!;
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record LaunchBarrierRecord
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Guid AttemptId { get; init; }
    public Guid ProfileId { get; init; }
    public string OriginDataRoot { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string UserDataPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public ProcessIdentity? Root { get; init; }
}

public sealed record ProfileStatus(Guid ProfileId, string Name, RuntimeState State, ProcessIdentity? Root, string? Detail, string? WorkspacePath,
    string? IsolationBoxName = null, bool? WriteThroughReady = null);
public sealed record LaunchResult(ProfileStatus Status, bool StartedNewProcess);
public sealed record StopResult(ProfileStatus Status, bool WasRunning, bool Graceful);

public enum CapabilityState { Verified, Observed, Experimental, ManualVerificationRequired, Unknown, Unsupported, Incompatible }
public sealed record CapabilityFinding(string Name, CapabilityState State, string Detail);
public sealed record DoctorReport(DateTimeOffset CheckedUtc, string DataRoot, string? ExecutablePath, string? ExecutableVersion,
    bool ExecutableFound, bool CompatibleForExperimentalLaunch, IReadOnlyList<CapabilityFinding> Capabilities,
    IReadOnlyList<string> Warnings, IReadOnlyList<ProfileStatus> Profiles);

public sealed record LaunchPlan(string ExecutablePath, string Version, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment, string SanitizedCommandTemplate);

public sealed record ProcessSnapshot(int ProcessId, int ParentProcessId, DateTimeOffset StartTimeUtc,
    string ExecutablePath, string CommandLine, nint MainWindowHandle);
public sealed record LaunchedProcess(int ProcessId, DateTimeOffset StartTimeUtc);

public class HubException(string message, string code, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
public sealed class ConfigurationException(string message, string code = "configuration_invalid", Exception? inner = null) : HubException(message, code, inner);
public sealed class ProfileNotFoundException(Guid id) : HubException($"Profile '{id}' was not found.", "profile_not_found");
public sealed class UnsupportedCapabilityException(string message) : HubException(message, "unsupported_capability");
public sealed class ProcessOwnershipException(string message) : HubException(message, "process_ownership_unknown");
public sealed class LaunchNotStartedException(string message, Exception? inner = null) : HubException(message, "launch_failed", inner);
public sealed class LaunchStateUncertainException(string message, Exception? inner = null) : HubException(message, "launch_state_uncertain", inner);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfilesDocument))]
[JsonSerializable(typeof(HubSettings))]
[JsonSerializable(typeof(RuntimeRecord))]
[JsonSerializable(typeof(LaunchBarrierRecord))]
[JsonSerializable(typeof(ProfileRegistration))]
[JsonSerializable(typeof(ProfileStatus))]
[JsonSerializable(typeof(LaunchResult))]
[JsonSerializable(typeof(StopResult))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(List<ProfileStatus>))]
[JsonSerializable(typeof(IsolationStateDocument))]
[JsonSerializable(typeof(PendingCallback))]
[JsonSerializable(typeof(DeepLinkRegistration))]
[JsonSerializable(typeof(ElevationRequest))]
[JsonSerializable(typeof(ElevationResult))]
public partial class HubJsonContext : JsonSerializerContext;
