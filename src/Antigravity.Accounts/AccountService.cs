using System.Collections.Concurrent;

namespace Antigravity.Accounts;

internal sealed class AccountService : IAsyncDisposable
{
    private readonly IAccountStore _store;
    private readonly IGoogleOAuth _oauth;
    private readonly Action<Uri> _openBrowser;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AccountToken>>> _refreshes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _loginGate = new();
    private LoginStatus _login = new("idle");
    private readonly List<Task> _loginTasks = [];
    private CancellationTokenSource? _loginCancellation;
    private bool _selectionLocked;
    private bool _tokenProvided;
    private Guid? _lockedAccount;
    private long _lockedGeneration;
    private int _tokenRequests;
    private PendingSwitch? _pendingSwitch;

    private sealed class PendingSwitch(Guid target, long generation)
    {
        public Guid Ticket { get; } = Guid.NewGuid();
        public Guid Target { get; } = target;
        public long Generation { get; } = generation;
        public AccountToken? Token { get; set; }
        public PendingSwitchStatus Status => new(Ticket, Target, Generation, Token is null ? "preparing" : "ready");
    }

    public AccountService(IAccountStore store, IGoogleOAuth oauth, Action<Uri> openBrowser, Func<DateTimeOffset>? now = null)
    {
        _store = store; _oauth = oauth; _openBrowser = openBrowser; _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AccountStatus> GetStatusAsync(CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            var state = await _store.ReadAsync(token);
            var selected = _selectionLocked ? _lockedAccount : state.SelectedAccountId;
            LoginStatus login;
            lock (_loginGate) login = _login;
            return new(state.OAuth is not null, _selectionLocked, selected, login,
                selected is { } id && state.Accounts.FirstOrDefault(a => a.Id == id)?.Disabled == true,
                state.PrimaryAccountId, _selectionLocked ? _lockedGeneration : state.Generation, _pendingSwitch?.Status);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task<IReadOnlyList<AccountSummary>> ListAccountsAsync(CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            var state = await _store.ReadAsync(token);
            var selected = _selectionLocked ? _lockedAccount : state.SelectedAccountId;
            return state.Accounts.Select(a => new AccountSummary(a.Id, a.Label, a.Email, selected == a.Id,
                state.PrimaryAccountId is null ? "unassigned" : state.PrimaryAccountId == a.Id ? "primary" : "attached",
                state.PrimaryAccountId, a.Disabled)).ToArray();
        }
        finally { _selectionGate.Release(); }
    }

    public async Task ConfigureOAuthAsync(OAuthClient client, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(client.ClientId) || client.ClientId.Length > 512 ||
            !client.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) ||
            client.ClientId.Any(char.IsWhiteSpace) || client.ClientSecret?.Length > 4096 || client.ClientSecret?.Any(char.IsControl) == true)
            throw new AccountOperationException("invalid_oauth_client", "A Google desktop OAuth client ID is required.");
        client = client with { Scopes = OAuthScopes.Normalize(client.Scopes) };
        await _selectionGate.WaitAsync(token);
        try
        {
            var current = await _store.ReadAsync(token);
            if (current.OAuth is { } existing && SameClient(existing, client)) return;
            RequireUnlocked();
            await _store.UpdateAsync(s =>
            {
                // Host updates may reduce scopes for future sign-ins. Preserve
                // every saved account's client and refresh token; changing the
                // client or expanding scopes still requires explicit login.
                if (s.Accounts.Count != 0 && !(s.OAuth is { } previous &&
                    previous.ClientId == client.ClientId && (previous.ClientSecret ?? "") == (client.ClientSecret ?? "") &&
                    OAuthScopes.Normalize(client.Scopes).All(scope => OAuthScopes.Normalize(previous.Scopes).Contains(scope, StringComparer.Ordinal))))
                    throw new AccountOperationException("oauth_configuration_in_use", "Remove existing independent accounts before changing their OAuth client configuration.");
                s.OAuth = client;
            }, token);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task BeginLoginAsync(string? label, CancellationToken token = default)
    {
        label = ValidateLabel(label);
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireNoSwitch();
            var client = (await _store.ReadAsync(token)).OAuth ??
                throw new AccountOperationException("oauth_not_configured", "Configure an independent OAuth client before adding an account.");
            lock (_loginGate)
            {
                if (_login.State == "pending") throw new AccountOperationException("login_pending", "An account sign-in is already pending.");
                _login = new("pending");
                _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loginTasks.RemoveAll(task => task.IsCompleted);
                _loginTasks.Add(RunLoginAsync(client, label, _loginCancellation));
            }
        }
        finally { _selectionGate.Release(); }
    }

