namespace AGProfileHub.Core;

public sealed record IsolationBoxRecord(Guid ProfileId, string BoxName, DateTimeOffset CreatedUtc);

public sealed record IsolationStateDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<IsolationBoxRecord> Boxes { get; init; } = [];
}

public sealed record IsolationPrepareResult(Guid ProfileId, string BoxName, bool BoxCreated, bool MappingCreated);
public sealed record IsolationLaunchResult(Guid ProfileId, string BoxName, IReadOnlyList<int> BoxPids, bool StartedNewProcess);
public sealed record IsolationRuntimeStatus(Guid ProfileId, string BoxName, bool BackendAvailable, bool BoxExists,
    int ProcessCount);

public interface IProfileIsolation
{
    Task<IsolationBoxRecord?> FindAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IsolationPrepareResult> PrepareAsync(Guid profileId, string? explicitBoxName = null,
        bool claimExistingFreshBox = false, CancellationToken cancellationToken = default);
    Task<IsolationLaunchResult> LaunchAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<bool> StopAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IsolationLaunchResult> RestartAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IsolationRuntimeStatus?> GetStatusAsync(Guid profileId, string? workspacePath = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IsolationRuntimeStatus>> StatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Launches the unmodified vendor desktop inside a dedicated per-profile scope.
/// A launch is only trusted once the language server proves it keeps its OAuth
/// token in the profile's own file, never in the Windows user's shared
/// credential entry.
/// </summary>
public sealed class IsolatedProfileLauncher : IProfileIsolation
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultScopeVerificationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The language server announces its credential-store choice with this
    /// phrase. It is the only first-hand evidence that an instance moved off the
    /// Windows user's shared credential entry, so a launch that never prints it
    /// is treated as unisolated.
    /// </summary>
    private const string FileTokenStoreMarker = "Using file-based token storage";

    private readonly HubPaths _paths;
    private readonly IAntigravityDriver _driver;
    private readonly ConfigRepository _repository;
    private readonly Func<IsolationBackendKind, IIsolationBackend?> _backendFactory;
    private readonly TimeSpan _scopeVerificationTimeout;
    private readonly AtomicJsonStore _json;

    public IsolatedProfileLauncher(HubPaths paths, IAntigravityDriver driver, ConfigRepository repository,
        Func<IsolationBackendKind, IIsolationBackend?>? backendFactory = null,
        TimeSpan? scopeVerificationTimeout = null)
    {
        _paths = paths;
        _driver = driver;
        _repository = repository;
        _backendFactory = backendFactory ?? (kind => IsolationBackends.TryCreate(paths, kind));
        _scopeVerificationTimeout = scopeVerificationTimeout ?? DefaultScopeVerificationTimeout;
        _json = new AtomicJsonStore(paths.DataRoot);
    }

    public static string BoxNameFor(Guid profileId) => "AGPH_" + profileId.ToString("N");

    /// <summary>The backend this Profile runs under, recorded when it was created.</summary>
    /// <remarks>
    /// An unregistered Profile falls back to the process-local default rather
    /// than failing: profiles created before the field existed have no value,
    /// and isolation state may legitimately outlive a Profile record. Sandboxie,
    /// the historical default, has been removed, and its recorded value resolves
    /// to the same process-local scope.
    /// </remarks>
    private async Task<IsolationBackendKind> KindAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var document = await _repository.ReadProfilesAsync(cancellationToken).ConfigureAwait(false);
        var registered = document.Profiles.FirstOrDefault(p => p.Id == profileId);
        return registered is null || !Enum.IsDefined(registered.Backend)
            ? IsolationBackendKind.ProcessLocal
            : registered.Backend;
    }

    private IIsolationBackend RequireBackend(IsolationBackendKind kind) =>
        _backendFactory(kind) ?? throw new HubException(
            "The isolation backend could not be created.", "isolation_backend_unavailable");

