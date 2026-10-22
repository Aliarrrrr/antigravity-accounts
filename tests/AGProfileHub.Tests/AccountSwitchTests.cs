using System.Text.Json;
using AGProfileHub.Core;

internal static class AccountSwitchTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Shared account client is unsupported until a real adapter is verified", UnsupportedAsync),
        ("Shared account switch retains every conversation and the original window across A-B-A", RoundTripAsync),
        ("Shared account switch waits for all tasks and cancellation preserves their execution", WaitingCancellationAsync),
        ("Shared account switches use one group lock across coordinator instances", DuplicateAsync),
        ("Shared account switch rejects unconfirmed identity, changed context and missing model access", VerificationAsync),
        ("Shared account switch cancellation reattaches an available original official service", PostMutationCancellationAsync),
        ("Shared account unavailable original service never triggers a second identity switch", UnavailableOriginalServiceAsync),
        ("Shared account client failures recover only through the original official service", ClientFailureAsync),
        ("Shared account final state is saved before new work is admitted", FinalPersistenceAsync),
        ("Shared account ambiguous resume pauses again and waits for new work before explicit recovery", ResumeFailureAsync),
        ("Shared account recovery rechecks original identity, window, conversations, drafts and model access", FailedRecoveryAsync),
        ("Shared account interrupted persisted switch requires explicit recovery", InterruptedAsync),
        ("Shared account state rejects unknown schemas and leaves their content intact", RuntimeSchemaAsync)
    ];

    private static async Task UnsupportedAsync()
    {
        using var directory = new OwnedTestDirectory(prefix: "agprofile-shared-switch-");
        var paths = new HubPaths(Path.Combine(directory.Root, "not-created"), Path.Combine(directory.Root, "global"));
        var coordinator = new AccountSwitchCoordinator(paths);
        var group = Guid.NewGuid();
        var capabilities = await coordinator.GetCapabilitiesAsync(group);
        TestAssert.Check(!capabilities.Supported && capabilities.Reason.Contains("not been verified", StringComparison.Ordinal),
            "The production client claimed a tested switching capability.");
        var result = await coordinator.ExecuteAsync(group, Guid.NewGuid());
        TestAssert.Check(!result.Succeeded && result.Code == "shared_client_unsupported" && !Directory.Exists(paths.DataRoot),
            "An unsupported client wrote runtime state or enabled account switching.");
        TestAssert.Check(typeof(AccountSwitchCoordinator).Assembly.GetType("AGProfileHub.Core.AccountCredentialVault") is null,
            "The production assembly still exposes the removed credential vault.");
    }

    private static async Task RoundTripAsync()
    {
        using var fixture = new SwitchFixture();
        var original = fixture.Client.Context;
        var second = Guid.NewGuid();
        var forward = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, second);
        TestAssert.Check(forward.Succeeded && forward.State is { SendingBlocked: false, IsAttached: true } &&
            forward.State.ActiveProfileId == second && SameContext(fixture.Client.Context, original with { ProfileId = second }),
            "The forward switch did not preserve the window, projects, drafts or view positions.");
        var back = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, original.ProfileId);
        TestAssert.Check(back.Succeeded && SameContext(fixture.Client.Context, original) && !fixture.Client.Paused,
            "The return switch lost the original account or context.");
        var state = await fixture.Coordinator.ReadStateAsync(fixture.GroupId);
        var display = JsonSerializer.Serialize(state);
        TestAssert.Check(!display.Contains("private draft", StringComparison.Ordinal) &&
            !display.Contains("Conversations", StringComparison.Ordinal), "Status exposed a draft or full context.");
        TestAssert.Check(fixture.Client.PauseTransitions.SequenceEqual([true, false, true, false]),
            "New submissions were not paused around both account changes.");
    }

    private static async Task WaitingCancellationAsync()
    {
        using var fixture = new SwitchFixture();
        fixture.Client.Running = [new("one", "conversation-a", "generation"), new("two", "conversation-b", "tool")];
        using var cancel = new CancellationTokenSource();
        var switching = fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid(), cancel.Token);
        await fixture.Client.ReadTasks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TestAssert.Check(fixture.Client.Paused && fixture.Client.SwitchCalls == 0, "The identity changed while work was active.");
        cancel.Cancel();
        var result = await switching;
        TestAssert.Check(!result.Succeeded && result.Code == "shared_switch_cancelled" &&
            result.State?.Phase == AccountSwitchPhase.Cancelled && !fixture.Client.Paused &&
            fixture.Client.Running.Count == 2 && fixture.Client.SwitchCalls == 0,
            "Cancelling a waiting switch aborted work or left submissions paused.");

        fixture.Client.Running = [new("two", "conversation-b", "tool")];
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var afterOne = fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid(), finish.Token);
        await Task.Delay(30);
        TestAssert.Check(fixture.Client.SwitchCalls == 0, "A tool still running in another conversation was ignored.");
        fixture.Client.Running = [];
        TestAssert.Check((await afterOne).Succeeded, "The switch did not resume after all work completed.");
    }

    private static async Task DuplicateAsync()
    {
        using var fixture = new SwitchFixture();
        fixture.Client.Running = [new("one", "conversation-a", "generation")];
        using var cancel = new CancellationTokenSource();
        var first = fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid(), cancel.Token);
        await fixture.Client.ReadTasks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = new AccountSwitchCoordinator(fixture.Paths, fixture.Client, TimeSpan.FromMilliseconds(5));
        await TestAssert.ThrowsCodeAsync<HubException>(() => second.ExecuteAsync(fixture.GroupId, Guid.NewGuid()), "shared_group_busy");
        cancel.Cancel(); await first;
        TestAssert.Check(fixture.Client.SwitchCalls == 0 && fixture.Client.ServiceRestoreCalls == 0,
            "A duplicate or pre-switch cancellation reached a client service change.");
    }

    private static async Task VerificationAsync()
    {
        foreach (var failure in new[] { "identity", "profile", "window", "conversation", "draft", "view", "model" })
        {
            using var fixture = new SwitchFixture();
            var original = fixture.Client.Context;
            var target = Guid.NewGuid();
            fixture.Client.VerificationFailure = profile => profile == target ? failure : null;
            var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target);
            TestAssert.Check(!result.Succeeded && result.Code == "shared_switch_verification_failed" &&
                result.State?.Phase == AccountSwitchPhase.RolledBack && SameContext(fixture.Client.Context, original) && !fixture.Client.Paused &&
                fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
                $"A {failure} verification failure did not restore the original identity and full context.");
        }
    }

    private static async Task PostMutationCancellationAsync()
    {
        using var fixture = new SwitchFixture();
        var original = fixture.Client.Context;
        using var cancel = new CancellationTokenSource();
        var target = Guid.NewGuid();
        fixture.Client.AfterIdentityChanged = profile => { if (profile == target) cancel.Cancel(); };
        var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target, cancel.Token);
        TestAssert.Check(result.Code == "shared_switch_cancelled" && result.State?.Phase == AccountSwitchPhase.RolledBack &&
            SameContext(fixture.Client.Context, original) && fixture.Client.SwitchCalls == 1 &&
            fixture.Client.ServiceRestoreCalls == 1 && !fixture.Client.Paused,
            "Cancellation did not verify the original service or performed a second identity switch.");
    }

    private static async Task UnavailableOriginalServiceAsync()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var fixture = new SwitchFixture();
            var original = fixture.Client.Context;
            var target = Guid.NewGuid();
            fixture.Client.OriginalServiceAvailable = false;
            using var cancellation = new CancellationTokenSource();
            if (cancelled) fixture.Client.AfterIdentityChanged = _ => cancellation.Cancel();
            else fixture.Client.VerificationFailure = profile => profile == target ? "identity" : null;
            var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target, cancellation.Token);
            TestAssert.Check(!result.Succeeded && result.Code == "shared_switch_original_service_unavailable" &&
                result.State is { State: AccountSwitchStatus.RecoveryRequired, Phase: AccountSwitchPhase.RecoveryRequired,
                    ActiveProfileId: null, SendingBlocked: true } && fixture.Client.Paused &&
                fixture.Client.Context.ProfileId == target && fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
                "An unavailable original service was treated as restored after failure or cancellation.");
            await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid()),
                "shared_switch_recovery_required");
            var retry = await fixture.Coordinator.RecoverAsync(fixture.GroupId);
            TestAssert.Check(retry.Code == "shared_switch_original_service_unavailable" && fixture.Client.SwitchCalls == 1 &&
                retry.State is { ActiveProfileId: null, SendingBlocked: true },
                "Explicit recovery recreated an identity instead of retaining the unavailable service state.");
            fixture.Client.OriginalServiceAvailable = true;
            var restored = await fixture.Coordinator.RecoverAsync(fixture.GroupId);
            TestAssert.Check(restored.State is { State: AccountSwitchStatus.Ready, Phase: AccountSwitchPhase.RolledBack,
                    SendingBlocked: false } && SameContext(fixture.Client.Context, original) &&
                fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 3,
                "The original service was not reverified when it became available again.");
        }
    }

    private static async Task FailedRecoveryAsync()
    {
        foreach (var failure in new[] { "identity", "profile", "window", "conversation", "draft", "view", "model" })
        {
            using var fixture = new SwitchFixture();
            var original = fixture.Client.Context;
            fixture.Client.VerificationFailure = _ => failure;
            var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid());
            TestAssert.Check(result.Code == "shared_switch_recovery_failed" &&
                result.State is { State: AccountSwitchStatus.RecoveryRequired, SendingBlocked: true, ActiveProfileId: null } &&
                fixture.Client.Paused && fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
                $"A {failure} mismatch after service recovery was accepted without live verification.");
            var other = new AccountSwitchCoordinator(fixture.Paths, fixture.Client);
            await TestAssert.ThrowsCodeAsync<HubException>(() => other.ExecuteAsync(fixture.GroupId, Guid.NewGuid()),
                "shared_switch_recovery_required");
            fixture.Client.VerificationFailure = null;
            var recovered = await other.RecoverAsync(fixture.GroupId);
            TestAssert.Check(recovered.State is { State: AccountSwitchStatus.Ready, SendingBlocked: false } &&
                SameContext(fixture.Client.Context, original) && !fixture.Client.Paused && fixture.Client.SwitchCalls == 1,
                "Explicit recovery did not verify the original service and context.");
        }
    }

    private static async Task ClientFailureAsync()
    {
        foreach (var failRestore in new[] { false, true })
        {
            using var fixture = new SwitchFixture();
            var original = fixture.Client.Context;
            var target = Guid.NewGuid();
            if (failRestore) fixture.Client.BeforeRestore = () =>
            {
                if (fixture.Client.Context.ProfileId == target) throw new InvalidOperationException("A test-only service restart or restore failed.");
            };
            else fixture.Client.AfterIdentityChanged = profile =>
            {
                if (profile == target) throw new InvalidOperationException("A test-only official service handoff failure.");
            };
            var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target);
            TestAssert.Check(result.Code == "shared_switch_failed" && result.State?.Phase == AccountSwitchPhase.RolledBack &&
                SameContext(fixture.Client.Context, original) && !fixture.Client.Paused &&
                fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
                "A client failure did not verify the original service, or caused another identity switch.");
        }
    }

    private static async Task FinalPersistenceAsync()
    {
        using var fixture = new SwitchFixture();
        var original = fixture.Client.Context;
        var target = Guid.NewGuid();
        FileStream? lockedState = null;
        fixture.Client.BeforeVerification = profile =>
        {
            if (profile == target) lockedState = new FileStream(fixture.RuntimePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        fixture.Client.BeforeServiceRestore = () => { lockedState?.Dispose(); lockedState = null; };
        try
        {
            var result = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target);
            TestAssert.Check(!result.Succeeded && result.State?.Phase == AccountSwitchPhase.RolledBack &&
                SameContext(fixture.Client.Context, original) && fixture.Client.PauseTransitions.SequenceEqual([true, true, false]),
                "New work was admitted before saving final state, or a final save failure skipped account recovery.");
        }
        finally { lockedState?.Dispose(); }
    }

    private static async Task ResumeFailureAsync()
    {
        using var fixture = new SwitchFixture();
        var original = fixture.Client.Context;
        var target = Guid.NewGuid();
        var checkedPersistedState = false;
        fixture.Client.BeforeResume = () =>
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(fixture.RuntimePath));
            checkedPersistedState = saved.RootElement.GetProperty("State").GetString() == "Ready" &&
                saved.RootElement.GetProperty("ActiveProfileId").GetGuid() == target;
            fixture.Client.Running = [new("newly-admitted", "conversation-a", "generation")];
            throw new InvalidOperationException("The test adapter resumed, then failed before acknowledging it.");
        };
        var failed = await fixture.Coordinator.ExecuteAsync(fixture.GroupId, target);
        TestAssert.Check(checkedPersistedState && failed.Code == "shared_switch_resume_failed" &&
            failed.State is { SendingBlocked: true, State: AccountSwitchStatus.RecoveryRequired } &&
            fixture.Client.SwitchCalls == 1 && fixture.Client.Paused,
            "An ambiguous resume performed an immediate identity rollback while a new task could be running.");
        fixture.Client.BeforeResume = null;
        var recovering = fixture.Coordinator.RecoverAsync(fixture.GroupId);
        await Task.Delay(30);
        TestAssert.Check(fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 0,
            "Recovery changed the service before the newly admitted work finished.");
        fixture.Client.Running = [];
        var recovered = await recovering;
        TestAssert.Check(recovered.State?.Phase == AccountSwitchPhase.RolledBack &&
            SameContext(fixture.Client.Context, original) && !fixture.Client.Paused &&
            fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
            "Explicit recovery did not verify the original service after new work finished.");
    }

    private static async Task InterruptedAsync()
    {
        foreach (var originalServiceAvailable in new[] { false, true })
        {
            using var fixture = new SwitchFixture();
            var original = fixture.Client.Context;
            string? interruptedState = null;
            fixture.Client.AfterIdentityChanged = _ => interruptedState ??= File.ReadAllText(fixture.RuntimePath);
            TestAssert.Check((await fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid())).Succeeded,
                "The setup switch failed.");
            File.WriteAllText(fixture.RuntimePath, interruptedState);
            fixture.Client.OriginalServiceAvailable = originalServiceAvailable;
            var replacement = new AccountSwitchCoordinator(fixture.Paths, fixture.Client);
            await TestAssert.ThrowsCodeAsync<HubException>(() => replacement.ExecuteAsync(fixture.GroupId, Guid.NewGuid()),
                "shared_switch_recovery_required");
            var restored = await replacement.RecoverAsync(fixture.GroupId);
            TestAssert.Check(!restored.Succeeded && fixture.Client.SwitchCalls == 1 && fixture.Client.ServiceRestoreCalls == 1,
                "Recovering an interrupted operation performed another identity switch.");
            if (originalServiceAvailable)
                TestAssert.Check(restored.State?.Phase == AccountSwitchPhase.RolledBack &&
                    SameContext(fixture.Client.Context, original) && !fixture.Client.Paused,
                    "An interrupted switch was accepted without verifying the original official service.");
            else
                TestAssert.Check(restored.Code == "shared_switch_original_service_unavailable" &&
                    restored.State is { State: AccountSwitchStatus.RecoveryRequired, ActiveProfileId: null, SendingBlocked: true } &&
                    fixture.Client.Paused,
                    "A saved pre-interruption context was treated as an available original service.");
        }
    }

    private static async Task RuntimeSchemaAsync()
    {
        using var fixture = new SwitchFixture();
        var root = Path.GetDirectoryName(fixture.RuntimePath)!;
        Directory.CreateDirectory(root);
        const string corrupt = "{\"SchemaVersion\":99}";
        File.WriteAllText(fixture.RuntimePath, corrupt);
        await TestAssert.ThrowsCodeAsync<HubException>(() => fixture.Coordinator.ExecuteAsync(fixture.GroupId, Guid.NewGuid()),
            "shared_switch_runtime_invalid");
        TestAssert.Check(File.ReadAllText(fixture.RuntimePath) == corrupt && fixture.Client.SwitchCalls == 0,
            "An unknown runtime schema was overwritten or reached the client.");
    }

    private static bool SameContext(SharedClientContext left, SharedClientContext right) =>
        left.ProfileId == right.ProfileId && left.WindowId == right.WindowId &&
        left.ActiveConversationId == right.ActiveConversationId && left.Conversations.SequenceEqual(right.Conversations);

    private sealed class SwitchFixture : IDisposable
    {
        private readonly OwnedTestDirectory _directory = new(prefix: "agprofile-shared-switch-");
        public Guid GroupId { get; } = Guid.NewGuid();
        public HubPaths Paths { get; }
        public FakeClient Client { get; } = new();
        public AccountSwitchCoordinator Coordinator { get; }
        public string RuntimePath => Path.Combine(Paths.DataRoot, "shared-groups", GroupId.ToString("D"), "runtime.json");
        public SwitchFixture()
        {
            Paths = new HubPaths(_directory.Root, Path.Combine(_directory.Root, "global"));
            Coordinator = new(Paths, Client, TimeSpan.FromMilliseconds(5));
        }
        public void Dispose() => _directory.Dispose();
    }

    private sealed class FakeClient : ISharedAccountClient
    {
        public SharedClientContext Context { get; private set; } = new(Guid.NewGuid(), "official-window-one", "conversation-b",
            [new("project-a", "conversation-a", "private draft one", "line:18/scroll:40"),
                new("project-b", "conversation-b", "private draft two", "line:3/scroll:220")]);
        public IReadOnlyList<SharedRunningTask> Running { get; set; } = [];
        public TaskCompletionSource ReadTasks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Paused { get; private set; }
        public int SwitchCalls { get; private set; }
        public int ServiceRestoreCalls { get; private set; }
        public bool OriginalServiceAvailable { get; set; } = true;
        public List<bool> PauseTransitions { get; } = [];
        public Func<Guid, string?>? VerificationFailure { get; set; }
        public Action<Guid>? AfterIdentityChanged { get; set; }
        public Action<Guid>? BeforeVerification { get; set; }
        public Action? BeforeRestore { get; set; }
        public Action? BeforeServiceRestore { get; set; }
        public Action? BeforeResume { get; set; }
        public Task<SharedClientCapabilities> GetCapabilitiesAsync(Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedClientCapabilities(true, "Test fake; no production client verification."));
        public Task SetNewTasksPausedAsync(Guid groupId, bool paused, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Paused = paused; PauseTransitions.Add(paused);
            if (!paused) BeforeResume?.Invoke();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SharedRunningTask>> ReadRunningTasksAsync(Guid groupId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadTasks.TrySetResult(); return Task.FromResult(Running);
        }
        public Task<SharedClientContext> CaptureContextAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(Context);
        public Task SwitchIdentityAsync(Guid groupId, Guid targetProfileId, CancellationToken cancellationToken = default)
        {
            TestAssert.Check(Paused && Running.Count == 0, "The coordinator changed identity before stopping new submissions and draining work.");
            SwitchCalls++; Context = Context with { ProfileId = targetProfileId }; AfterIdentityChanged?.Invoke(targetProfileId);
            cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
        public Task<bool> TryRestoreOriginalServiceAsync(Guid groupId, SharedClientContext originalContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestAssert.Check(Paused && Running.Count == 0, "The coordinator reattached a service before active work finished.");
            ServiceRestoreCalls++;
            if (!OriginalServiceAvailable) return Task.FromResult(false);
            BeforeServiceRestore?.Invoke();
            Context = Context with { ProfileId = originalContext.ProfileId };
            return Task.FromResult(true);
        }
        public Task RestoreContextAsync(Guid groupId, SharedClientContext context, CancellationToken cancellationToken = default)
        {
            BeforeRestore?.Invoke(); Context = context with { ProfileId = Context.ProfileId }; return Task.CompletedTask;
        }
        public Task<SharedAccountVerification> VerifyAsync(Guid groupId, Guid expectedProfileId,
            SharedClientContext expectedContext, CancellationToken cancellationToken = default)
        {
            BeforeVerification?.Invoke(expectedProfileId);
            var failure = VerificationFailure?.Invoke(expectedProfileId);
            var actual = failure switch
            {
                "window" => Context with { WindowId = "a-different-window" },
                "conversation" => Context with { ActiveConversationId = "conversation-a" },
                "draft" => Context with { Conversations = [Context.Conversations[0] with { Draft = "lost" }, Context.Conversations[1]] },
                "view" => Context with { Conversations = [Context.Conversations[0] with { ViewLocation = "line:1/scroll:0" }, Context.Conversations[1]] },
                _ => Context
            };
            return Task.FromResult(new SharedAccountVerification(failure == "profile" ? Guid.NewGuid() : Context.ProfileId,
                failure != "identity", actual, failure != "model"));
        }
    }
}