    public async Task<bool> CancelLoginAsync(CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            lock (_loginGate)
            {
                if (_login.State != "pending" || _loginCancellation is not { } pending) return false;
                _loginCancellation = null;
                _login = new("cancelled", "Account sign-in was cancelled. Saved accounts have not changed.");
                pending.Cancel();
                return true;
            }
        }
        finally { _selectionGate.Release(); }
    }

    private void FinishLogin(CancellationTokenSource attempt, LoginStatus status)
    {
        lock (_loginGate)
        {
            if (_loginCancellation != attempt) return;
            _loginCancellation = null;
            _login = status;
        }
    }

    private async Task RunLoginAsync(OAuthClient client, string? label, CancellationTokenSource attempt)
    {
        // Yield before calling the browser so beginLogin can return and status/shutdown remain usable.
        await Task.Yield();
        try
        {
            attempt.Token.ThrowIfCancellationRequested();
            var result = await _oauth.LoginAsync(client, uri =>
            {
                attempt.Token.ThrowIfCancellationRequested();
                _openBrowser(uri);
            }, attempt.Token).WaitAsync(attempt.Token);
            await _selectionGate.WaitAsync(attempt.Token);
            try
            {
                attempt.Token.ThrowIfCancellationRequested();
                await _store.UpdateAsync(s =>
                {
                    if (s.OAuth is not { } configured || !SameClient(configured, client))
                        throw new AccountOperationException("oauth_configuration_changed", "OAuth configuration changed during sign-in; start sign-in again.");
                    var existing = s.Accounts.FirstOrDefault(a => a.Subject == result.Subject);
                    RequireNoSwitch();
                    if (existing is null && s.Accounts.Count >= SavedState.MaximumAccounts)
                        throw new AccountOperationException("account_limit_reached", "Remove an unused account before adding another account; the account limit is 100.");
                    if (existing is not null && _selectionLocked && existing.Id == _lockedAccount)
                        throw new AccountOperationException("account_locked", "Switch away from the active quota account before replacing its authorization.");
                    var account = existing ?? new SavedAccount { Id = Guid.NewGuid(), Subject = result.Subject };
                    account.Label = label ?? (string.IsNullOrWhiteSpace(result.Email) ? "Google account" : result.Email);
                    account.Email = result.Email;
                    account.Client = client;
                    account.Token = result.Token;
                    account.Disabled = false;
                    if (existing is null) s.Accounts.Add(account);
                    // Explicit selection is required even after the first successful sign-in.
                }, attempt.Token);
                // Cancellation shares this gate with persistence: once saved,
                // a later cancel cannot report that the completed login was cancelled.
                FinishLogin(attempt, new("succeeded"));
            }
            finally { _selectionGate.Release(); }
        }
        catch (OperationCanceledException) { FinishLogin(attempt, new("failed", "Account sign-in was cancelled or timed out.")); }
        catch (AccountOperationException error) { FinishLogin(attempt, new("failed", error.Message)); }
        catch { FinishLogin(attempt, new("failed", "Account sign-in could not be completed.")); }
        finally { lock (_loginGate) attempt.Dispose(); }
    }

    public async Task SelectAccountAsync(Guid id, CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireUnlocked();
            await _store.UpdateAsync(s =>
            {
                RequirePrimary(s);
                var account = s.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
                if (account.Disabled) throw NeedsLogin();
                if (s.SelectedAccountId != id) { s.SelectedAccountId = id; s.Generation = checked(s.Generation + 1); }
            }, token);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task SetPrimaryAccountAsync(Guid id, CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireUnlocked();
            await _store.UpdateAsync(s =>
            {
                if (s.PrimaryAccountId is { } primary)
                {
                    if (primary == id) return;
                    throw new AccountOperationException("primary_account_already_set", "The primary account is already set; attached accounts cannot replace it.");
                }
                var account = s.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
                if (account.Disabled) throw NeedsLogin();
                s.PrimaryAccountId = id;
                s.SelectedAccountId = id;
                s.Generation = checked(s.Generation + 1);
            }, token);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task<SwitchTicket> BeginSwitchAsync(Guid targetId, long expectedGeneration, CancellationToken token = default)
    {
        PendingSwitch pending;
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireNoSwitch();
            lock (_loginGate)
                if (_login.State == "pending") throw new AccountOperationException("login_pending", "Finish the pending account sign-in before switching accounts.");
            if (_tokenRequests != 0)
                throw new AccountOperationException("token_request_in_progress", "Wait for active token requests before switching accounts.");
            var state = await _store.ReadAsync(token);
            RequirePrimary(state);
            if (expectedGeneration < 0 || expectedGeneration != CurrentGeneration(state)) throw SelectionChanged();
            if (_selectionLocked && (state.Generation != _lockedGeneration || state.SelectedAccountId != _lockedAccount)) throw SelectionChanged();
            var account = state.Accounts.FirstOrDefault(a => a.Id == targetId) ?? throw NotFound();
            if (account.Disabled) throw NeedsLogin();
            _pendingSwitch = pending = new(targetId, expectedGeneration);
        }
        finally { _selectionGate.Release(); }
        try
        {
            var prepared = await GetAccountTokenAsync(targetId, token);
            await _selectionGate.WaitAsync(token);
            try
            {
                if (_pendingSwitch != pending) throw new AccountOperationException("switch_cancelled", "The account switch was cancelled.");
                pending.Token = prepared;
                return new(pending.Ticket, pending.Target, pending.Generation);
            }
            finally { _selectionGate.Release(); }
        }
        catch
        {
            await _selectionGate.WaitAsync(CancellationToken.None);
            try { if (_pendingSwitch == pending) _pendingSwitch = null; }
            finally { _selectionGate.Release(); }
            throw;
        }
    }

    public async Task<SwitchCommit> CommitSwitchAsync(Guid ticket, CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            var pending = RequireTicket(ticket);
            if (pending.Token is null) throw new AccountOperationException("switch_not_ready", "The target account authorization is still being prepared.");
            if (!Usable(pending.Token))
            {
                _pendingSwitch = null;
                throw new AccountOperationException("switch_token_expired", "The prepared account authorization expired; the previous account is still selected.");
            }
            long generation = 0;
            await _store.UpdateAsync(s =>
            {
                RequirePrimary(s);
                if (s.Generation != pending.Generation || _selectionLocked && s.SelectedAccountId != _lockedAccount) throw SelectionChanged();
                var account = s.Accounts.FirstOrDefault(a => a.Id == pending.Target) ?? throw NotFound();
                if (account.Disabled) throw NeedsLogin();
                s.SelectedAccountId = pending.Target;
                s.Generation = generation = checked(s.Generation + 1);
            }, token);
            if (_selectionLocked) { _lockedAccount = pending.Target; _lockedGeneration = generation; }
            _pendingSwitch = null;
            return new(pending.Target, generation);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task CancelSwitchAsync(Guid ticket, CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try { _ = RequireTicket(ticket); _pendingSwitch = null; }
        finally { _selectionGate.Release(); }
    }

    public async Task RenameAccountAsync(Guid id, string? label, CancellationToken token = default)
    {
        label = ValidateLabel(label);
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireNoSwitch();
            await _store.UpdateAsync(s =>
            {
                var account = s.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
                account.Label = label ?? (string.IsNullOrWhiteSpace(account.Email) ? "Google account" : account.Email);
            }, token);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task RemoveAccountAsync(Guid id, CancellationToken token = default)
    {
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireNoSwitch();
            if (_selectionLocked && _lockedAccount == id)
                throw new AccountOperationException("account_locked", "Switch away from the active quota account before removing it.");
            await _store.UpdateAsync(s =>
            {
                if (s.PrimaryAccountId == id)
                {
                    if (_selectionLocked) throw new AccountOperationException("account_locked", "The primary account cannot be removed while the language service is running.");
                    if (s.Accounts.Any(a => a.Id != id)) throw new AccountOperationException("primary_has_attached_accounts", "Remove attached accounts before removing the primary account.");
                    s.PrimaryAccountId = null;
                }
                if (s.Accounts.RemoveAll(a => a.Id == id) == 0) throw NotFound();
                if (s.SelectedAccountId == id) { s.SelectedAccountId = null; s.Generation = checked(s.Generation + 1); }
            }, token);
        }
        finally { _selectionGate.Release(); }
    }

    public async Task<AccountToken> GetTokenAsync(CancellationToken token = default)
    {
        var result = await GetTokenResponseAsync(token);
        return new(result.AccessToken, result.TokenType, result.RefreshToken, result.Expiry, result.IsGcpTos);
    }

    public async Task<AccountTokenResponse> GetTokenResponseAsync(CancellationToken token = default)
    {
        Guid id;
        long generation;
        await _selectionGate.WaitAsync(token);
        try
        {
            RequireNoSwitch();
            var state = await _store.ReadAsync(token);
            id = (_selectionLocked ? _lockedAccount : state.SelectedAccountId) ??
                throw new AccountOperationException("account_not_selected", "Select an independently authorized account before starting the language service.");
            RequirePrimary(state);
            var account = state.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
            if (account.Disabled) throw NeedsLogin();
            if (!_selectionLocked) { _lockedAccount = id; _lockedGeneration = state.Generation; }
            _selectionLocked = true;
            generation = _lockedGeneration;
            _tokenRequests++;
        }
        finally { _selectionGate.Release(); }
        var provided = false;
        try
        {
            var result = await GetAccountTokenAsync(id, token);
            provided = true;
            return new(result.AccessToken, result.TokenType, result.RefreshToken, result.Expiry, result.IsGcpTos, id, generation);
        }
        finally
        {
            await _selectionGate.WaitAsync(CancellationToken.None);
            try
            {
                if (provided) _tokenProvided = true;
                _tokenRequests--;
                // A failed initial refresh must leave startup account selection
                // usable. Once a token has reached the host, keep that identity
                // locked even when a later refresh fails.
                if (_tokenRequests == 0 && !_tokenProvided)
                {
                    _selectionLocked = false;
                    _lockedAccount = null;
                    _lockedGeneration = 0;
                }
            }
            finally { _selectionGate.Release(); }
        }
    }

    private async Task<AccountToken> GetAccountTokenAsync(Guid id, CancellationToken token)
    {
        var state = await _store.ReadAsync(token);
        var account = state.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
        if (account.Disabled) throw NeedsLogin();
        if (Usable(account.Token)) return account.Token;
        Lazy<Task<AccountToken>>? candidate = null;
        candidate = new(() => RefreshAndRemoveAsync(id, candidate!), LazyThreadSafetyMode.ExecutionAndPublication);
        var refresh = _refreshes.GetOrAdd(id, candidate);
        return await refresh.Value.WaitAsync(token);
    }

    private async Task<AccountToken> RefreshAndRemoveAsync(Guid id, Lazy<Task<AccountToken>> entry)
    {
        try { return await RefreshAsync(id); }
        finally { _refreshes.TryRemove(new KeyValuePair<Guid, Lazy<Task<AccountToken>>>(id, entry)); }
    }

    private async Task<AccountToken> RefreshAsync(Guid id)
    {
        await using var refreshLock = await _store.LockRefreshAsync(id, _lifetime.Token);
        var state = await _store.ReadAsync(_lifetime.Token);
        var account = state.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
        if (account.Disabled) throw NeedsLogin();
        if (Usable(account.Token)) return account.Token;
        var originalToken = account.Token;
        try
        {
            var refreshed = await _oauth.RefreshAsync(account.Client, originalToken, _lifetime.Token);
            var supplied = refreshed;
            await _store.UpdateAsync(s =>
            {
                var current = s.Accounts.FirstOrDefault(a => a.Id == id) ?? throw NotFound();
                // All initial waiters may have cancelled while this shared
                // refresh continued. Keep a later explicit authorization.
                if (current.Token == originalToken) current.Token = refreshed;
                else supplied = current.Token;
            }, _lifetime.Token);
            return supplied;
        }
        catch (AccountOperationException error) when (error.Code == "account_reauth_required")
        {
            AccountToken? replacement = null;
            await _store.UpdateAsync(s =>
            {
                var current = s.Accounts.FirstOrDefault(a => a.Id == id);
                if (current is null) return;
                if (current.Token == originalToken) current.Disabled = true;
                else if (!current.Disabled && Usable(current.Token)) replacement = current.Token;
            }, _lifetime.Token);
            if (replacement is not null) return replacement;
            throw;
        }
    }

    private bool Usable(AccountToken token) => !string.IsNullOrWhiteSpace(token.AccessToken) && token.Expiry > _now().AddSeconds(60);
    private static bool SameClient(OAuthClient left, OAuthClient right) => left.ClientId == right.ClientId &&
        (left.ClientSecret ?? "") == (right.ClientSecret ?? "") &&
        OAuthScopes.Normalize(left.Scopes).SequenceEqual(OAuthScopes.Normalize(right.Scopes), StringComparer.Ordinal);
    private void RequireUnlocked()
    {
        RequireNoSwitch();
        if (_selectionLocked) throw new AccountOperationException("selection_locked", "A running language service can change quota accounts only through a host-controlled switch transaction.");
    }
    private void RequireNoSwitch()
    {
        if (_pendingSwitch is not null) throw new AccountOperationException("switch_in_progress", "An account switch is pending; no account token can be used until it is committed or cancelled.");
    }
    private PendingSwitch RequireTicket(Guid ticket) => _pendingSwitch is { } pending && pending.Ticket == ticket ? pending :
        throw new AccountOperationException("invalid_switch_ticket", "The switch ticket is not active.");
    private long CurrentGeneration(SavedState state) => _selectionLocked ? _lockedGeneration : state.Generation;
    private static void RequirePrimary(SavedState state)
    {
        if (state.PrimaryAccountId is null) throw new AccountOperationException("primary_account_not_set", "Explicitly choose an independently authorized primary account first.");
    }
    private static AccountOperationException SelectionChanged() => new("selection_changed", "The selected account generation changed; read current status before switching.");
    private static string? ValidateLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var label = value.Trim();
        if (label.Length > 80 || label.Any(char.IsControl)) throw new AccountOperationException("invalid_label", "Account labels must contain at most 80 printable characters.");
        return label;
    }
    private static AccountOperationException NotFound() => new("account_not_found", "Account was not found.");
    private static AccountOperationException NeedsLogin() => new("account_reauth_required", "This account needs an explicit sign-in; no other account was selected.");

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task[] pending;
        lock (_loginGate) pending = _loginTasks.ToArray();
        await Task.WhenAll(pending);
        try { await Task.WhenAll(_refreshes.Values.Where(x => x.IsValueCreated).Select(x => x.Value)); } catch { }
        _lifetime.Dispose();
        _selectionGate.Dispose();
    }
}
