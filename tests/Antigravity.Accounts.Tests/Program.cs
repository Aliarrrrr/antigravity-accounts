using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Antigravity.Accounts;

var now = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
var cases = new (string Name, Func<Task> Run)[]
{
    ("no startup network and no implicit selection", NoImplicitSelection),
    ("public projections exclude all credentials", PublicProjection),
    ("official credential imports are rejected", RejectImports),
    ("direct selection freezes after token request", FrozenSelection),
    ("primary identity and attached account deletion are constrained", PrimaryIdentity),
    ("deleting an inactive attached account preserves both remaining identities and switches", RemoveInactiveAttached),
    ("account label edits persist without changing active identity or credentials", RenameAccount),
    ("account label edits reject invalid requests and pending switches", RenameAccountBoundaries),
    ("switch commit changes quota identity without restarting service", SwitchCommit),
    ("pending switch blocks all token requests until cancellation", SwitchCancellation),
    ("failed target refresh preserves prior selected account", FailedSwitch),
    ("switch tickets and generation reject stale operations", SwitchGeneration),
    ("caller cancellation releases pending preparation without selecting target", CancelPreparingSwitch),
    ("expired prepared token cannot be committed", ExpiredPreparedSwitch),
    ("active token refresh prevents starting a switch", SwitchDuringTokenRequest),
    ("legacy storage migration preserves accounts and recoverable original", LegacyMigration),
    ("stdio runtime switch reports matching token generation", StdioRuntimeSwitch),
    ("parallel token calls share a refresh", ConcurrentRefresh),
    ("invalid grant disables account without fallback", InvalidGrant),
    ("initial invalid grant releases selection for explicit recovery", InitialInvalidGrantRecovery),
    ("initial temporary refresh failure permits replacing authorization", InitialTemporaryFailureRecovery),
    ("refresh failure after a successful token keeps selection locked", EstablishedRefreshFailure),
    ("initial cancellation unlocks only after every token waiter ends", InitialCancelledSelection),
    ("one successful concurrent token keeps selection locked after another cancels", ConcurrentSuccessKeepsSelection),
    ("abandoned initial refresh cannot overwrite replacement authorization", AbandonedRefreshAuthorization),
    ("OAuth PKCE and callback state are checked", ProofValidation),
    ("OAuth loopback callback exchanges PKCE and verifies subject", LoopbackLogin),
    ("initial authorization requires an offline refresh token", MissingRefreshToken),
    ("encrypted store persists only protected bytes", EncryptedStore),
    ("refresh token rotation is preserved", RotatedRefresh),
    ("HTTP unauthorized disables even with non-JSON body", UnauthorizedResponse),
    ("explicit login records verified identity without selecting it", ExplicitLogin),
    ("OAuth scopes and client changes are constrained", ConfigurationBoundaries),
    ("a native scope reduction preserves existing accounts and credentials", NativeScopeReduction),
    ("cancelled refresh waiter does not cancel shared refresh", CancelledWaiter),
    ("completed refresh is released after its only waiter cancels", CancelledOnlyWaiter),
    ("stdio arguments and JSON request boundaries work in a real child", StdioBoundaries),
    ("stdio persists synthetic accounts with Windows DPAPI", StdioDpapiAccounts),
    ("Windows DPAPI roundtrip uses synthetic data", DpapiRoundtrip)
};
var failures = 0;
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error.GetType().Name + " " + error.Message); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} passed; no real authentication or model requests.");
return failures == 0 ? 0 : 1;

