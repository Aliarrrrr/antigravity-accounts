namespace AGProfileHub.Core;

public enum IsolationStage
{
    NotStarted = 0,
    BoxMissing = 2,
    Ready = 5
}

public sealed record SetupStep(IsolationStage Stage, bool Applied, string? Detail);
public sealed record IsolationSetupReport(Guid ProfileId, IsolationStage Stage, IReadOnlyList<SetupStep> Steps, bool Ready);

/// <summary>
/// One idempotent state machine for isolation setup. It owns the whole chain —
/// scope creation and registration — so callers ask for a ready profile and get
/// one, instead of choreographing iso-prepare by hand. Every step is a no-op
/// when it is already done, so the machine is safe to run on every launch.
/// </summary>
internal sealed class IsolationSetup
{
    private readonly IProfileIsolation _isolation;
    private readonly Func<IsolationBackendKind, IIsolationBackend?> _backendFactory;

    public IsolationSetup(IProfileIsolation isolation, Func<IsolationBackendKind, IIsolationBackend?> backendFactory)
    {
        _isolation = isolation;
        _backendFactory = backendFactory;
    }

    public async Task<IsolationStage> DetectAsync(Guid profileId, IsolationBackendKind kind,
        string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var backend = _backendFactory(kind);
        if (backend is null) return IsolationStage.BoxMissing;
        var record = await _isolation.FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return IsolationStage.BoxMissing;
        if (!backend.BoxExists(record.BoxName)) return IsolationStage.BoxMissing;
        return IsolationStage.Ready;
    }

    public async Task<IsolationSetupReport> EnsureReadyAsync(Guid profileId, IsolationBackendKind kind, bool allowFreshBox,
        string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var steps = new List<SetupStep>();
        var backend = _backendFactory(kind)
            ?? throw new UnsupportedCapabilityException("The isolation backend could not be created.");

        var record = await _isolation.FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        var boxName = record?.BoxName ?? IsolatedProfileLauncher.BoxNameFor(profileId);
        var boxPreExisted = backend.BoxExists(boxName);

        if (!boxPreExisted)
        {
            // The scope is a directory tree the Hub owns, so it needs no
            // elevation and no system-wide configuration at all.
            backend.CreateBox(boxName);
            if (!backend.BoxExists(boxName))
                throw new HubException($"Isolation scope '{boxName}' could not be created.", "isolation_scope_create_failed");
            steps.Add(new SetupStep(IsolationStage.BoxMissing, true, $"Created isolation scope {boxName}."));
        }
        else
        {
            steps.Add(new SetupStep(IsolationStage.BoxMissing, false, null));
        }

        // A scope we just created is necessarily fresh; taking over one that
        // pre-existed still needs an explicit acknowledgement from the user.
        var claimFresh = record is null && (allowFreshBox || !boxPreExisted);
        if (record is null)
        {
            var prepared = await _isolation.PrepareAsync(profileId, record?.BoxName, claimExistingFreshBox: claimFresh, cancellationToken).ConfigureAwait(false);
            steps.Add(new SetupStep(IsolationStage.BoxMissing, true, $"Registered {prepared.BoxName} and prepared its isolation."));
        }

        var stage = await DetectAsync(profileId, kind, workspacePath, cancellationToken).ConfigureAwait(false);
        return new IsolationSetupReport(profileId, stage, steps, stage == IsolationStage.Ready);
    }
}
