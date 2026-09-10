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
    private readonly HubPaths _paths;
    private readonly IAntigravityDriver _driver;
    private readonly ConfigRepository _repository;
    private readonly Func<IIsolationBackend?> _backendFactory;
    private readonly Func<(string Executable, IReadOnlyList<string> Arguments)> _shimCommandFactory;
    private readonly AtomicJsonStore _json;

    public IsolatedProfileLauncher(HubPaths paths, IAntigravityDriver driver, ConfigRepository repository,
        Func<IIsolationBackend?>? backendFactory = null,
        Func<(string Executable, IReadOnlyList<string> Arguments)>? shimCommandFactory = null)
    {
        _paths = paths;
        _driver = driver;
        _repository = repository;
        _backendFactory = backendFactory ?? (() => SandboxieBackend.TryDiscover());
        _shimCommandFactory = shimCommandFactory ?? FindShimCommand;
        _json = new AtomicJsonStore(paths.DataRoot);
    }

    // Profile UUIDs remain the default identity. Existing manually accepted
    // boxes can still be attached explicitly after a fresh-box acknowledgement.
    public static string BoxNameFor(Guid profileId) => "AGPH_" + profileId.ToString("N");

    private IIsolationBackend RequireBackend() =>
        _backendFactory()
        ?? throw new HubException("Sandboxie Plus was not found. Isolation-scoped launch requires it.", "sandbox_not_found");

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
        var backend = RequireBackend();
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
            catch (HubException ex)
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
            shimExit = RunKeyringShim(backend, boxName);
            if (shimExit != 11)
                throw new HubException($"The isolation keyring shim exited with {shimExit}; the box was not marked safe.", "keyring_shim_failed");
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
        var backend = RequireBackend();
        var record = await FindAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new HubException("This profile has no isolation box yet. Run iso-prepare first.", "isolation_not_prepared");
        EnsureLaunchReady(backend, record);

        if (HasVendorProcess(backend, record.BoxName))
        {
            var running = backend.ListPids(record.BoxName);
            return new IsolationLaunchResult(profileId, record.BoxName, running, false);
        }

        var installation = await _driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var plan = _driver.BuildLaunchPlan(installation, profileId);
        backend.LaunchDetached(record.BoxName, plan.ExecutablePath, plan.Arguments, plan.Environment);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            var pids = backend.ListPids(record.BoxName);
            if (pids.Count > 0) return new IsolationLaunchResult(profileId, record.BoxName, pids, true);
        }
        throw new LaunchStateUncertainException("The isolation launcher exited but no process appeared in the expected box.");
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
        var backend = RequireBackend();
        var record = await FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        if (!backend.BoxExists(record.BoxName))
            throw new HubException($"Isolation box '{record.BoxName}' is missing; refusing to act on another scope.", "isolation_box_missing");
        if (backend.ListPids(record.BoxName).Count == 0) return false;
        backend.Terminate(record.BoxName);
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
        var backend = _backendFactory();
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

    private static void EnsureLaunchReady(IIsolationBackend backend, IsolationBoxRecord record)
    {
        if (!record.KeyringClaimed)
            throw new HubException("The isolation box has not claimed its keyring entry; refusing an unsafe launch.", "keyring_not_claimed");
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