SavedAccount Account(Guid? id = null, bool expired = false) => new()
{
    Id = id ?? Guid.NewGuid(), Subject = Guid.NewGuid().ToString("N"), Label = "Synthetic", Email = "synthetic@example.invalid",
    Client = new("synthetic.apps.googleusercontent.com", "synthetic-client-secret"),
    Token = new("synthetic-access-secret", "Bearer", "synthetic-refresh-secret", now.AddMinutes(expired ? -1 : 30))
};
AccountService Service(MemoryStore store, FakeOAuth oauth) => new(store, oauth, _ => oauth.BrowserOpens++, () => now);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Expect(string code, Func<Task> action)
{
    try { await action(); }
    catch (AccountOperationException error) when (error.Code == code) { return; }
    throw new Exception("Expected error " + code);
}
async Task NoImplicitSelection()
{
    var oauth = new FakeOAuth(now);
    await using var service = Service(new(), oauth);
    _ = await service.GetStatusAsync(); _ = await service.ListAccountsAsync();
    await Expect("account_not_selected", async () => _ = await service.GetTokenAsync());
    Check(oauth.Logins == 0 && oauth.Refreshes == 0 && oauth.BrowserOpens == 0, "Reading state triggered authentication.");
}
async Task PublicProjection()
{
    var a = Account(); var store = new MemoryStore(new() { OAuth = a.Client, Accounts = [a], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, new(now));
    var json = JsonSerializer.Serialize(new { status = await service.GetStatusAsync(), accounts = await service.ListAccountsAsync() });
    Check(!json.Contains("secret", StringComparison.Ordinal) && !json.Contains("refresh", StringComparison.OrdinalIgnoreCase) && !json.Contains("accessToken", StringComparison.OrdinalIgnoreCase), "A public response exposed credentials.");
}
async Task RejectImports()
{
    await using var service = Service(new(), new(now));
    using var value = JsonDocument.Parse("{\"tokenFile\":\"never-read-this-file\"}");
    await Expect("method_not_supported", () => Antigravity.Accounts.Program.DispatchAsync(service, "importOfficialAccount", value.RootElement));
    await Expect("unsupported_parameter", () => Antigravity.Accounts.Program.DispatchAsync(service, "beginLogin", value.RootElement));
    using var inline = JsonDocument.Parse("{\"accessToken\":\"synthetic\"}");
    await Expect("unsupported_parameter", () => Antigravity.Accounts.Program.DispatchAsync(service, "configureOAuth", inline.RootElement));
}
async Task FrozenSelection()
{
    var a = Account(); var b = Account(); var store = new MemoryStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, new(now));
    _ = await service.GetTokenAsync();
    await Expect("selection_locked", () => service.SelectAccountAsync(b.Id));
    await Expect("account_locked", () => service.RemoveAccountAsync(a.Id));
    await store.UpdateAsync(s => s.SelectedAccountId = b.Id);
    Check((await service.GetStatusAsync()).SelectedAccountId == a.Id, "External selection changed a frozen service identity.");
}
async Task PrimaryIdentity()
{
    var primary = Account(); var attached = Account(); var oauth = new FakeOAuth(now);
    var store = new MemoryStore(new() { OAuth = primary.Client, Accounts = [primary, attached], SelectedAccountId = attached.Id });
    await using var service = Service(store, oauth);
    await Expect("primary_account_not_set", async () => _ = await service.GetTokenAsync());
    Check((await service.ListAccountsAsync()).All(a => a.Role == "unassigned"), "Legacy accounts were implicitly assigned a primary.");
    await service.SetPrimaryAccountAsync(primary.Id);
    var list = await service.ListAccountsAsync();
    Check(list.Single(a => a.Id == primary.Id).Role == "primary" && list.Single(a => a.Id == attached.Id).Role == "attached" &&
        list.All(a => a.PrimaryAccountId == primary.Id) && (await service.GetStatusAsync()).SelectedAccountId == primary.Id,
        "The primary/attached relationship was not established explicitly.");
    await Expect("primary_account_already_set", () => service.SetPrimaryAccountAsync(attached.Id));
    await Expect("primary_has_attached_accounts", () => service.RemoveAccountAsync(primary.Id));
    await service.SelectAccountAsync(attached.Id);
    _ = await service.GetTokenAsync();
    await Expect("account_locked", () => service.RemoveAccountAsync(primary.Id));
    await service.BeginLoginAsync("New attached account");
    for (var i = 0; i < 100 && (await service.GetStatusAsync()).Login.State == "pending"; i++) await Task.Delay(5);
    Check((await service.ListAccountsAsync()).Count == 3 && (await service.GetStatusAsync()).PrimaryAccountId == primary.Id,
        "Adding an independently authorized attached account replaced or required restarting the primary.");
}
async Task RenameAccount()
{
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        static byte[] Transform(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0xAA)).ToArray();
        var store = new EncryptedAccountStore(directory, Transform, Transform);
        var primary = Account(); var attached = Account(); var oauth = new FakeOAuth(now);
        await store.UpdateAsync(s =>
        {
            s.OAuth = primary.Client; s.Accounts = [primary, attached];
            s.PrimaryAccountId = primary.Id; s.SelectedAccountId = attached.Id; s.Generation = 7;
        });
        await using (var service = new AccountService(store, oauth, _ => oauth.BrowserOpens++, () => now))
        {
            _ = await service.GetTokenAsync();
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new { id = attached.Id, label = "  工作账号  " }));
            _ = await Antigravity.Accounts.Program.DispatchAsync(service, "renameAccount", parameters.RootElement);
            Check((await service.ListAccountsAsync()).Single(a => a.Id == attached.Id).Label == "工作账号", "The public account label was not updated.");
            var status = await service.GetStatusAsync();
            Check(status.SelectedAccountId == attached.Id && status.PrimaryAccountId == primary.Id && status.SelectionLocked && status.Generation == 7,
                "A label edit changed the active identity or generation.");
        }
        var reopened = new EncryptedAccountStore(directory, Transform, Transform);
        var saved = await reopened.ReadAsync();
        var changed = saved.Accounts.Single(a => a.Id == attached.Id);
        Check(changed.Label == "工作账号" && changed.Token == attached.Token && changed.Client == attached.Client &&
            changed.Subject == attached.Subject && changed.Email == attached.Email && !changed.Disabled &&
            saved.Accounts.Single(a => a.Id == primary.Id).Label == primary.Label && oauth.Logins == 0 && oauth.Refreshes == 0 && oauth.BrowserOpens == 0,
            "The label did not persist independently of authorization and identity fields.");
        await using var restarted = new AccountService(reopened, oauth, _ => oauth.BrowserOpens++, () => now);
        await restarted.RenameAccountAsync(attached.Id, " ");
        Check((await restarted.ListAccountsAsync()).Single(a => a.Id == attached.Id).Label == attached.Email, "An empty label did not restore the email display.");
    }
    finally { CleanupSyntheticDirectory(directory); }
}
async Task RenameAccountBoundaries()
{
    var a = Account(); var b = Account();
    var store = new MemoryStore(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] });
    await using var service = Service(store, new(now));
    await Expect("invalid_label", () => service.RenameAccountAsync(a.Id, new string('x', 81)));
    await Expect("invalid_label", () => service.RenameAccountAsync(a.Id, "bad\nlabel"));
    await Expect("account_not_found", () => service.RenameAccountAsync(Guid.NewGuid(), "New label"));
    foreach (var fields in new object[] { new { id = a.Id }, new { id = a.Id, label = 1 } })
    {
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(fields));
        await Expect("invalid_request", () => Antigravity.Accounts.Program.DispatchAsync(service, "renameAccount", parameters.RootElement));
    }
    using (var extra = JsonDocument.Parse(JsonSerializer.Serialize(new { id = a.Id, label = "New label", accessToken = "synthetic" })))
        await Expect("unsupported_parameter", () => Antigravity.Accounts.Program.DispatchAsync(service, "renameAccount", extra.RootElement));
    var ticket = await service.BeginSwitchAsync(b.Id, 0);
    await Expect("switch_in_progress", () => service.RenameAccountAsync(a.Id, "New label"));
    await service.CancelSwitchAsync(ticket.Ticket);
    Check((await service.ListAccountsAsync()).All(account => account.Label == "Synthetic"), "A rejected label edit changed an account.");
}
async Task SwitchCommit()
{
    var a = Account(); var b = Account();
    b.Token = b.Token with { AccessToken = "synthetic-attached-access" };
    var oauth = new FakeOAuth(now); var store = new MemoryStore(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] });
    await using var service = Service(store, oauth);
    var before = await service.GetTokenResponseAsync();
    var pending = await service.BeginSwitchAsync(b.Id, before.Generation);
    Check((await service.GetStatusAsync()).SelectedAccountId == a.Id && (await service.GetStatusAsync()).PendingSwitch?.State == "ready",
        "Preparing a switch changed the selected account.");
    await Expect("switch_in_progress", async () => _ = await service.GetTokenAsync());
    var committed = await service.CommitSwitchAsync(pending.Ticket);
    var after = await service.GetTokenResponseAsync();
    Check(after.AccountId == b.Id && after.AccessToken == "synthetic-attached-access" && after.Generation == before.Generation + 1 &&
        committed.Generation == after.Generation && (await service.GetStatusAsync()).PrimaryAccountId == a.Id && oauth.Refreshes == 0,
        "A committed switch did not update the quota token/generation while preserving the primary account.");
    var back = await service.BeginSwitchAsync(a.Id, after.Generation);
    await service.CommitSwitchAsync(back.Ticket);
    Check((await service.GetTokenResponseAsync()).AccountId == a.Id, "Switching back required another service instance.");
}
async Task RemoveInactiveAttached()
{
    var primary = Account(); var current = Account(); var attached = Account();
    var primaryToken = primary.Token; var attachedToken = attached.Token;
    var primaryClient = primary.Client; var attachedClient = attached.Client;
    var oauth = new FakeOAuth(now);
    var store = new MemoryStore(new() { PrimaryAccountId = primary.Id, SelectedAccountId = current.Id, Accounts = [primary, current, attached] });
    await using var service = Service(store, oauth);
    var original = await service.GetTokenResponseAsync();
    await Expect("account_locked", () => service.RemoveAccountAsync(current.Id));
    var prepared = await service.BeginSwitchAsync(attached.Id, original.Generation);
    await Expect("switch_in_progress", () => service.RemoveAccountAsync(current.Id));
    await service.CommitSwitchAsync(prepared.Ticket);
    var selected = await service.GetTokenResponseAsync();
    await service.RemoveAccountAsync(current.Id);
    var status = await service.GetStatusAsync();
    var remaining = await store.ReadAsync();
    Check(status.SelectedAccountId == attached.Id && status.PrimaryAccountId == primary.Id && status.Generation == selected.Generation &&
        remaining.Accounts.Count == 2 && remaining.Accounts.All(a => a.Id != current.Id) &&
        remaining.Accounts.Single(a => a.Id == primary.Id).Token == primaryToken && remaining.Accounts.Single(a => a.Id == primary.Id).Client == primaryClient &&
        remaining.Accounts.Single(a => a.Id == attached.Id).Token == attachedToken && remaining.Accounts.Single(a => a.Id == attached.Id).Client == attachedClient,
        "Deleting an inactive account changed a remaining identity, credential, or generation.");
    foreach (var target in new[] { primary.Id, attached.Id })
    {
        var before = await service.GetStatusAsync();
        var ticket = await service.BeginSwitchAsync(target, before.Generation);
        await service.CommitSwitchAsync(ticket.Ticket);
        Check((await service.GetTokenResponseAsync()).AccountId == target, "The remaining account switch was affected by deletion.");
    }
    Check(oauth.Refreshes == 0 && oauth.Logins == 0 && oauth.BrowserOpens == 0, "Deleting an inactive account triggered authentication.");
}
async Task SwitchCancellation()
{
    var a = Account(); var b = Account(expired: true); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    await using var service = Service(new(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] }), oauth);
    _ = await service.GetTokenAsync();
    var beginning = service.BeginSwitchAsync(b.Id, 0);
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var status = await service.GetStatusAsync();
    Check(status.PendingSwitch?.State == "preparing" && status.SelectedAccountId == a.Id, "Pending refresh was not exposed as preparing.");
    await Expect("switch_in_progress", async () => _ = await service.GetTokenAsync());
    await Expect("switch_in_progress", async () => _ = await service.BeginSwitchAsync(a.Id, 0));
    await Expect("switch_not_ready", async () => _ = await service.CommitSwitchAsync(status.PendingSwitch!.Ticket));
    await service.CancelSwitchAsync(status.PendingSwitch!.Ticket);
    Check((await service.GetTokenResponseAsync()).AccountId == a.Id, "Cancelling a pending switch changed the current account.");
    oauth.ReleaseRefresh.TrySetResult();
    await Expect("switch_cancelled", async () => _ = await beginning);
    Check((await service.GetStatusAsync()).Generation == 0 && (await service.GetStatusAsync()).PendingSwitch is null && oauth.Refreshes == 1,
        "A cancelled preparation changed the committed selection.");
}
async Task FailedSwitch()
{
    var a = Account(); var b = Account(expired: true); var oauth = new FakeOAuth(now) { InvalidGrant = true };
    await using var service = Service(new(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] }), oauth);
    _ = await service.GetTokenAsync();
    await Expect("account_reauth_required", async () => _ = await service.BeginSwitchAsync(b.Id, 0));
    Check((await service.GetStatusAsync()).PendingSwitch is null && (await service.GetTokenResponseAsync()).AccountId == a.Id &&
        (await service.ListAccountsAsync()).Single(x => x.Id == b.Id).Disabled, "Failed preparation changed accounts or left a stuck transaction.");
    await Expect("account_reauth_required", async () => _ = await service.BeginSwitchAsync(b.Id, 0));
    Check(oauth.Refreshes == 1, "A disabled target retried or used another account.");
}
async Task SwitchGeneration()
{
    var a = Account(); var b = Account(); var store = new MemoryStore(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] });
    await using var service = Service(store, new(now));
    _ = await service.GetTokenAsync();
    await Expect("selection_changed", async () => _ = await service.BeginSwitchAsync(b.Id, 1));
    var pending = await service.BeginSwitchAsync(b.Id, 0);
    await Expect("invalid_switch_ticket", async () => _ = await service.CommitSwitchAsync(Guid.NewGuid()));
    await Expect("invalid_switch_ticket", () => service.CancelSwitchAsync(Guid.NewGuid()));
    Check((await service.GetStatusAsync()).PendingSwitch!.Ticket == pending.Ticket, "A wrong ticket cancelled another switch.");
    await store.UpdateAsync(s => s.Generation++);
    await Expect("selection_changed", async () => _ = await service.CommitSwitchAsync(pending.Ticket));
    await service.CancelSwitchAsync(pending.Ticket);
    Check((await service.GetStatusAsync()).SelectedAccountId == a.Id, "A stale commit changed the live account.");
}
async Task SwitchDuringTokenRequest()
{
    var a = Account(expired: true); var b = Account(); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    await using var service = Service(new(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] }), oauth);
    var token = service.GetTokenAsync();
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await Expect("token_request_in_progress", async () => _ = await service.BeginSwitchAsync(b.Id, 0));
    oauth.ReleaseRefresh.TrySetResult();
    _ = await token;
    var pending = await service.BeginSwitchAsync(b.Id, 0);
    await service.CommitSwitchAsync(pending.Ticket);
    Check((await service.GetTokenResponseAsync()).AccountId == b.Id, "Completed token work prevented a later switch.");
}
async Task CancelPreparingSwitch()
{
    var a = Account(); var b = Account(expired: true); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    await using var service = Service(new(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] }), oauth);
    _ = await service.GetTokenAsync();
    using var cancellation = new CancellationTokenSource();
    var preparing = service.BeginSwitchAsync(b.Id, 0, cancellation.Token);
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancellation.Cancel();
    try { _ = await preparing; throw new Exception("Cancelled switch preparation completed."); }
    catch (OperationCanceledException) { }
    Check((await service.GetStatusAsync()).PendingSwitch is null && (await service.GetTokenResponseAsync()).AccountId == a.Id,
        "Cancelling preparation selected its target or left a pending switch.");
    oauth.ReleaseRefresh.TrySetResult();
}
async Task ExpiredPreparedSwitch()
{
    var a = Account(); var b = Account(); var clock = now;
    await using var service = new AccountService(new MemoryStore(new() { PrimaryAccountId = a.Id, SelectedAccountId = a.Id, Accounts = [a, b] }),
        new FakeOAuth(now), _ => { }, () => clock);
    _ = await service.GetTokenAsync();
    var pending = await service.BeginSwitchAsync(b.Id, 0);
    clock = now.AddHours(1);
    await Expect("switch_token_expired", async () => _ = await service.CommitSwitchAsync(pending.Ticket));
    var status = await service.GetStatusAsync();
    Check(status.SelectedAccountId == a.Id && status.Generation == 0 && status.PendingSwitch is null,
        "Committing an expired preparation changed the selected account.");
}
async Task LegacyMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    static byte[] Transform(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0xAA)).ToArray();
    try
    {
        Directory.CreateDirectory(directory);
        var a = Account(); var b = Account(expired: true);
        var original = Transform(JsonSerializer.SerializeToUtf8Bytes(new SavedState { SchemaVersion = 1, OAuth = a.Client, Accounts = [a, b], SelectedAccountId = b.Id },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var path = Path.Combine(directory, "accounts.v1.dpapi");
        await File.WriteAllBytesAsync(path, original);
        var store = new EncryptedAccountStore(directory, Transform, Transform);
        var migrated = await store.ReadAsync();
        Check(migrated.SchemaVersion == 2 && migrated.PrimaryAccountId is null && migrated.SelectedAccountId == b.Id && migrated.Accounts.Count == 2 &&
            (await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Reading legacy data lost accounts, guessed a primary, or rewrote the original.");
        await using var service = new AccountService(store, new FakeOAuth(now), _ => { }, () => now);
        await service.SetPrimaryAccountAsync(a.Id);
        var converted = await store.ReadAsync();
        var backup = Path.Combine(directory, "accounts.v1.before-v2.dpapi");
        Check((await File.ReadAllBytesAsync(backup)).SequenceEqual(original) && converted.PrimaryAccountId == a.Id && converted.Accounts.Count == 2 &&
            converted.Accounts.Single(x => x.Id == b.Id).Token.RefreshToken == b.Token.RefreshToken, "Migration did not preserve legacy bytes and account authorizations.");
        File.Copy(backup, path, true);
        var restored = await store.ReadAsync();
        Check(restored.PrimaryAccountId is null && restored.SelectedAccountId == b.Id && restored.Accounts.Select(x => x.Id).SequenceEqual(new[] { a.Id, b.Id }) &&
            restored.Accounts[0].Token == a.Token && restored.Accounts[1].Token == b.Token, "Restoring the legacy encrypted copy changed account records.");
    }
    finally { CleanupSyntheticDirectory(directory); }
}
async Task ConcurrentRefresh()
{
    var a = Account(expired: true); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    await using var service = Service(new(new() { Accounts = [a], PrimaryAccountId = a.Id, SelectedAccountId = a.Id }), oauth);
    var tasks = Enumerable.Range(0, 12).Select(_ => service.GetTokenAsync()).ToArray();
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    oauth.ReleaseRefresh.TrySetResult();
    var tokens = await Task.WhenAll(tasks);
    Check(oauth.Refreshes == 1 && tokens.All(t => t.AccessToken == "synthetic-refreshed-secret"), "Refresh was not shared.");
}
async Task InvalidGrant()
{
    var a = Account(expired: true); var b = Account(); var oauth = new FakeOAuth(now) { InvalidGrant = true };
    var store = new MemoryStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, oauth);
    await Expect("account_reauth_required", async () => _ = await service.GetTokenAsync());
    await Expect("account_reauth_required", async () => _ = await service.GetTokenAsync());
    Check(oauth.Refreshes == 1 && (await service.GetStatusAsync()).SelectedAccountId == a.Id && (await store.ReadAsync()).Accounts[0].Disabled,
        "Invalid grant retried or changed identity.");
}
async Task InitialInvalidGrantRecovery()
{
    var a = Account(expired: true); var b = Account(); var oauth = new FakeOAuth(now) { InvalidGrant = true };
    var store = new MemoryStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, oauth);
    await Expect("account_reauth_required", async () => _ = await service.GetTokenResponseAsync());
    var failed = await service.GetStatusAsync();
    Check(!failed.SelectionLocked && failed.SelectedAccountId == a.Id && failed.SelectedAccountDisabled &&
        failed.PrimaryAccountId == a.Id && failed.Generation == 0, "A failed initial token locked selection or changed the primary account.");
    await service.SelectAccountAsync(b.Id);
    var recovered = await service.GetTokenResponseAsync();
    Check(recovered.AccountId == b.Id && recovered.Generation == 1 && (await service.GetStatusAsync()).SelectionLocked &&
        oauth.Refreshes == 1, "Explicitly selecting the usable attached account did not recover initial startup.");
}
async Task InitialTemporaryFailureRecovery()
{
    var a = Account(expired: true); a.Subject = "synthetic-subject";
    var oauth = new FakeOAuth(now) { TemporaryFailure = true };
    var store = new MemoryStore(new() { OAuth = a.Client, Accounts = [a], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, oauth);
    await Expect("oauth_request_failed", async () => _ = await service.GetTokenResponseAsync());
    var failed = await service.GetStatusAsync();
    Check(!failed.SelectionLocked && !failed.SelectedAccountDisabled, "A temporary initial refresh failure prevented account recovery.");
    await service.BeginLoginAsync("Reauthorized primary");
    for (var i = 0; i < 100 && (await service.GetStatusAsync()).Login.State == "pending"; i++) await Task.Delay(5);
    var recovered = await service.GetTokenResponseAsync();
    var status = await service.GetStatusAsync();
    Check(status.Login.State == "succeeded" && status.SelectionLocked && status.PrimaryAccountId == a.Id && status.Generation == 0 &&
        recovered.AccountId == a.Id && recovered.AccessToken == "synthetic-login-access" && (await service.ListAccountsAsync()).Count == 1,
        "Reauthorizing the same primary did not recover its initial failed token request.");
}
async Task EstablishedRefreshFailure()
{
    var a = Account(); var b = Account(); var oauth = new FakeOAuth(now) { InvalidGrant = true };
    var store = new MemoryStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, oauth);
    _ = await service.GetTokenResponseAsync();
    await store.UpdateAsync(s => s.Accounts.Single(x => x.Id == a.Id).Token = a.Token with { Expiry = now.AddMinutes(-1) });
    await Expect("account_reauth_required", async () => _ = await service.GetTokenResponseAsync());
    var failed = await service.GetStatusAsync();
    Check(failed.SelectionLocked && failed.SelectedAccountId == a.Id && failed.SelectedAccountDisabled && failed.Generation == 0,
        "A refresh failure after native token use unlocked or changed its identity.");
    await Expect("selection_locked", () => service.SelectAccountAsync(b.Id));
    var recovery = await service.BeginSwitchAsync(b.Id, failed.Generation);
    await service.CommitSwitchAsync(recovery.Ticket);
    Check((await service.GetTokenResponseAsync()).AccountId == b.Id && (await service.GetStatusAsync()).SelectionLocked,
        "A locked failed account could not recover through the host-controlled switch transaction.");
}
async Task InitialCancelledSelection()
{
    var a = Account(expired: true); var b = Account(); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    var store = new MemoryStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = Service(store, oauth);
    using var firstCancellation = new CancellationTokenSource();
    using var lastCancellation = new CancellationTokenSource();
    var first = service.GetTokenResponseAsync(firstCancellation.Token);
    var last = service.GetTokenResponseAsync(lastCancellation.Token);
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    try
    {
        firstCancellation.Cancel();
        try { _ = await first; throw new Exception("The first cancelled waiter completed."); }
        catch (OperationCanceledException) { }
        Check((await service.GetStatusAsync()).SelectionLocked, "One cancelled waiter unlocked another in-flight token request.");
        await Expect("selection_locked", () => service.SelectAccountAsync(b.Id));
        await Expect("token_request_in_progress", async () => _ = await service.BeginSwitchAsync(b.Id, 0));
        await Expect("account_locked", () => service.RemoveAccountAsync(a.Id));
        lastCancellation.Cancel();
        try { _ = await last; throw new Exception("The final cancelled waiter completed."); }
        catch (OperationCanceledException) { }
        var cancelled = await service.GetStatusAsync();
        Check(!cancelled.SelectionLocked && cancelled.SelectedAccountId == a.Id && cancelled.Generation == 0,
            "The final failed initial waiter left a locked or changed identity.");
        await service.SelectAccountAsync(b.Id);
        var selected = await service.GetTokenResponseAsync();
        Check(selected.AccountId == b.Id && selected.Generation == 1 && (await service.GetStatusAsync()).SelectionLocked,
            "An abandoned initial refresh prevented explicit selection of another account.");
    }
    finally { oauth.ReleaseRefresh.TrySetResult(); }
}
async Task AbandonedRefreshAuthorization()
{
    foreach (var invalidGrant in new[] { false, true })
    {
        var a = Account(expired: true); a.Subject = "synthetic-subject";
        var oauth = new FakeOAuth(now) { DelayRefresh = true, InvalidGrant = invalidGrant };
        var store = new MemoryStore(new() { OAuth = a.Client, Accounts = [a], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
        await using var service = Service(store, oauth);
        using var cancellation = new CancellationTokenSource();
        var abandoned = service.GetTokenResponseAsync(cancellation.Token);
        await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            cancellation.Cancel();
            try { _ = await abandoned; throw new Exception("The cancelled initial token request completed."); }
            catch (OperationCanceledException) { }
            await service.BeginLoginAsync("Replacement authorization");
            for (var i = 0; i < 100 && (await service.GetStatusAsync()).Login.State == "pending"; i++) await Task.Delay(5);
            Check((await service.GetStatusAsync()).Login.State == "succeeded", "Initial cancellation did not permit explicit reauthorization.");
            var replacement = (await store.ReadAsync()).Accounts.Single().Token;
            var oldRefreshWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.Updated = () => oldRefreshWritten.TrySetResult();
            oauth.ReleaseRefresh.TrySetResult();
            await oldRefreshWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var saved = (await store.ReadAsync()).Accounts.Single();
            Check(saved.Token == replacement && !saved.Disabled,
                invalidGrant ? "An abandoned invalid grant disabled replacement authorization." : "An abandoned refresh overwrote replacement authorization.");
            var current = await service.GetTokenResponseAsync();
            Check(current.AccessToken == "synthetic-login-access" && current.AccountId == a.Id,
                "Replacement authorization was not provided after the abandoned refresh finished.");
        }
        finally { oauth.ReleaseRefresh.TrySetResult(); }
    }
}
async Task ConcurrentSuccessKeepsSelection()
{
    var a = Account(); var b = Account();
    var store = new PausingTokenReadStore(new() { Accounts = [a, b], PrimaryAccountId = a.Id, SelectedAccountId = a.Id });
    await using var service = new AccountService(store, new FakeOAuth(now), _ => { }, () => now);
    using var cancellation = new CancellationTokenSource();
    var cancelled = service.GetTokenResponseAsync(cancellation.Token);
    await store.TokenReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    try
    {
        var provided = await service.GetTokenResponseAsync();
        Check(provided.AccountId == a.Id, "The second concurrent token request did not return the selected account.");
        cancellation.Cancel();
        try { _ = await cancelled; throw new Exception("The cancelled concurrent request completed."); }
        catch (OperationCanceledException) { }
        var status = await service.GetStatusAsync();
        Check(status.SelectionLocked && status.SelectedAccountId == a.Id && status.Generation == 0,
            "The last failing request unlocked an identity already returned by another concurrent call.");
        await Expect("selection_locked", () => service.SelectAccountAsync(b.Id));
    }
    finally { cancellation.Cancel(); store.ReleaseTokenRead.TrySetResult(); }
}
Task ProofValidation()
{
    var proof = OAuthProof.Create();
    var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(proof.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Check(proof.Verifier.Length >= 43 && proof.Challenge == challenge && proof.State != OAuthProof.Create().State, "PKCE/state generation failed.");
    Check(proof.ValidateCallback(proof.State, "synthetic-code", null) == "synthetic-code", "Valid callback was rejected.");
    try { proof.ValidateCallback("wrong-state", "synthetic-code", null); throw new Exception("Bad state accepted."); }
    catch (AccountOperationException error) when (error.Code == "oauth_state_mismatch") { }
    return Task.CompletedTask;
}
async Task LoopbackLogin()
{
    var handler = new LoginHandler();
    using var oauth = new GoogleOAuth(handler, () => now);
    using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Task<HttpResponseMessage>? callback = null;
    var result = await oauth.LoginAsync(new("synthetic.apps.googleusercontent.com", "synthetic-client-secret"), authorization =>
    {
        Check(authorization.Host == "accounts.google.com", "Unexpected authorization host.");
        var query = System.Web.HttpUtility.ParseQueryString(authorization.Query);
        handler.Challenge = query["code_challenge"];
        handler.Redirect = query["redirect_uri"];
        Check(query["code_challenge_method"] == "S256" && query["response_type"] == "code" &&
            query["access_type"] == "offline" && query["prompt"] == "consent" &&
            query["scope"]!.Split(' ').Order(StringComparer.Ordinal).SequenceEqual(OAuthScopes.Default.Order(StringComparer.Ordinal)),
            "Authorization request omitted native scopes or PKCE.");
        var redirect = new Uri(handler.Redirect!);
        Check(redirect.Host == "127.0.0.1" && redirect.AbsolutePath == "/oauth/callback", "Callback did not use loopback.");
        callback = loopback.GetAsync(handler.Redirect + "?state=" + Uri.EscapeDataString(query["state"]!) + "&code=synthetic-code", cancellation.Token);
    }, cancellation.Token);
    using var response = await callback!;
    Check(response.StatusCode == HttpStatusCode.OK && result.Subject == "synthetic-subject" && result.Email == "synthetic@example.invalid" &&
        result.Token.RefreshToken == "synthetic-login-refresh" && handler.Calls == 2,
        "The loopback login did not complete through the token and identity handlers.");
}
async Task MissingRefreshToken()
{
    var handler = new LoginHandler { OmitRefresh = true };
    using var oauth = new GoogleOAuth(handler, () => now);
    using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Task<HttpResponseMessage>? callback = null;
    await Expect("oauth_refresh_token_missing", async () => _ = await oauth.LoginAsync(new("synthetic.apps.googleusercontent.com", "synthetic-client-secret"), authorization =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(authorization.Query);
        handler.Challenge = query["code_challenge"]; handler.Redirect = query["redirect_uri"];
        callback = loopback.GetAsync(handler.Redirect + "?state=" + Uri.EscapeDataString(query["state"]!) + "&code=synthetic-code", cancellation.Token);
    }, cancellation.Token));
    using var response = await callback!;
    Check(handler.Calls == 1, "Missing offline authorization still queried or saved an identity.");
}
async Task EncryptedStore()
{
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        static byte[] Transform(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0xAA)).ToArray();
        var store = new EncryptedAccountStore(directory, Transform, Transform);
        var account = Account();
        await store.UpdateAsync(s => { s.Accounts.Add(account); s.PrimaryAccountId = account.Id; s.SelectedAccountId = account.Id; });
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "accounts.v1.dpapi"));
        Check(!Encoding.UTF8.GetString(bytes).Contains("synthetic-access-secret", StringComparison.Ordinal), "Credential was written in clear text.");
        Check((await store.ReadAsync()).Accounts.Single().Token.AccessToken == account.Token.AccessToken, "Encrypted store failed to roundtrip.");
    }
    finally { CleanupSyntheticDirectory(directory); }
}
async Task RotatedRefresh()
{
    var handler = new StubHandler(_ => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"access_token\":\"synthetic-next-access\",\"refresh_token\":\"synthetic-next-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")
    });
    using var oauth = new GoogleOAuth(handler, () => now);
    var account = Account(expired: true);
    var token = await oauth.RefreshAsync(account.Client, account.Token, default);
    Check(token.RefreshToken == "synthetic-next-refresh" && handler.Uris.Single().Host == "oauth2.googleapis.com", "Refresh rotation/endpoint failed.");
}
async Task UnauthorizedResponse()
{
    using var oauth = new GoogleOAuth(new StubHandler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("not-json") }), () => now);
    var account = Account(expired: true);
    await Expect("account_reauth_required", async () => _ = await oauth.RefreshAsync(account.Client, account.Token, default));
}
async Task ExplicitLogin()
{
    var oauth = new FakeOAuth(now); var store = new MemoryStore(new() { OAuth = new("synthetic.apps.googleusercontent.com") });
    await using var service = Service(store, oauth);
    await service.BeginLoginAsync("Chosen label");
    for (var i = 0; i < 100 && (await service.GetStatusAsync()).Login.State == "pending"; i++) await Task.Delay(5);
    var list = await service.ListAccountsAsync();
    Check(oauth.Logins == 1 && oauth.BrowserOpens == 1 && list.Single().Label == "Chosen label" && !list.Single().Selected, "Explicit login did not preserve selection rules.");
}
Task DpapiRoundtrip()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    var clear = Encoding.UTF8.GetBytes("synthetic-dpapi-roundtrip");
    var encrypted = WindowsDataProtection.Protect(clear);
    Check(!encrypted.SequenceEqual(clear) && WindowsDataProtection.Unprotect(encrypted).SequenceEqual(clear), "DPAPI roundtrip failed.");
    return Task.CompletedTask;
}
async Task ConfigurationBoundaries()
{
    var account = Account(); var store = new MemoryStore(new() { OAuth = account.Client, Accounts = [account] });
    await using var service = Service(store, new(now));
    await service.ConfigureOAuthAsync(account.Client with { Scopes = OAuthScopes.Default.Reverse().ToArray() });
    await Expect("oauth_configuration_in_use", () => service.ConfigureOAuthAsync(new("different.apps.googleusercontent.com")));
    await Expect("invalid_oauth_scopes", () => service.ConfigureOAuthAsync(account.Client with { Scopes = ["https://invalid.example/scope"] }));
    await Expect("invalid_oauth_scopes", () => service.ConfigureOAuthAsync(account.Client with { Scopes = ["openid"] }));
    await Expect("invalid_oauth_scopes", () => service.ConfigureOAuthAsync(account.Client with { Scopes = OAuthScopes.Default.Take(6).Append("openid").ToArray() }));
    Check((await store.ReadAsync()).OAuth!.ClientId == account.Client.ClientId, "Existing client configuration changed.");
}
async Task NativeScopeReduction()
{
    var account = Account(expired: true);
    account.Client = account.Client with { Scopes = OAuthScopes.Legacy.ToArray() };
    var originalClient = account.Client; var originalToken = account.Token;
    var state = new SavedState { OAuth = originalClient, Accounts = [account], PrimaryAccountId = account.Id, SelectedAccountId = account.Id, Generation = 7 };
    var store = new MemoryStore(state); var oauth = new FakeOAuth(now);
    await using var service = Service(store, oauth);
    await service.ConfigureOAuthAsync(originalClient with { Scopes = OAuthScopes.Default.ToArray() });
    var updated = await store.ReadAsync();
    Check(OAuthScopes.Normalize(updated.OAuth!.Scopes).SequenceEqual(OAuthScopes.Normalize(OAuthScopes.Default)), "New logins did not adopt native scopes.");
    Check(updated.Accounts.Single().Client == originalClient && updated.Accounts.Single().Token == originalToken, "Existing account authorization was rewritten.");
    Check(updated.PrimaryAccountId == account.Id && updated.SelectedAccountId == account.Id && updated.Generation == 7, "Scope reduction changed the selected account.");
    Check(oauth.Refreshes == 0 && oauth.Logins == 0, "Scope configuration contacted OAuth.");
    await Expect("oauth_configuration_in_use", () => service.ConfigureOAuthAsync(originalClient));
    _ = await service.GetTokenAsync();
    Check(oauth.Refreshes == 1, "Saved account refresh no longer worked after native scope reduction.");
}
async Task CancelledWaiter()
{
    var account = Account(expired: true); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    await using var service = Service(new(new() { Accounts = [account], PrimaryAccountId = account.Id, SelectedAccountId = account.Id }), oauth);
    using var cancellation = new CancellationTokenSource();
    var cancelled = service.GetTokenAsync(cancellation.Token);
    var continuing = service.GetTokenAsync();
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancellation.Cancel();
    try { await cancelled; throw new Exception("Cancelled waiter completed."); }
    catch (OperationCanceledException) { }
    oauth.ReleaseRefresh.TrySetResult();
    Check((await continuing).AccessToken == "synthetic-refreshed-secret" && oauth.Refreshes == 1, "A cancelled waiter interrupted the shared refresh.");
}
async Task CancelledOnlyWaiter()
{
    var account = Account(expired: true); var oauth = new FakeOAuth(now) { DelayRefresh = true };
    var clock = now;
    var store = new MemoryStore(new() { Accounts = [account], PrimaryAccountId = account.Id, SelectedAccountId = account.Id });
    await using var service = new AccountService(store, oauth, _ => { }, () => clock);
    using var cancellation = new CancellationTokenSource();
    var cancelled = service.GetTokenAsync(cancellation.Token);
    await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancellation.Cancel();
    try { await cancelled; throw new Exception("Cancelled waiter completed."); }
    catch (OperationCanceledException) { }
    oauth.ReleaseRefresh.TrySetResult();
    for (var i = 0; i < 100 && (await store.ReadAsync()).Accounts[0].Token.AccessToken != "synthetic-refreshed-secret"; i++) await Task.Delay(5);
    clock = now.AddHours(2);
    _ = await service.GetTokenAsync();
    Check(oauth.Refreshes == 2, "A completed abandoned refresh returned its stale token instead of refreshing again.");
}
async Task StdioBoundaries()
{
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    using var process = StartBroker(directory);
    try
    {
        var lines = new[]
        {
            "{\"id\":1,\"method\":\"getStatus\",\"params\":{}}",
            "{\"id\":2,\"method\":\"beginLogin\",\"params\":{\"tokenFile\":\"not-a-real-file\"}}",
            "{\"id\":3,\"method\":\"getStatus\",\"method\":\"shutdown\",\"params\":{}}",
            "{\"id\":true,\"method\":\"getStatus\",\"params\":{}}",
            "{\"id\":5,\"method\":\"listAccounts\",\"params\":{}}",
            "{\"id\":6,\"method\":\"shutdown\",\"params\":{}}"
        };
        foreach (var line in lines) await process.StandardInput.WriteLineAsync(line);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var responses = (await outputTask).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x)).ToArray();
        try
        {
            Check(process.ExitCode == 0 && responses.Length == 6, "Stdio process did not return all responses.");
            Check(!responses[0].RootElement.GetProperty("result").GetProperty("configured").GetBoolean(), "Unexpected OAuth configuration.");
            Check(responses[1].RootElement.GetProperty("error").GetProperty("code").GetString() == "unsupported_parameter", "Credential import path was accepted.");
            Check(responses[2].RootElement.GetProperty("error").GetProperty("code").GetString() == "unsupported_parameter", "Duplicate method was accepted.");
            Check(responses[3].RootElement.GetProperty("error").GetProperty("code").GetString() == "invalid_request_id", "Boolean request ID was accepted.");
            Check(!Directory.Exists(directory), "Read-only requests created or opened an account directory.");
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
    finally
    {
        if (!process.HasExited) process.Kill(true);
        CleanupSyntheticDirectory(directory);
    }
}
async Task StdioDpapiAccounts()
{
    if (!OperatingSystem.IsWindows()) return;
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    Process? process = null;
    try
    {
        var account = Account();
        account.Token = account.Token with { Expiry = DateTimeOffset.UtcNow.AddHours(1) };
        account.Client = account.Client with { Scopes = OAuthScopes.Default.ToArray() };
        var store = new EncryptedAccountStore(directory);
        await store.UpdateAsync(s => { s.OAuth = account.Client; s.Accounts.Add(account); s.PrimaryAccountId = account.Id; s.SelectedAccountId = account.Id; });
        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "accounts.v1.dpapi"));
        Check(!Encoding.UTF8.GetString(protectedBytes).Contains("synthetic-access-secret", StringComparison.Ordinal), "The real store wrote an unprotected token.");
        process = StartBroker(directory);
        var requests = new object[]
        {
            new { id = 1, method = "configureOAuth", @params = new { clientId = account.Client.ClientId, clientSecret = account.Client.ClientSecret, scopes = OAuthScopes.Default.Reverse().ToArray() } },
            new { id = 2, method = "configureOAuth", @params = new { clientId = "different.apps.googleusercontent.com" } },
            new { id = 3, method = "listAccounts", @params = new { } },
            new { id = 4, method = "getToken", @params = new { } },
            new { id = 5, method = "selectAccount", @params = new { id = account.Id } },
            new { id = 6, method = "getStatus", @params = new { } },
            new { id = 7, method = "shutdown", @params = new { } }
        };
        foreach (var request in requests) await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var responses = (await outputTask).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x)).ToArray();
        try
        {
            Check(process.ExitCode == 0 && responses.Length == 7 && string.IsNullOrEmpty(await errorTask), "The real broker process failed.");
            Check(responses[0].RootElement.GetProperty("result").GetProperty("configured").GetBoolean(), "Equivalent persisted scope arrays were rejected.");
            Check(responses[1].RootElement.GetProperty("error").GetProperty("code").GetString() == "oauth_configuration_in_use", "Existing account client was changed.");
            Check(!responses[2].RootElement.GetProperty("result").GetRawText().Contains("secret", StringComparison.Ordinal), "Public list leaked credentials.");
            Check(responses[3].RootElement.GetProperty("result").GetProperty("accessToken").GetString() == account.Token.AccessToken, "DPAPI account could not be read through stdio.");
            Check(responses[4].RootElement.GetProperty("error").GetProperty("code").GetString() == "selection_locked", "Token use did not freeze identity.");
            Check(responses[5].RootElement.GetProperty("result").GetProperty("selectionLocked").GetBoolean(), "Locked state was not reported.");
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Check((await store.ReadAsync()).OAuth!.ClientId == account.Client.ClientId, "Rejected configuration changed encrypted state.");
    }
    finally
    {
        if (process is not null) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
        CleanupSyntheticDirectory(directory);
    }
}
static Process StartBroker(string directory)
{
    var assembly = typeof(AccountToken).Assembly.Location;
    var start = new ProcessStartInfo(Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null))
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
    start.ArgumentList.Add("--stdio"); start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(directory);
    return Process.Start(start)!;
}
async Task StdioRuntimeSwitch()
{
    if (!OperatingSystem.IsWindows()) return;
    var directory = Path.Combine(Path.GetTempPath(), "ag-accounts-test-" + Guid.NewGuid().ToString("N"));
    Process? process = null;
    try
    {
        var a = Account(); var b = Account();
        a.Token = a.Token with { Expiry = DateTimeOffset.UtcNow.AddHours(1) };
        b.Token = b.Token with { AccessToken = "synthetic-attached-token", Expiry = DateTimeOffset.UtcNow.AddHours(1) };
        var store = new EncryptedAccountStore(directory);
        await store.UpdateAsync(s => { s.OAuth = a.Client; s.Accounts = [a, b]; });
        process = StartBroker(directory);
        async Task<JsonDocument> Send(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = method, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            return JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        }
        using var primary = await Send("setPrimaryAccount", new { id = a.Id });
        using var before = await Send("getToken", new { });
        var first = before.RootElement.GetProperty("result");
        Check(first.GetProperty("accountId").GetGuid() == a.Id && first.GetProperty("generation").GetInt64() == 1, "The stdio token omitted committed identity metadata.");
        using var begun = await Send("beginSwitch", new { targetId = b.Id, expectedGeneration = 1 });
        var ticket = begun.RootElement.GetProperty("result").GetProperty("ticket").GetGuid();
        using var blocked = await Send("getToken", new { });
        Check(blocked.RootElement.GetProperty("error").GetProperty("code").GetString() == "switch_in_progress", "The stdio broker provided a token while a switch was pending.");
        using var committed = await Send("commitSwitch", new { ticket });
        using var after = await Send("getToken", new { });
        var selected = after.RootElement.GetProperty("result");
        Check(selected.GetProperty("accountId").GetGuid() == b.Id && selected.GetProperty("generation").GetInt64() == 2 &&
            selected.GetProperty("accessToken").GetString() == "synthetic-attached-token", "The same stdio process did not provide the committed attached token.");
        using var status = await Send("getStatus", new { });
        Check(status.RootElement.GetProperty("result").GetProperty("primaryAccountId").GetGuid() == a.Id &&
            !status.RootElement.GetProperty("result").GetRawText().Contains("secret", StringComparison.Ordinal), "Runtime selection changed or exposed primary credentials.");
        using var shutdown = await Send("shutdown", new { });
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(process.ExitCode == 0 && string.IsNullOrEmpty(await process.StandardError.ReadToEndAsync()), "The stdio switch process failed.");
    }
    finally
    {
        if (process is not null) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
        CleanupSyntheticDirectory(directory);
    }
}
static void CleanupSyntheticDirectory(string directory)
{
    var absolute = Path.GetFullPath(directory);
    var expectedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ag-accounts-test-");
    if (!absolute.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) || absolute[expectedPrefix.Length..].Contains(Path.DirectorySeparatorChar))
        throw new Exception("Synthetic test directory cleanup was refused.");
    if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
}

