using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>An HTTP answer as the caller saw it: status and the raw body (the sink "GraphQL error answers").</summary>
internal sealed record StackAnswer(HttpStatusCode Status, string Body)
{
    public JsonNode? Json => Body.Length > 0 && Body.TrimStart().StartsWith('{') ? JsonNode.Parse(Body) : null;

    /// <summary><c>errors[0].extensions.code</c>, or the HTTP status when there is no GraphQL error.</summary>
    public string Outcome
    {
        get
        {
            if (Status != HttpStatusCode.OK)
            {
                return "HTTP " + (int)Status;
            }

            var code = Json?["errors"]?[0]?["extensions"]?["code"]?.GetValue<string>();
            return code ?? (Json?["errors"] is JsonArray { Count: > 0 } ? "GRAPHQL_ERROR" : "SUCCESS");
        }
    }
}

/// <summary>Talks to the local identity service (tokens, a client without roles) and the GraphQL API of the asset repository.</summary>
internal sealed class StackClient : IDisposable
{
    private readonly ScanSettings _settings;
    private readonly HttpClient _http;

    public StackClient(ScanSettings settings)
    {
        _settings = settings;
        // The local stack serves the self-signed development certificate.
        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A client-credentials token of <paramref name="clientId" /> for the scan tenant.</summary>
    public async Task<string> GetTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "octo_api",
            ["acr_values"] = "tenant:" + _settings.TenantId
        });
        using var response = await _http.PostAsync(new Uri(_settings.IdentityUri, "connect/token"), content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // never echo the body of a token response: it may carry the token
            throw new InvalidOperationException($"Token request for client '{clientId}' failed with HTTP {(int)response.StatusCode}");
        }

        return JsonNode.Parse(body)!["access_token"]!.GetValue<string>();
    }

    /// <summary>Creates a client-credentials client without any role in the scan tenant (the refused caller).</summary>
    public async Task CreateClientWithoutRolesAsync(string adminToken, string clientId, string clientSecret, CancellationToken ct)
    {
        var dto = new JsonObject
        {
            ["isEnabled"] = true,
            ["clientId"] = clientId,
            ["clientName"] = "AB#7118 canary scan caller without roles",
            ["clientSecret"] = clientSecret,
            ["requireClientSecret"] = true,
            ["allowedGrantTypes"] = new JsonArray("client_credentials"),
            ["allowedScopes"] = new JsonArray("octo_api")
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_settings.IdentityUri, $"{_settings.TenantId}/v1/clients"))
        {
            Content = new StringContent(dto.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Creating client '{clientId}' failed with HTTP {(int)response.StatusCode}");
        }
    }

    public async Task DeleteClientAsync(string adminToken, string clientId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            new Uri(_settings.IdentityUri, $"{_settings.TenantId}/v1/clients/{Uri.EscapeDataString(clientId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _http.SendAsync(request, ct);
    }

    /// <summary>Posts one GraphQL request with variables (as a client application does) and returns the raw answer.</summary>
    public async Task<StackAnswer> GraphQlAsync(string? bearer, string query, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables ?? new JsonObject() };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_settings.AssetRepositoryUri, $"tenants/{_settings.TenantId}/graphQl"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        // the WWW-Authenticate header of a 401 is part of the answer the caller sees
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        return new StackAnswer(response.StatusCode, challenge.Length > 0 ? text + "\nWWW-Authenticate: " + challenge : text);
    }

    /// <summary>All events of the tenant created at or after <paramref name="sinceUtc" />, as raw JSON (the sink "audit events").</summary>
    public async Task<string> GetEventsSinceAsync(string adminToken, DateTime sinceUtc, CancellationToken ct)
    {
        const string query = """
            query($after: String) { runtime { systemNotificationEvent(first: 500, after: $after) {
              pageInfo { hasNextPage endCursor }
              items { rtId rtCreationDateTime message level source category details { key value } } } } }
            """;
        var collected = new JsonArray();
        string? after = null;
        for (var page = 0; page < 20; page++)
        {
            var answer = await GraphQlAsync(adminToken, query, new JsonObject { ["after"] = after }, ct);
            var connection = answer.Json?["data"]?["runtime"]?["systemNotificationEvent"]
                             ?? throw new InvalidOperationException("Reading the events failed: " + answer.Outcome);
            foreach (var item in connection["items"]!.AsArray())
            {
                var created = item!["rtCreationDateTime"]?.GetValue<DateTime>();
                if (created == null || created.Value.ToUniversalTime() >= sinceUtc.AddSeconds(-1))
                {
                    collected.Add(item.DeepClone());
                }
            }

            if (connection["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true)
            {
                break;
            }

            after = connection["pageInfo"]!["endCursor"]!.GetValue<string>();
        }

        return collected.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
