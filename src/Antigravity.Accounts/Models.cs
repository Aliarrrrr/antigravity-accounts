namespace Antigravity.Accounts;

public sealed class AccountOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record OAuthClient(string ClientId, string? ClientSecret = null, string[]? Scopes = null);

public static class OAuthScopes
{
    public static readonly string[] Default =
    [
        "openid", "https://www.googleapis.com/auth/cloud-platform", "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile", "https://www.googleapis.com/auth/cclog",
        "https://www.googleapis.com/auth/experimentsandconfigs"
    ];
    public static readonly string[] Legacy = [.. Default, "https://www.googleapis.com/auth/aicode"];

    public static string[] Normalize(string[]? scopes)
    {
        var selected = scopes ?? Default;
        if (!new[] { Default, Legacy }.Any(allowed => selected.Length == allowed.Length &&
                selected.Distinct(StringComparer.Ordinal).Count() == allowed.Length && selected.All(s => allowed.Contains(s, StringComparer.Ordinal))))
            throw new AccountOperationException("invalid_oauth_scopes", "OAuth scopes must match a reviewed native client scope set.");
        return selected.Order(StringComparer.Ordinal).ToArray();
    }
}
public sealed record AccountToken(string AccessToken, string TokenType, string RefreshToken,
    DateTimeOffset Expiry, bool IsGcpTos = false);
public sealed record OAuthLoginResult(string Subject, string Email, AccountToken Token);
public sealed record AccountSummary(Guid Id, string Label, string Email, bool Selected,
    string Role, Guid? PrimaryAccountId, bool Disabled);
public sealed record LoginStatus(string State, string? Message = null);
public sealed record AccountStatus(bool Configured, bool SelectionLocked, Guid? SelectedAccountId,
    LoginStatus Login, bool SelectedAccountDisabled, Guid? PrimaryAccountId, long Generation,
    PendingSwitchStatus? PendingSwitch);
public sealed record PendingSwitchStatus(Guid Ticket, Guid TargetAccountId, long ExpectedGeneration, string State);
public sealed record SwitchTicket(Guid Ticket, Guid TargetAccountId, long ExpectedGeneration);
public sealed record SwitchCommit(Guid SelectedAccountId, long Generation);
public sealed record AccountTokenResponse(string AccessToken, string TokenType, string RefreshToken,
    DateTimeOffset Expiry, bool IsGcpTos, Guid AccountId, long Generation);

internal sealed class SavedAccount
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public OAuthClient Client { get; set; } = new("");
    public AccountToken Token { get; set; } = new("", "Bearer", "", DateTimeOffset.MinValue);
    public bool Disabled { get; set; }
}

internal sealed class SavedState
{
    public int SchemaVersion { get; set; } = 2;
    public OAuthClient? OAuth { get; set; }
    public Guid? PrimaryAccountId { get; set; }
    public Guid? SelectedAccountId { get; set; }
    public long Generation { get; set; }
    public List<SavedAccount> Accounts { get; set; } = [];
}

public interface IGoogleOAuth
{
    Task<OAuthLoginResult> LoginAsync(OAuthClient client, Action<Uri> openBrowser, CancellationToken token);
    Task<AccountToken> RefreshAsync(OAuthClient client, AccountToken current, CancellationToken token);
}

internal interface IAccountStore
{
    Task<SavedState> ReadAsync(CancellationToken token = default);
    Task UpdateAsync(Action<SavedState> update, CancellationToken token = default);
    Task<IAsyncDisposable> LockRefreshAsync(Guid id, CancellationToken token = default);
}
