using System.Diagnostics;
using System.Text.Json;

namespace Antigravity.Accounts;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--stdio") args = args[1..];
        string root;
        if (args.Length == 0)
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityAccounts");
        else if (args.Length == 2 && args[0] == "--data-root" && Path.IsPathFullyQualified(args[1])) root = args[1];
        else
        {
            Console.Error.WriteLine("Usage: Antigravity.Accounts [--stdio] [--data-root <independent absolute directory>]");
            return 2;
        }
        using var oauth = new GoogleOAuth();
        await using var service = new AccountService(new EncryptedAccountStore(root), oauth,
            uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        while (await Console.In.ReadLineAsync() is { } line)
        {
            JsonElement? id = null;
            var shutdown = false;
            object response;
            try
            {
                if (line.Length > 65536) throw new AccountOperationException("invalid_request", "Request is too large.");
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                Allowed(request, "id", "method", "params");
                if (!request.TryGetProperty("id", out var requestId) || requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    throw new AccountOperationException("invalid_request_id", "Request ID must be a string or number.");
                id = requestId.Clone();
                var method = Text(request, "method", true)!;
                var parameters = request.TryGetProperty("params", out var supplied) ? supplied : EmptyObject();
                var result = await DispatchAsync(service, method, parameters);
                shutdown = method == "shutdown";
                response = new { id, result };
            }
            catch (AccountOperationException error) { response = new { id, error = new { code = error.Code, message = error.Message } }; }
            catch (JsonException) { response = new { id, error = new { code = "invalid_request", message = "Request must be a JSON object with supported fields." } }; }
            catch (OperationCanceledException) { response = new { id, error = new { code = "operation_cancelled", message = "The account operation was cancelled or timed out." } }; }
            catch { response = new { id, error = new { code = "account_operation_failed", message = "The local account operation failed. No other account was selected." } }; }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            await Console.Out.FlushAsync();
            if (shutdown) break;
        }
        return 0;
    }

    internal static async Task<object> DispatchAsync(AccountService service, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "getStatus": Allowed(parameters); return await service.GetStatusAsync();
            case "listAccounts": Allowed(parameters); return await service.ListAccountsAsync();
            case "configureOAuth":
                Allowed(parameters, "clientId", "clientSecret", "scopes");
                await service.ConfigureOAuthAsync(new(Text(parameters, "clientId", true)!, Text(parameters, "clientSecret"), Scopes(parameters)));
                return new { configured = true };
            case "beginLogin":
                Allowed(parameters, "label");
                await service.BeginLoginAsync(Text(parameters, "label"));
                return new { started = true };
            case "cancelLogin":
                Allowed(parameters);
                return new { cancelled = await service.CancelLoginAsync() };
            case "getToken": Allowed(parameters); return await service.GetTokenResponseAsync();
            case "setPrimaryAccount":
                Allowed(parameters, "id");
                await service.SetPrimaryAccountAsync(Identifier(parameters));
                return new { primaryAccountSet = true };
            case "beginSwitch":
                Allowed(parameters, "targetId", "expectedGeneration");
                return await service.BeginSwitchAsync(Identifier(parameters, "targetId"), Generation(parameters));
            case "commitSwitch":
                Allowed(parameters, "ticket");
                return await service.CommitSwitchAsync(Identifier(parameters, "ticket"));
            case "cancelSwitch":
                Allowed(parameters, "ticket");
                await service.CancelSwitchAsync(Identifier(parameters, "ticket"));
                return new { cancelled = true };
            case "selectAccount":
                Allowed(parameters, "id");
                await service.SelectAccountAsync(Identifier(parameters));
                return new { selected = true };
            case "removeAccount":
                Allowed(parameters, "id");
                await service.RemoveAccountAsync(Identifier(parameters));
                return new { removed = true };
            case "renameAccount":
                Allowed(parameters, "id", "label");
                await service.RenameAccountAsync(Identifier(parameters), Text(parameters, "label", true));
                return new { renamed = true };
            case "shutdown": Allowed(parameters); return new { stopped = true };
            default: throw new AccountOperationException("method_not_supported", "This component only manages independently authorized accounts; importing official credentials is not supported.");
        }
    }

    private static void Allowed(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)) ||
            value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new AccountOperationException("unsupported_parameter", "Request contains unsupported fields. Credential paths and token imports are not accepted.");
    }
    private static string? Text(JsonElement value, string name, bool required = false)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            if (required) throw new AccountOperationException("invalid_request", "A required request field is missing.");
            return null;
        }
        if (field.ValueKind == JsonValueKind.Null && !required) return null;
        if (field.ValueKind != JsonValueKind.String) throw new AccountOperationException("invalid_request", "A request field has an unsupported type.");
        return field.GetString();
    }
    private static Guid Identifier(JsonElement value, string name = "id") => Guid.TryParse(Text(value, name, true), out var id) && id != Guid.Empty
        ? id : throw new AccountOperationException("invalid_account_id", "A valid account ID is required.");
    private static long Generation(JsonElement value) => value.TryGetProperty("expectedGeneration", out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var generation) && generation >= 0 ? generation :
        throw new AccountOperationException("invalid_generation", "A non-negative selected account generation is required.");
    private static string[]? Scopes(JsonElement value)
    {
        if (!value.TryGetProperty("scopes", out var field)) return null;
        if (field.ValueKind != JsonValueKind.Array || field.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new AccountOperationException("invalid_oauth_scopes", "OAuth scopes must be a supported string array.");
        return field.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });
}
