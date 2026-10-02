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
/// a document written by an older build simply deserialises without it, so no
/// migration is needed and unknown future schemas are still rejected. Sandboxie
/// support has been removed; the recorded value resolves to the same
/// process-local scope either way.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<IsolationBackendKind>))]
public enum IsolationBackendKind { Sandboxie = 0, ProcessLocal = 1 }

public sealed record ProfileRegistration(Guid Id, string Name, string? WorkspacePath, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    /// <summary>Isolation backend for this Profile. Absent in older documents means the process-local default.</summary>
    IsolationBackendKind Backend = IsolationBackendKind.ProcessLocal);

public sealed record ProfilesDocument
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public List<ProfileRegistration> Profiles { get; init; } = [];
}

public sealed record HubSettings
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public string? AntigravityExecutablePath { get; init; }

    /// <summary>
    /// SHA-256 of the configured executable, recorded when it was configured
    /// and verified before every launch or deep-link handoff. Absent in older
    /// documents means "not pinned": the executable keeps working, and doctor
    /// recommends reconfiguring to pin it. Like the backend field this is an
    /// additive optional change, not a schema bump.
    /// </summary>
    public string? ExecutableSha256 { get; init; }
    public string? LastTestedExecutablePath { get; init; }
    public string? LastTestedVersion { get; init; }
    public int ProbeStrategyRevision { get; init; } = 1;
    public DateTimeOffset? AuthIsolationAttestedUtc { get; init; }
    public string? AuthIsolationAttestedVersion { get; init; }

    /// <summary>
    /// Backend applied to newly created Profiles. Missing in JSON loads as
    /// process-local (the C# default). Older builds that recorded Sandboxie
    /// still resolve to the same process-local scope at launch.
    /// </summary>
    public IsolationBackendKind DefaultBackend { get; init; } = IsolationBackendKind.ProcessLocal;
}

public enum RuntimeState { Stopped, Running, Unknown, Starting, Stopping, Failed }

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

/// <summary>Written after a cold start proves the scope switched to its own file token store.</summary>
public sealed record ScopeAttestation
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public Guid ProfileId { get; init; }
    public DateTimeOffset VerifiedUtc { get; init; }
}

public sealed record ProfileStatus(Guid ProfileId, string Name, RuntimeState State, ProcessIdentity? Root, string? Detail, string? WorkspacePath,
    string? IsolationBoxName = null, bool? IsolationReady = null);
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
[JsonSerializable(typeof(PendingCallback))]
[JsonSerializable(typeof(DeepLinkRegistration))]
[JsonSerializable(typeof(ScopeAttestation))]
public partial class HubJsonContext : JsonSerializerContext;