sealed class MemoryStore(SavedState? initial = null) : IAccountStore
{
    private readonly SavedState _state = initial ?? new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    public Action? Updated;
    public Task<SavedState> ReadAsync(CancellationToken token = default) => Task.FromResult(_state);
    public Task UpdateAsync(Action<SavedState> update, CancellationToken token = default) { lock (_state) update(_state); Updated?.Invoke(); return Task.CompletedTask; }
    public async Task<IAsyncDisposable> LockRefreshAsync(Guid id, CancellationToken token = default) { await _refresh.WaitAsync(token); return new Lease(_refresh); }
    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
}
sealed class PausingTokenReadStore(SavedState state) : IAccountStore
{
    private readonly MemoryStore _store = new(state);
    private int _reads;
    public TaskCompletionSource TokenReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseTokenRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<SavedState> ReadAsync(CancellationToken token = default)
    {
        if (Interlocked.Increment(ref _reads) == 2)
        {
            TokenReadStarted.TrySetResult();
            await ReleaseTokenRead.Task.WaitAsync(token);
        }
        return await _store.ReadAsync(token);
    }
    public Task UpdateAsync(Action<SavedState> update, CancellationToken token = default) => _store.UpdateAsync(update, token);
    public Task<IAsyncDisposable> LockRefreshAsync(Guid id, CancellationToken token = default) => _store.LockRefreshAsync(id, token);
}
sealed class FakeOAuth(DateTimeOffset now) : IGoogleOAuth
{
    public int Logins; public int Refreshes; public int BrowserOpens;
    public bool InvalidGrant; public bool DelayRefresh; public bool TemporaryFailure;
    public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<OAuthLoginResult> LoginAsync(OAuthClient client, Action<Uri> openBrowser, CancellationToken token)
    {
        Interlocked.Increment(ref Logins); openBrowser(new("https://accounts.google.com/o/oauth2/v2/auth"));
        return Task.FromResult(new OAuthLoginResult("synthetic-subject", "synthetic@example.invalid", new("synthetic-login-access", "Bearer", "synthetic-login-refresh", now.AddHours(1))));
    }
    public async Task<AccountToken> RefreshAsync(OAuthClient client, AccountToken current, CancellationToken token)
    {
        Interlocked.Increment(ref Refreshes); RefreshStarted.TrySetResult();
        if (DelayRefresh) await ReleaseRefresh.Task.WaitAsync(token);
        if (InvalidGrant) throw new AccountOperationException("account_reauth_required", "Synthetic invalid grant.");
        if (TemporaryFailure) throw new AccountOperationException("oauth_request_failed", "Synthetic temporary refresh failure.");
        return current with { AccessToken = "synthetic-refreshed-secret", Expiry = now.AddHours(1) };
    }
}
sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Uris { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uris.Add(request.RequestUri!); return Task.FromResult(respond(request));
    }
}
sealed class LoginHandler : HttpMessageHandler
{
    public string? Challenge; public string? Redirect; public int Calls; public bool OmitRefresh;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token" && request.Method == HttpMethod.Post)
        {
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (form["client_id"] != "synthetic.apps.googleusercontent.com" || form["client_secret"] != "synthetic-client-secret" ||
                form["code"] != "synthetic-code" || form["grant_type"] != "authorization_code" || form["redirect_uri"] != Redirect || computed != Challenge)
                throw new Exception("The token exchange did not bind the callback and PKCE verifier.");
            return new(HttpStatusCode.OK) { Content = new StringContent(OmitRefresh
                ? "{\"access_token\":\"synthetic-login-access\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"
                : "{\"access_token\":\"synthetic-login-access\",\"refresh_token\":\"synthetic-login-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}") };
        }
        if (request.RequestUri.AbsoluteUri == "https://openidconnect.googleapis.com/v1/userinfo" && request.Method == HttpMethod.Get &&
            request.Headers.Authorization?.ToString() == "Bearer synthetic-login-access")
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"sub\":\"synthetic-subject\",\"email\":\"synthetic@example.invalid\"}") };
        throw new Exception("An unexpected OAuth request was attempted.");
    }
}