    public async Task<IsolationBoxRecord?> FindAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.IsolationLock, LockTimeout, cancellationToken).ConfigureAwait(false);
        return (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)).Boxes.FirstOrDefault(b => b.ProfileId == profileId);
    }

    /// <param name="claimExistingFreshBox">
    /// Confirms that an existing, unregistered scope is fresh and may be taken
    /// over. Never use this for a scope that has already completed sign-in.
    /// </param>
    public async Task<IsolationPrepareResult> PrepareAsync(Guid profileId, string? explicitBoxName = null,
        bool claimExistingFreshBox = false, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty) throw new ConfigurationException("Profile ID cannot be empty.");
        var kind = await KindAsync(profileId, cancellationToken).ConfigureAwait(false);
        var backend = RequireBackend(kind);
        var requestedName = string.IsNullOrWhiteSpace(explicitBoxName) ? BoxNameFor(profileId) : explicitBoxName.Trim();
        ValidateBoxName(requestedName);

        await using var held = await FileLock.AcquireAsync(_paths.IsolationLock, LockTimeout, cancellationToken).ConfigureAwait(false);
        var document = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        var record = document.Boxes.FirstOrDefault(b => b.ProfileId == profileId);
        if (record is not null && !string.Equals(record.BoxName, requestedName, StringComparison.OrdinalIgnoreCase) && explicitBoxName is not null)
            throw new HubException($"Profile is already bound to isolation scope '{record.BoxName}'.", "isolation_already_prepared");
        if (record is null && document.Boxes.Any(b => string.Equals(b.BoxName, requestedName, StringComparison.OrdinalIgnoreCase)))
            throw new HubException($"Isolation scope '{requestedName}' is already bound to another profile.", "isolation_box_in_use");

        var boxName = record?.BoxName ?? requestedName;
        var boxExists = backend.BoxExists(boxName);
        var boxCreated = false;
        if (!boxExists)
        {
            backend.CreateBox(boxName);
            boxCreated = true;
        }
        else if (record is null && !claimExistingFreshBox)
        {
            throw new HubException(
                $"Isolation scope '{boxName}' already exists but is not registered to this profile. " +
                "If it has never been used to sign in, retry with --fresh; otherwise choose a new scope.",
                "isolation_box_unowned");
        }

        var mappingCreated = record is null;
        if (record is null)
        {
            record = new IsolationBoxRecord(profileId, boxName, DateTimeOffset.UtcNow);
            document.Boxes.Add(record);
            await WriteUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
        }

        return new IsolationPrepareResult(profileId, boxName, boxCreated, mappingCreated);
    }

    public async Task<IsolationLaunchResult> LaunchAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var launchHeld = await _repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var profileHeld = await _repository.LockProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await LaunchCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IsolationLaunchResult> LaunchCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var kind = await KindAsync(profileId, cancellationToken).ConfigureAwait(false);
        var backend = RequireBackend(kind);
        var record = await FindAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new HubException("This profile has no isolation scope yet. Run iso-prepare first.", "isolation_not_prepared");
        EnsureLaunchReady(backend, record);

        if (HasVendorProcess(backend, record.BoxName))
        {
            var running = backend.ListPids(record.BoxName);
            return new IsolationLaunchResult(profileId, record.BoxName, running, false);
        }

        // Only the part of the log this launch appends to counts; earlier runs
        // prove nothing about this one.
        var storeLogFloor = LogLength(StoreLogPath(profileId));

        // A callback that arrives while this instance runs must reach it, not
        // a freshly started desktop. Writing the waiting Profile here is what
        // the callback verb acts on; the scheme only routes to it after
        // configure-deeplink register.
        await DeepLinkRouter.WritePendingAsync(_paths, profileId, _paths.UserData(profileId), cancellationToken).ConfigureAwait(false);

        var installation = await _driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var plan = _driver.BuildLaunchPlan(installation, profileId, kind);
        backend.LaunchDetached(record.BoxName, plan.ExecutablePath, plan.Arguments, plan.Environment);

        // Process enumeration (and a cold profile's first start) can take well
        // past the old ten-second window, so the wait is deadline-based. A launch
        // that never produces a process is still reported as uncertain, but slow
        // starts are no longer mistaken for one.
        var launchDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < launchDeadline)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            var pids = backend.ListPids(record.BoxName);
            if (pids.Count == 0) continue;
            await RequireFileTokenStoreAsync(profileId, backend, record.BoxName, storeLogFloor, cancellationToken).ConfigureAwait(false);
            return new IsolationLaunchResult(profileId, record.BoxName, pids, true);
        }
        throw new LaunchStateUncertainException("The isolation launcher exited but no process appeared in the expected scope.");
    }

    /// <summary>Where the desktop writes the language server's own log.</summary>
    private string StoreLogPath(Guid profileId) =>
        Path.Combine(_paths.UserData(profileId), "logs", "language_server.log");

    private static long LogLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Fails closed unless the instance proves it moved to its own token file.
    /// </summary>
    /// <remarks>
    /// Nothing else stops an instance from reading the Windows user's shared
    /// credential entry, and a shared entry means the most recent sign-in wins
    /// for every Profile. An unverified instance is stopped rather than left
    /// running next to the others.
    /// </remarks>
    private async Task RequireFileTokenStoreAsync(Guid profileId, IIsolationBackend backend, string scopeName,
        long logFloor, CancellationToken cancellationToken)
    {
        var logPath = StoreLogPath(profileId);
        var deadline = DateTimeOffset.UtcNow + _scopeVerificationTimeout;
        while (true)
        {
            if (await LogContainsAsync(logPath, logFloor, FileTokenStoreMarker, cancellationToken).ConfigureAwait(false)) return;
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        backend.Terminate(scopeName);
        throw new HubException(
            $"Profile {profileId:D} did not switch to its own token file, so it could have used the Windows user's shared credential entry. " +
            $"The instance was stopped. Expected '{FileTokenStoreMarker}' in {logPath}.",
            "isolation_scope_unverified");
    }

    private static async Task<bool> LogContainsAsync(string path, long floor, string marker, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The language server rewrites its log on every start, so a log that
            // is shorter than the floor is a fresh file and the floor is stale.
            if (stream.Length < floor) floor = 0;
            if (stream.Length <= floor) return false;
            stream.Seek(floor, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return text.Contains(marker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True only when a vendor application process is alive in the scope.</summary>
    private static bool HasVendorProcess(IIsolationBackend backend, string boxName)
    {
        foreach (var pid in backend.ListPids(boxName))
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                var name = process.ProcessName;
                if (name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("language_server", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The pid vanished between listing and inspection; treat as idle.
            }
        }
        return false;
    }

    public async Task<bool> StopAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var launchHeld = await _repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var profileHeld = await _repository.LockProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> StopCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var backend = RequireBackend(await KindAsync(profileId, cancellationToken).ConfigureAwait(false));
        var record = await FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        if (!backend.BoxExists(record.BoxName))
            throw new HubException($"Isolation scope '{record.BoxName}' is missing; refusing to act on another scope.", "isolation_box_missing");
        if (backend.ListPids(record.BoxName).Count == 0) return false;
        backend.Terminate(record.BoxName);
        // A stopped Profile cannot receive a callback, so stop advertising it.
        DeepLinkRouter.ClearPending(_paths);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (backend.ListPids(record.BoxName).Count == 0) return true;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new HubException("The isolation scope still contains processes after termination.", "isolation_stop_failed");
    }

    public async Task<IsolationLaunchResult> RestartAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var launchHeld = await _repository.LockLaunchAsync(cancellationToken).ConfigureAwait(false);
        await using var profileHeld = await _repository.LockProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await LaunchCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IsolationRuntimeStatus?> GetStatusAsync(Guid profileId, string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        var record = await FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var backend = _backendFactory(await KindAsync(profileId, cancellationToken).ConfigureAwait(false));
        if (backend is null)
            return new IsolationRuntimeStatus(profileId, record.BoxName, false, false, 0);
        var exists = backend.BoxExists(record.BoxName);
        if (!exists)
            return new IsolationRuntimeStatus(profileId, record.BoxName, true, false, 0);
        return new IsolationRuntimeStatus(profileId, record.BoxName, true, true, backend.ListPids(record.BoxName).Count);
    }

    public async Task<IReadOnlyList<IsolationRuntimeStatus>> StatusAsync(CancellationToken cancellationToken = default)
    {
        IsolationBoxRecord[] records;
        await using (var held = await FileLock.AcquireAsync(_paths.IsolationLock, LockTimeout, cancellationToken).ConfigureAwait(false))
            records = [.. (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)).Boxes];
        var result = new List<IsolationRuntimeStatus>(records.Length);
        foreach (var record in records)
        {
            var status = await GetStatusAsync(record.ProfileId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status is not null) result.Add(status);
        }
        return result;
    }

    private async Task<IsolationStateDocument> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        var document = await _json.ReadAsync(_paths.IsolationStateFile, HubJsonContext.Default.IsolationStateDocument,
            () => new IsolationStateDocument(), cancellationToken).ConfigureAwait(false);
        Validate(document);
        return document;
    }

    private Task WriteUnlockedAsync(IsolationStateDocument document, CancellationToken cancellationToken)
    {
        Validate(document);
        return _json.WriteAsync(_paths.IsolationStateFile, document, HubJsonContext.Default.IsolationStateDocument, cancellationToken);
    }

    private static void Validate(IsolationStateDocument document)
    {
        if (document.SchemaVersion != 1 || document.Boxes is null)
            throw new ConfigurationException("Isolation state has an unsupported schema.", "isolation_state_invalid");
        if (document.Boxes.Any(box => box is null || box.ProfileId == Guid.Empty || box.CreatedUtc == default))
            throw new ConfigurationException("Isolation state contains an invalid mapping.", "isolation_state_invalid");
        foreach (var box in document.Boxes) ValidateBoxName(box.BoxName);
        if (document.Boxes.GroupBy(box => box.ProfileId).Any(group => group.Count() > 1) ||
            document.Boxes.GroupBy(box => box.BoxName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ConfigurationException("Isolation state contains duplicate profile or scope mappings.", "isolation_state_invalid");
    }

    private static void ValidateBoxName(string boxName)
    {
        if (boxName.Length is < 1 or > 64 || boxName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new ConfigurationException("Isolation scope names may contain only 1-64 ASCII letters, digits, or underscores.", "isolation_box_name_invalid");
    }

    private static void EnsureLaunchReady(IIsolationBackend backend, IsolationBoxRecord record)
    {
        if (!backend.BoxExists(record.BoxName))
            throw new HubException($"Isolation scope '{record.BoxName}' is missing; rerun iso-prepare before launching.", "isolation_box_missing");
    }
}
