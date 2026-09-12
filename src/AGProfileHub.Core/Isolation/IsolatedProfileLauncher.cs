namespace AGProfileHub.Core;

public sealed record IsolationBoxRecord(Guid ProfileId, string BoxName, DateTimeOffset CreatedUtc, bool KeyringClaimed);

public sealed record IsolationStateDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<IsolationBoxRecord> Boxes { get; init; } = [];
}

public sealed record IsolationPrepareResult(Guid ProfileId, string BoxName, bool BoxCreated, bool MappingCreated,
    bool KeyringClaimed, int ShimExitCode);
public sealed record IsolationLaunchResult(Guid ProfileId, string BoxName, IReadOnlyList<int> BoxPids, bool StartedNewProcess);
public sealed record IsolationRuntimeStatus(Guid ProfileId, string BoxName, bool BackendAvailable, bool BoxExists,
    bool KeyringClaimed, int ProcessCount, bool WriteThroughReady, IReadOnlyList<string> MissingWriteThroughPaths);

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
    IReadOnlyList<string> WriteThroughPaths(Guid profileId, string? workspacePath = null);
}

/// <summary>
/// Launches the unmodified vendor desktop inside a dedicated per-profile box.
/// Each box first claims the vendor keyring name, preventing credential reads
/// from falling through to the Windows user's shared host entry.
/// </summary>
public sealed class IsolatedProfileLauncher : IProfileIsolation
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultScopeVerificationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The language server announces its credential-store choice with this
    /// phrase. It is the only first-hand evidence that an instance moved off the
    /// Windows user's shared credential entry, so a process-local launch that
    /// never prints it is treated as unisolated.
    /// </summary>
    private const string FileTokenStoreMarker = "Using file-based token storage";

    private readonly HubPaths _paths;
    private readonly IAntigravityDriver _driver;
    private readonly ConfigRepository _repository;
    private readonly Func<IsolationBackendKind, IIsolationBackend?> _backendFactory;
    private readonly Func<(string Executable, IReadOnlyList<string> Arguments)> _shimCommandFactory;
    private readonly TimeSpan _scopeVerificationTimeout;
    private readonly AtomicJsonStore _json;

    public IsolatedProfileLauncher(HubPaths paths, IAntigravityDriver driver, ConfigRepository repository,
        Func<IsolationBackendKind, IIsolationBackend?>? backendFactory = null,
        Func<(string Executable, IReadOnlyList<string> Arguments)>? shimCommandFactory = null,
        TimeSpan? scopeVerificationTimeout = null)
    {
        _paths = paths;
        _driver = driver;
        _repository = repository;
        _backendFactory = backendFactory ?? (kind => IsolationBackends.TryCreate(paths, kind));
        _shimCommandFactory = shimCommandFactory ?? FindShimCommand;
        _scopeVerificationTimeout = scopeVerificationTimeout ?? DefaultScopeVerificationTimeout;
        _json = new AtomicJsonStore(paths.DataRoot);
    }

    // Profile UUIDs remain the default identity. Existing manually accepted
    // boxes can still be attached explicitly after a fresh-box acknowledgement.
    public static string BoxNameFor(Guid profileId) => "AGPH_" + profileId.ToString("N");

    /// <summary>The backend this Profile runs under, recorded when it was created.</summary>
    /// <remarks>
    /// An unregistered Profile falls back to the historical default rather than
    /// failing: profiles created before the backend existed have no field, and
    /// isolation state may legitimately outlive a Profile record. A live Profile
    /// always reaches this method already resolved by the service layer.
    /// </remarks>
    private async Task<IsolationBackendKind> KindAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var document = await _repository.ReadProfilesAsync(cancellationToken).ConfigureAwait(false);
        var registered = document.Profiles.FirstOrDefault(p => p.Id == profileId);
        return registered is null || !Enum.IsDefined(registered.Backend)
            ? IsolationBackendKind.Sandboxie
            : registered.Backend;
    }

    private IIsolationBackend RequireBackend(IsolationBackendKind kind) =>
        _backendFactory(kind) ?? throw new HubException(
            kind == IsolationBackendKind.Sandboxie
                ? "Sandboxie Plus was not found. Isolation-scoped launch requires it."
                : "The process-local isolation backend could not be created.",
            kind == IsolationBackendKind.Sandboxie ? "sandbox_not_found" : "isolation_backend_unavailable");

    public async Task<IsolationBoxRecord?> FindAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var held = await FileLock.AcquireAsync(_paths.IsolationLock, LockTimeout, cancellationToken).ConfigureAwait(false);
        return (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)).Boxes.FirstOrDefault(b => b.ProfileId == profileId);
    }

    /// <param name="claimExistingFreshBox">
    /// Confirms that an existing, unregistered box is fresh and may have its
    /// same-named credential entry overwritten with the invalid placeholder.
    /// Never use this for a box that has already completed sign-in.
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
            throw new HubException($"Profile is already bound to isolation box '{record.BoxName}'.", "isolation_already_prepared");
        if (record is null && document.Boxes.Any(b => string.Equals(b.BoxName, requestedName, StringComparison.OrdinalIgnoreCase)))
            throw new HubException($"Isolation box '{requestedName}' is already bound to another profile.", "isolation_box_in_use");

        var boxName = record?.BoxName ?? requestedName;
        var boxExists = backend.BoxExists(boxName);
        var boxCreated = false;
        if (!boxExists)
        {
            try
            {
                backend.CreateBox(boxName);
                boxCreated = true;
            }
            catch (HubException ex) when (kind == IsolationBackendKind.Sandboxie)
            {
                throw new HubException(
                    $"{ex.Message} Sandboxie stores configuration system-wide, so box setup needs elevation once. " +
                    $"Run the packaged scripts\\New-IsolationBox.ps1 for box {boxName}, then retry iso-prepare with --fresh.",
                    ex.Code, ex);
            }
        }
        else if (record is null && !claimExistingFreshBox)
        {
            throw new HubException(
                $"Isolation box '{boxName}' already exists but is not registered to this profile. " +
                "If it has never been used to sign in, retry with --fresh; otherwise choose a new box.",
                "isolation_box_unowned");
        }

        var mappingCreated = record is null;
        if (record is null)
        {
            record = new IsolationBoxRecord(profileId, boxName, DateTimeOffset.UtcNow, false);
            document.Boxes.Add(record);
            await WriteUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
        }

        var shimExit = 0;
        if (!record.KeyringClaimed)
        {
            // The sandbox backend isolates credentials by making its box own a
            // same-named entry, which the shim claims. The process-local backend
            // claims nothing: the instance keeps its token in its own home, and
            // the launch verifies that before the scope is trusted.
            if (kind == IsolationBackendKind.Sandboxie)
            {
                shimExit = RunKeyringShim(backend, boxName);
                if (shimExit != 11)
                    throw new HubException($"The isolation keyring shim exited with {shimExit}; the box was not marked safe.", "keyring_shim_failed");
            }
            record = record with { KeyringClaimed = true };
            document = document with
            {
                Boxes = [.. document.Boxes.Select(b => b.ProfileId == profileId ? record : b)]
            };
            await WriteUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
        }

        return new IsolationPrepareResult(profileId, boxName, boxCreated, mappingCreated, record.KeyringClaimed, shimExit);
    }

    private int RunKeyringShim(IIsolationBackend backend, string boxName)
    {
        var command = _shimCommandFactory();
        var arguments = command.Arguments.ToList();
        arguments.Add("credential-shim");
        arguments.Add("--force");
        arguments.Add("--fresh");
        // The shim verifies this against the injected Sandboxie module. Passing
        // it lets the shim refuse to touch the vendor target when it landed in
        // some other box than the one this profile owns.
        arguments.Add("--box");
        arguments.Add(boxName);
        return backend.RunInBox(boxName, command.Executable, arguments, wait: true);
    }

    private static (string Executable, IReadOnlyList<string> Arguments) FindShimCommand()
    {
        try { return (DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory), []); }
        catch (ConfigurationException)
        {
            var process = Environment.ProcessPath;
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(process) && !string.IsNullOrEmpty(entry) &&
                Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(entry).Equals("AGProfileHub.Cli", StringComparison.OrdinalIgnoreCase))
                return (process, [entry]);
            throw new HubException("Cannot locate the CLI executable required by the isolation keyring shim.", "shim_path_unknown");
        }
    }

    public IReadOnlyList<string> WriteThroughPaths(Guid profileId, string? workspacePath = null)
    {
        // One rule for the whole profile root instead of five rules for its
        // managed sub-directories. A path move or a new managed child no longer
        // invalidates the configuration. The legacy five-rule form is still
        // honoured as complete by IsWriteThroughCovered below.
        var result = new List<string> { _paths.ProfileRoot(profileId) };
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            try { result.Add(Path.GetFullPath(workspacePath)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
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
        EnsureLaunchReady(kind, backend, record);

        if (HasVendorProcess(backend, record.BoxName))
        {
            var running = backend.ListPids(record.BoxName);
            return new IsolationLaunchResult(profileId, record.BoxName, running, false);
        }

        // Only the part of the log this launch appends to counts; earlier runs
        // prove nothing about this one.
        var storeLogFloor = kind == IsolationBackendKind.ProcessLocal ? LogLength(StoreLogPath(profileId)) : 0;

        if (kind == IsolationBackendKind.ProcessLocal)
        {
            // A callback that arrives while this instance runs must reach it, not
            // a freshly started desktop. Writing the waiting Profile here is what
            // the callback verb acts on; the scheme only routes to it after
            // configure-deeplink register.
            await DeepLinkRouter.WritePendingAsync(_paths, profileId, _paths.UserData(profileId), cancellationToken).ConfigureAwait(false);
        }

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
            if (kind == IsolationBackendKind.ProcessLocal)
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
    /// Without a sandbox nothing else stops an instance from reading the Windows
    /// user's shared credential entry, and a shared entry means the most recent
    /// sign-in wins for every Profile. An unverified instance is stopped rather
    /// than left running next to the others.
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

    /// <summary>
    /// True only when a vendor application process is alive inside the box.
    /// Sandboxie keeps a helper process behind even after /terminate, so the
    /// raw pid count must never be used as a "still running" signal.
    /// </summary>
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
            throw new HubException($"Isolation box '{record.BoxName}' is missing; refusing to act on another scope.", "isolation_box_missing");
        if (backend.ListPids(record.BoxName).Count == 0) return false;
        backend.Terminate(record.BoxName);
        // A stopped Profile cannot receive a callback, so stop advertising it.
        DeepLinkRouter.ClearPending(_paths);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (backend.ListPids(record.BoxName).Count == 0) return true;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new HubException("The isolation box still contains processes after termination.", "sandbox_stop_failed");
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
        var expected = WriteThroughPaths(profileId, workspacePath);
        var backend = _backendFactory(await KindAsync(profileId, cancellationToken).ConfigureAwait(false));
        if (backend is null)
            return new IsolationRuntimeStatus(profileId, record.BoxName, false, false, record.KeyringClaimed, 0, false, expected);
        var exists = backend.BoxExists(record.BoxName);
        if (!exists)
            return new IsolationRuntimeStatus(profileId, record.BoxName, true, false, record.KeyringClaimed, 0, false, expected);
        var configured = backend.GetIndexedSettingValues(record.BoxName, "OpenFilePath");
        var missing = expected.Where(path => !IsWriteThroughCovered(path, profileId, configured)).ToList();
        return new IsolationRuntimeStatus(profileId, record.BoxName, true, true, record.KeyringClaimed,
            backend.ListPids(record.BoxName).Count, missing.Count == 0, missing);
    }

    /// <summary>
    /// The profile root is covered either by its own rule or by the legacy
    /// five-rule form that opened every managed sub-directory individually.
    /// Workspace paths must match exactly.
    /// </summary>
    private bool IsWriteThroughCovered(string expected, Guid profileId, IReadOnlyList<string> configured)
    {
        if (configured.Any(value => PathEquals(value, expected))) return true;
        if (!PathEquals(expected, _paths.ProfileRoot(profileId))) return false;
        var legacy = new[]
        {
            _paths.Home(profileId),
            _paths.Roaming(profileId),
            _paths.Local(profileId),
            _paths.Temp(profileId),
            _paths.UserData(profileId)
        };
        return legacy.All(child => configured.Any(value => PathEquals(value, child)));
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
            throw new ConfigurationException("Isolation state contains duplicate profile or box mappings.", "isolation_state_invalid");
    }

    private static void ValidateBoxName(string boxName)
    {
        if (boxName.Length is < 1 or > 64 || boxName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new ConfigurationException("Isolation box names may contain only 1-64 ASCII letters, digits, or underscores.", "isolation_box_name_invalid");
    }

    private static void EnsureLaunchReady(IsolationBackendKind kind, IIsolationBackend backend, IsolationBoxRecord record)
    {
        if (!record.KeyringClaimed)
            throw new HubException(
                kind == IsolationBackendKind.Sandboxie
                    ? "The isolation box has not claimed its keyring entry; refusing an unsafe launch."
                    : "The isolation scope has not been prepared; refusing an unsafe launch.",
                "keyring_not_claimed");
        if (!backend.BoxExists(record.BoxName))
            throw new HubException($"Isolation box '{record.BoxName}' is missing; rerun iso-prepare before launching.", "isolation_box_missing");
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
