using System.Text.Json;
using System.Text.Json.Serialization;

namespace AGProfileHub.Core;

public sealed record SharedClientCapabilities(bool Supported, string Reason, string? ClientVersion = null);
public sealed record SharedRunningTask(string Id, string ConversationId, string Kind);
public sealed record SharedConversationContext(string ProjectId, string ConversationId, string Draft, string ViewLocation);
public sealed record SharedClientContext(Guid ProfileId, string WindowId, string? ActiveConversationId,
    IReadOnlyList<SharedConversationContext> Conversations);
public sealed record SharedAccountVerification(Guid? AuthenticatedProfileId, bool IdentityConfirmed,
    SharedClientContext? Context, bool ModelAvailable);

/// <summary>
/// A verified client adapter must address one official window, pause all new submissions without aborting
/// existing work, enumerate all work in its group, and verify identity from the live authenticated service.
/// A saved profile ID or successful login command alone is insufficient identity verification.
/// The Hub passes references to official service environments only. Adapters must not read or write
/// tokens, cookies or passwords, retrieve account email, or start a browser login. Authentication
/// remains inside the official client. Neither switching nor verification may send model requests.
/// </summary>
public interface ISharedAccountClient
{
    Task<SharedClientCapabilities> GetCapabilitiesAsync(Guid groupId, CancellationToken cancellationToken = default);
    Task SetNewTasksPausedAsync(Guid groupId, bool paused, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedRunningTask>> ReadRunningTasksAsync(Guid groupId, CancellationToken cancellationToken = default);
    /// <summary>Captures live context whose profile attribution is confirmed by the official service, never by a manual label.</summary>
    Task<SharedClientContext> CaptureContextAsync(Guid groupId, CancellationToken cancellationToken = default);
    /// <summary>Hands a Profile reference to the official client; never imports or replaces authentication material.</summary>
    Task SwitchIdentityAsync(Guid groupId, Guid targetProfileId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Reattaches the still-usable original official service environment, using only its captured references.
    /// Returns false if it is unavailable or would require reading/writing credentials or another login.
    /// A true result is not identity or context verification; the coordinator verifies both afterwards.
    /// </summary>
    Task<bool> TryRestoreOriginalServiceAsync(Guid groupId, SharedClientContext originalContext,
        CancellationToken cancellationToken = default);
    Task RestoreContextAsync(Guid groupId, SharedClientContext context, CancellationToken cancellationToken = default);
    Task<SharedAccountVerification> VerifyAsync(Guid groupId, Guid expectedProfileId,
        SharedClientContext expectedContext, CancellationToken cancellationToken = default);
}

public enum AccountSwitchStatus { Switching, Ready, RecoveryRequired }
public enum AccountSwitchPhase
{
    WaitingForTasks, CapturingContext, SwitchingIdentity, RestoringContext, Verifying,
    Completed, Cancelled, RolledBack, RecoveryRequired
}

/// <summary>A safe display projection; drafts, view state and credentials are never returned here.</summary>
public sealed record AccountSwitchState(Guid GroupId, Guid OperationId, AccountSwitchStatus State,
    AccountSwitchPhase Phase, bool IsAttached, bool SendingBlocked, Guid? ActiveProfileId,
    Guid? TargetProfileId, string? LastErrorCode, DateTimeOffset UpdatedAtUtc);

public sealed record AccountSwitchResult(bool Succeeded, string Code, AccountSwitchState? State);

/// <summary>Coordinates a single group's switch under the same cross-process lock used by imports.</summary>
public sealed class AccountSwitchCoordinator
{
    private readonly HubPaths _paths;
    private readonly ISharedAccountClient _client;
    private readonly TimeSpan _pollInterval;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AccountSwitchCoordinator(HubPaths paths, ISharedAccountClient? client = null, TimeSpan? pollInterval = null)
    {
        _paths = paths;
        _client = client ?? new UnsupportedSharedAccountClient();
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public Task<SharedClientCapabilities> GetCapabilitiesAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        ValidateId(groupId);
        return _client.GetCapabilitiesAsync(groupId, cancellationToken);
    }

    public Task<AccountSwitchState?> ReadStateAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadRuntime(groupId)?.Projection());
    }

