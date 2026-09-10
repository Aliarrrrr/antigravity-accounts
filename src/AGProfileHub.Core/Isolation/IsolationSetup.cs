namespace AGProfileHub.Core;

public enum IsolationStage
{
    NotStarted = 0,
    BackendUnavailable = 1,
    BoxMissing = 2,
    KeyringPending = 3,
    WriteThroughMissing = 4,
    Ready = 5
}

public sealed record SetupStep(IsolationStage Stage, bool Applied, string? Detail);
public sealed record IsolationSetupReport(Guid ProfileId, IsolationStage Stage, IReadOnlyList<SetupStep> Steps, bool Ready);

/// <summary>
/// One idempotent state machine for isolation setup. It owns the whole chain —
/// box, registration, keyring claim, direct-write rules — so callers ask for a
/// ready profile and get one, instead of choreographing iso-prepare and
/// iso-writethrough by hand. Every step is a no-op when it is already done, so
/// the machine is safe to run on every launch.
/// </summary>
public sealed class IsolationSetup
{
    private readonly IProfileIsolation _isolation;
    private readonly IElevationBridge _elevation;
    private readonly Func<IIsolationBackend?> _backendFactory;

    public IsolationSetup(IProfileIsolation isolation, IElevationBridge elevation, Func<IIsolationBackend?> backendFactory)
    {
        _isolation = isolation;
        _elevation = elevation;
        _backendFactory = backendFactory;
    }

    public async Task<IsolationStage> DetectAsync(Guid profileId, string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var backend = _backendFactory();
        if (backend is null) return IsolationStage.BackendUnavailable;
        var record = await _isolation.FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return IsolationStage.BoxMissing;
        if (!backend.BoxExists(record.BoxName)) return IsolationStage.BoxMissing;
        if (!record.KeyringClaimed) return IsolationStage.KeyringPending;
        var status = await _isolation.GetStatusAsync(profileId, workspacePath, cancellationToken).ConfigureAwait(false);
        if (status is null) return IsolationStage.BoxMissing;
        if (!status.WriteThroughReady) return IsolationStage.WriteThroughMissing;
        return IsolationStage.Ready;
    }

    public async Task<IsolationSetupReport> EnsureReadyAsync(Guid profileId, bool allowFreshBox,
        string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var steps = new List<SetupStep>();
        var backend = _backendFactory()
            ?? throw new UnsupportedCapabilityException("Sandboxie Plus was not found. Isolation setup requires it.");

        var record = await _isolation.FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        var boxName = record?.BoxName ?? IsolatedProfileLauncher.BoxNameFor(profileId);
        var boxPreExisted = backend.BoxExists(boxName);

        if (!boxPreExisted)
        {
            var created = await _elevation.RunAsync(new ElevationRequest(AdminOperation.CreateBox, boxName, []), cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
                throw new HubException($"Isolation box '{boxName}' could not be created: {created.Detail}", "sandbox_create_failed");
            if (!backend.BoxExists(boxName))
                throw new HubException($"Isolation box '{boxName}' is still missing after the elevated step.", "sandbox_create_failed");
            steps.Add(new SetupStep(IsolationStage.BoxMissing, true, $"Created isolation box {boxName}."));
        }
        else
        {
            steps.Add(new SetupStep(IsolationStage.BoxMissing, false, null));
        }

        // A box we just created is necessarily fresh; taking over one that
        // pre-existed still needs an explicit acknowledgement from the user.
        var claimFresh = record is null && (allowFreshBox || !boxPreExisted);
        if (record is null || !record.KeyringClaimed)
        {
            var prepared = await _isolation.PrepareAsync(profileId, record?.BoxName, claimExistingFreshBox: claimFresh, cancellationToken).ConfigureAwait(false);
            steps.Add(new SetupStep(IsolationStage.KeyringPending, true, $"Registered box {prepared.BoxName} and claimed the keyring entry."));
        }
        else
        {
            steps.Add(new SetupStep(IsolationStage.KeyringPending, false, null));
        }

        var status = await _isolation.GetStatusAsync(profileId, workspacePath, cancellationToken).ConfigureAwait(false);
        if (status is not null && !status.WriteThroughReady)
        {
            var paths = _isolation.WriteThroughPaths(profileId, workspacePath);
            var applied = await _elevation.RunAsync(new ElevationRequest(AdminOperation.WriteThrough, status.BoxName, paths), cancellationToken).ConfigureAwait(false);
            if (!applied.Succeeded)
                throw new HubException($"Direct-write rules could not be applied: {applied.Detail}", "sandbox_writethrough_failed");
            steps.Add(new SetupStep(IsolationStage.WriteThroughMissing, true, $"Opened {paths.Count} direct-write path(s)."));
        }
        else
        {
            steps.Add(new SetupStep(IsolationStage.WriteThroughMissing, false, null));
        }

        var stage = await DetectAsync(profileId, workspacePath, cancellationToken).ConfigureAwait(false);
        return new IsolationSetupReport(profileId, stage, steps, stage == IsolationStage.Ready);
    }
}
