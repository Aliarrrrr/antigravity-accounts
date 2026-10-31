using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Antigravity.Accounts;

public sealed record OAuthProof(string State, string Verifier, string Challenge)
{
    public static OAuthProof Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new(Base64Url(RandomNumberGenerator.GetBytes(32)), verifier,
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public string ValidateCallback(string? state, string? code, string? error)
    {
        if (state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(State), Encoding.UTF8.GetBytes(state)))
            throw new AccountOperationException("oauth_state_mismatch", "OAuth callback state did not match.");
        if (!string.IsNullOrEmpty(error)) throw new AccountOperationException("oauth_declined", "Google authorization was not completed.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 8192)
            throw new AccountOperationException("oauth_code_missing", "OAuth callback did not contain an authorization code.");
        return code;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GoogleOAuth : IGoogleOAuth, IDisposable
{
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private static readonly Uri UserInfoEndpoint = new("https://openidconnect.googleapis.com/v1/userinfo");
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;

    public GoogleOAuth(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<OAuthLoginResult> LoginAsync(OAuthClient client, Action<Uri> openBrowser, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var proof = OAuthProof.Create();
        using var listener = CreateListener();
        var prefix = listener.Prefixes.Single();
        var redirect = prefix + "oauth/callback";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = string.Join(' ', OAuthScopes.Normalize(client.Scopes)),
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = proof.State,
            ["code_challenge"] = proof.Challenge, ["code_challenge_method"] = "S256"
        };
        var authorization = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" +
            string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        openBrowser(authorization);
        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
            if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/oauth/callback")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            try
            {
                code = proof.ValidateCallback(context.Request.QueryString["state"], context.Request.QueryString["code"], context.Request.QueryString["error"]);
                await ReplyAsync(context.Response, 200, "Authorization received. You can return to Antigravity.", deadline.Token);
                break;
            }
            catch
            {
                await ReplyAsync(context.Response, 400, "Authorization callback was rejected. Return to Antigravity and try again.", CancellationToken.None);
                throw;
            }
        }
        listener.Stop();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["code"] = code, ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code", ["code_verifier"] = proof.Verifier
        };
        AddSecret(form, client);
        var issued = await ExchangeAsync(form, null, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.AccessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new AccountOperationException("oauth_identity_failed", "Google account identity could not be confirmed.");
        using var identity = await ReadJsonAsync(response, deadline.Token);
        var subject = StringValue(identity.RootElement, "sub");
        if (string.IsNullOrWhiteSpace(subject)) throw new AccountOperationException("oauth_identity_failed", "Google account identity could not be confirmed.");
        return new(subject, StringValue(identity.RootElement, "email") ?? "", issued);
    }

    public Task<AccountToken> RefreshAsync(OAuthClient client, AccountToken current, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
            throw new AccountOperationException("account_reauth_required", "This account needs an explicit sign-in.");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token"
        };
        AddSecret(form, client);
        return ExchangeAsync(form, current, token);
    }

    private async Task<AccountToken> ExchangeAsync(Dictionary<string, string> form, AccountToken? previous, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AccountOperationException("account_reauth_required", "Google rejected this account authorization; sign in explicitly again.");
        using var document = await ReadJsonAsync(response, token);
        var value = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || StringValue(value, "error") == "invalid_grant")
                throw new AccountOperationException("account_reauth_required", "Google rejected this account authorization; sign in explicitly again.");
            throw new AccountOperationException("oauth_request_failed", "Google authorization failed; no other account was tried.");
        }
        var access = StringValue(value, "access_token");
        var type = StringValue(value, "token_type") ?? "Bearer";
        if (string.IsNullOrWhiteSpace(access) || !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !value.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds < 1 || seconds > 86400)
            throw new AccountOperationException("oauth_response_invalid", "Google returned an unsupported authorization response.");
        var refresh = StringValue(value, "refresh_token");
        if (string.IsNullOrWhiteSpace(refresh)) refresh = previous?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refresh))
            throw new AccountOperationException("oauth_refresh_token_missing", "Google did not return offline authorization. Sign in explicitly again and grant the requested access.");
        return new(access, "Bearer", refresh,
            _now().AddSeconds(seconds), previous?.IsGcpTos ?? false);
    }

    private static void AddSecret(Dictionary<string, string> form, OAuthClient client)
    {
        if (!string.IsNullOrEmpty(client.ClientSecret)) form["client_secret"] = client.ClientSecret;
    }

    private static string? StringValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            if (output.Length + count > 65536) throw new AccountOperationException("oauth_response_invalid", "Google authorization response was too large.");
            output.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(output.ToArray()); }
        catch (JsonException) { throw new AccountOperationException("oauth_response_invalid", "Google returned an unsupported authorization response."); }
    }

    private static HttpListener CreateListener()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var picker = new TcpListener(IPAddress.Loopback, 0);
            picker.Start();
            var port = ((IPEndPoint)picker.LocalEndpoint).Port;
            picker.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); return listener; }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new AccountOperationException("oauth_callback_unavailable", "The local OAuth callback could not be started.");
    }

    private static async Task ReplyAsync(HttpListenerResponse response, int status, string text, CancellationToken token)
    {
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        try { await response.OutputStream.WriteAsync(bytes, token); }
        finally { response.Close(); }
    }

    public void Dispose() => _http.Dispose();
}