    public async Task<AccountSwitchResult> ExecuteAsync(Guid groupId, Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(groupId); ValidateId(targetProfileId);
        var capability = await GetCapabilitiesAsync(groupId, cancellationToken);
        if (!capability.Supported) return new(false, "shared_client_unsupported", null);
        using var lease = Acquire(groupId);
        var previous = ReadRuntime(groupId);
        if (previous is { State: not AccountSwitchStatus.Ready } || previous?.SendingBlocked == true)
            throw new HubException("The previous account switch needs recovery before another switch.", "shared_switch_recovery_required");
        var runtime = new Runtime
        {
            GroupId = groupId, OperationId = Guid.NewGuid(), State = AccountSwitchStatus.Switching,
            Phase = AccountSwitchPhase.WaitingForTasks, IsAttached = true, SendingBlocked = true,
            ActiveProfileId = previous?.ActiveProfileId, TargetProfileId = targetProfileId
        };
        // Write before the first client change. A process exit must leave an explicit unfinished operation.
        WriteRuntime(runtime);
        try
        {
            runtime.PauseRequested = true;
            WriteRuntime(runtime);
            await _client.SetNewTasksPausedAsync(groupId, true, cancellationToken);
            while ((await _client.ReadRunningTasksAsync(groupId, cancellationToken)).Count != 0)
                await Task.Delay(_pollInterval, cancellationToken);
            SetPhase(runtime, AccountSwitchPhase.CapturingContext);
            var originalContext = await _client.CaptureContextAsync(groupId, cancellationToken);
            ValidateContext(originalContext);
            runtime.OriginalContext = originalContext;
            runtime.ActiveProfileId = runtime.OriginalContext.ProfileId;
            WriteRuntime(runtime);
            cancellationToken.ThrowIfCancellationRequested();
            runtime.IdentityMutationStarted = true;
            SetPhase(runtime, AccountSwitchPhase.SwitchingIdentity);
            await _client.SwitchIdentityAsync(groupId, targetProfileId, cancellationToken);
            SetPhase(runtime, AccountSwitchPhase.RestoringContext);
            await _client.RestoreContextAsync(groupId, runtime.OriginalContext, cancellationToken);
            SetPhase(runtime, AccountSwitchPhase.Verifying);
            var verified = await _client.VerifyAsync(groupId, targetProfileId, runtime.OriginalContext, cancellationToken);
            if (!Verified(verified, targetProfileId, runtime.OriginalContext))
                throw new HubException("The target identity, model permission or original window context could not be confirmed.", "shared_switch_verification_failed");
            cancellationToken.ThrowIfCancellationRequested();
            runtime.ActiveProfileId = targetProfileId;
            runtime.State = AccountSwitchStatus.Ready;
            runtime.Phase = AccountSwitchPhase.Completed;
            runtime.SendingBlocked = false;
            runtime.LastErrorCode = null;
            WriteRuntime(runtime);
            // Persist the verified identity before admitting work. A failure to resume can be ambiguous:
            // do not switch identity again until an explicit recovery has paused and drained new work.
            try { await _client.SetNewTasksPausedAsync(groupId, false, CancellationToken.None); }
            catch { return await BlockForRecoveryAsync(runtime, "shared_switch_resume_failed"); }
            return new(true, "shared_switch_completed", runtime.Projection());
        }
        catch (Exception error)
        {
            var code = error is OperationCanceledException ? "shared_switch_cancelled" :
                error is HubException hub ? hub.Code : "shared_switch_failed";
            return await CompensateAsync(runtime, code, cancelled: error is OperationCanceledException);
        }
    }

    /// <summary>Explicitly recovers an interrupted operation; never treats saved state as live verification.</summary>
    public async Task<AccountSwitchResult> RecoverAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var capability = await GetCapabilitiesAsync(groupId, cancellationToken);
        if (!capability.Supported) return new(false, "shared_client_unsupported", null);
        using var lease = Acquire(groupId);
        var runtime = ReadRuntime(groupId);
        if (runtime is null) return new(false, "shared_switch_no_runtime", null);
        if (runtime.State == AccountSwitchStatus.Ready && !runtime.SendingBlocked)
            return new(false, "shared_switch_recovery_unnecessary", runtime.Projection());
        cancellationToken.ThrowIfCancellationRequested();
        return await CompensateAsync(runtime, "shared_switch_interrupted", cancelled: false);
    }

    private async Task<AccountSwitchResult> CompensateAsync(Runtime runtime, string originalCode, bool cancelled)
    {
        using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // A cancelled caller must not cancel compensation after an identity may already have changed.
            var token = compensation.Token;
            await _client.SetNewTasksPausedAsync(runtime.GroupId, true, token);
            if (runtime.IdentityMutationStarted)
            {
                while ((await _client.ReadRunningTasksAsync(runtime.GroupId, token)).Count != 0)
                    await Task.Delay(_pollInterval, token);
                if (runtime.OriginalContext is null) throw new InvalidOperationException("The original context is unavailable.");
                if (!await _client.TryRestoreOriginalServiceAsync(runtime.GroupId, runtime.OriginalContext, token))
                    return await BlockForRecoveryAsync(runtime, "shared_switch_original_service_unavailable");
                await _client.RestoreContextAsync(runtime.GroupId, runtime.OriginalContext, token);
                var verified = await _client.VerifyAsync(runtime.GroupId, runtime.OriginalContext.ProfileId,
                    runtime.OriginalContext, token);
                if (!Verified(verified, runtime.OriginalContext.ProfileId, runtime.OriginalContext))
                    throw new InvalidOperationException("The original identity and context could not be restored.");
            }
            runtime.State = AccountSwitchStatus.Ready;
            runtime.Phase = cancelled && !runtime.IdentityMutationStarted ? AccountSwitchPhase.Cancelled : AccountSwitchPhase.RolledBack;
            runtime.SendingBlocked = false;
            runtime.ActiveProfileId = runtime.OriginalContext?.ProfileId ?? runtime.ActiveProfileId;
            runtime.LastErrorCode = originalCode;
            WriteRuntime(runtime);
            try { await _client.SetNewTasksPausedAsync(runtime.GroupId, false, token); }
            catch { return await BlockForRecoveryAsync(runtime, "shared_switch_resume_failed"); }
            return new(false, originalCode, runtime.Projection());
        }
        catch
        {
            return await BlockForRecoveryAsync(runtime, "shared_switch_recovery_failed");
        }
    }

    private async Task<AccountSwitchResult> BlockForRecoveryAsync(Runtime runtime, string code)
    {
        runtime.State = AccountSwitchStatus.RecoveryRequired;
        runtime.Phase = AccountSwitchPhase.RecoveryRequired;
        runtime.SendingBlocked = true;
        runtime.ActiveProfileId = null;
        runtime.LastErrorCode = code;
        WriteRuntime(runtime);
        using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _client.SetNewTasksPausedAsync(runtime.GroupId, true, pauseTimeout.Token); } catch { }
        return new(false, code, runtime.Projection());
    }

    private static bool Verified(SharedAccountVerification verification, Guid profileId, SharedClientContext expected) =>
        verification.IdentityConfirmed && verification.AuthenticatedProfileId == profileId && verification.ModelAvailable &&
        verification.Context is { } actual && actual.ProfileId == profileId &&
        actual.WindowId == expected.WindowId && actual.ActiveConversationId == expected.ActiveConversationId &&
        actual.Conversations.Count == expected.Conversations.Count &&
        actual.Conversations.OrderBy(item => item.ConversationId, StringComparer.Ordinal)
            .SequenceEqual(expected.Conversations.OrderBy(item => item.ConversationId, StringComparer.Ordinal));

    private static void ValidateContext(SharedClientContext context)
    {
        if (context.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(context.WindowId) ||
            context.Conversations is null || context.Conversations.Any(item => item is null || string.IsNullOrWhiteSpace(item.ProjectId) ||
                string.IsNullOrWhiteSpace(item.ConversationId)) ||
            context.Conversations.Select(item => item.ConversationId).Distinct(StringComparer.Ordinal).Count() != context.Conversations.Count ||
            context.ActiveConversationId is not null && !context.Conversations.Any(item => item.ConversationId == context.ActiveConversationId))
            throw new HubException("The original client context is incomplete.", "shared_context_invalid");
    }

    private FileStream Acquire(Guid groupId)
    {
        var directory = GroupDirectory(groupId);
        PathSafety.EnsureSafeDirectory(_paths.DataRoot, directory);
        var file = Path.Combine(directory, "group.lock");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, file);
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new HubException("This shared group is already being changed by another operation.", "shared_group_busy"); }
    }

    private string GroupDirectory(Guid groupId)
    {
        ValidateId(groupId);
        return Path.Combine(_paths.DataRoot, "shared-groups", groupId.ToString("D"));
    }

    private Runtime? ReadRuntime(Guid groupId)
    {
        PathSafety.RejectExistingPathAncestors(_paths.DataRoot);
        var path = Path.Combine(GroupDirectory(groupId), "runtime.json");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, path);
        if (!File.Exists(path)) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var value = JsonSerializer.Deserialize<Runtime>(input, Json);
            if (value is null || value.SchemaVersion != 1 || value.GroupId != groupId || value.OperationId == Guid.Empty ||
                !Enum.IsDefined(value.State) || !Enum.IsDefined(value.Phase) ||
                value.State != AccountSwitchStatus.Ready && !value.SendingBlocked ||
                value.IdentityMutationStarted && value.OriginalContext is null)
                throw new JsonException();
            if (value.OriginalContext is not null) ValidateContext(value.OriginalContext);
            return value;
        }
        catch (Exception error) when (error is JsonException or HubException or NotSupportedException)
        {
            throw new HubException("The shared account runtime is invalid; it has not been overwritten.", "shared_switch_runtime_invalid");
        }
    }

    private void SetPhase(Runtime runtime, AccountSwitchPhase phase)
    {
        runtime.Phase = phase;
        WriteRuntime(runtime);
    }

    private void WriteRuntime(Runtime runtime)
    {
        runtime.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var directory = GroupDirectory(runtime.GroupId);
        PathSafety.EnsureSafeDirectory(_paths.DataRoot, directory);
        var file = Path.Combine(directory, "runtime.json");
        PathSafety.RejectExistingReparsePoints(_paths.DataRoot, file);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, runtime, Json);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ConfigurationException("The shared group and account IDs cannot be empty.");
    }

    private sealed class Runtime
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid GroupId { get; set; }
        public Guid OperationId { get; set; }
        public AccountSwitchStatus State { get; set; }
        public AccountSwitchPhase Phase { get; set; }
        public bool IsAttached { get; set; }
        public bool SendingBlocked { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public Guid? TargetProfileId { get; set; }
        public SharedClientContext? OriginalContext { get; set; }
        public bool PauseRequested { get; set; }
        public bool IdentityMutationStarted { get; set; }
        public string? LastErrorCode { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public AccountSwitchState Projection() => new(GroupId, OperationId, State, Phase, IsAttached,
            SendingBlocked, ActiveProfileId, TargetProfileId, LastErrorCode, UpdatedAtUtc);
    }

    private sealed class UnsupportedSharedAccountClient : ISharedAccountClient
    {
        public Task<SharedClientCapabilities> GetCapabilitiesAsync(Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedClientCapabilities(false,
                "The installed Antigravity client has not been verified for same-window identity switching and full conversation restoration."));
        private static UnsupportedCapabilityException Unsupported() => new("No verified Antigravity account-switch adapter is enabled.");
        public Task SetNewTasksPausedAsync(Guid groupId, bool paused, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task<IReadOnlyList<SharedRunningTask>> ReadRunningTasksAsync(Guid groupId, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task<SharedClientContext> CaptureContextAsync(Guid groupId, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task SwitchIdentityAsync(Guid groupId, Guid targetProfileId, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task<bool> TryRestoreOriginalServiceAsync(Guid groupId, SharedClientContext originalContext,
            CancellationToken cancellationToken = default) => throw Unsupported();
        public Task RestoreContextAsync(Guid groupId, SharedClientContext context, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task<SharedAccountVerification> VerifyAsync(Guid groupId, Guid expectedProfileId, SharedClientContext expectedContext,
            CancellationToken cancellationToken = default) => throw Unsupported();
    }
}
