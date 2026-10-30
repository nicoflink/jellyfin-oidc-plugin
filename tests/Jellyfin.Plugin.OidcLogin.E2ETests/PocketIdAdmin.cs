using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Seeds PocketID through its REST API, authenticated with the <c>STATIC_API_KEY</c>.
/// </summary>
internal sealed class PocketIdAdmin : IDisposable
{
    private readonly HttpClient _http;

    public PocketIdAdmin(Uri baseAddress, string staticApiKey)
    {
        _http = new HttpClient { BaseAddress = baseAddress };
        _http.DefaultRequestHeaders.Add("X-API-Key", staticApiKey);
    }

    public async Task<string> CreateGroupAsync(string name, CancellationToken cancellationToken)
    {
        var group = await SendAsync(HttpMethod.Post, "api/user-groups", new { name, friendlyName = name }, cancellationToken);
        return group.GetProperty("id").GetString()!;
    }

    public async Task CreateUserAsync(PocketIdUser user, CancellationToken cancellationToken)
    {
        await SendAsync(
            HttpMethod.Post,
            "api/users",
            new
            {
                id = user.Id,
                username = user.Username,
                email = $"{user.Username}@example.test",
                emailVerified = true,
                firstName = user.Username,
                lastName = "Test",
                displayName = user.Username,
                isAdmin = false,
            },
            cancellationToken);
    }

    public async Task SetUserGroupsAsync(string userId, string[] groupIds, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Put, $"api/users/{userId}/user-groups", new { userGroupIds = groupIds }, cancellationToken);
    }

    /// <summary>
    /// Creates a confidential client with PKCE enforced, consent skipped, and access restricted to <paramref name="allowedGroupIds"/>.
    /// </summary>
    public async Task CreateOidcClientAsync(string clientId, string clientSecret, Uri callbackUrl, string[] allowedGroupIds, CancellationToken cancellationToken)
    {
        await SendAsync(
            HttpMethod.Post,
            "api/oidc/clients",
            new
            {
                id = clientId,
                name = clientId,
                callbackURLs = new[] { callbackUrl.ToString() },
                isPublic = false,
                pkceEnabled = true,
                skipConsent = true,
                isGroupRestricted = true,
            },
            cancellationToken);
        await SendAsync(HttpMethod.Post, $"api/oidc/clients/{clientId}/secrets", new { secret = clientSecret }, cancellationToken);
        await SendAsync(HttpMethod.Put, $"api/oidc/clients/{clientId}/allowed-user-groups", new { userGroupIds = allowedGroupIds }, cancellationToken);
    }

    /// <summary>
    /// Creates a one-time access token; opening <c>/lc/&lt;token&gt;</c> in a browser signs the user in.
    /// </summary>
    /// <remarks>
    /// The TTL must exceed 15 minutes: shorter TTLs yield a 6-character code, which PocketID's login page
    /// won't auto-submit unless unauthenticated email login is enabled.
    /// </remarks>
    public async Task<string> CreateOneTimeAccessTokenAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await SendAsync(HttpMethod.Post, $"api/users/{userId}/one-time-access-token", new { ttl = "1h" }, cancellationToken);
        return token.GetProperty("token").GetString()!;
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"PocketID {method} {path} returned {(int)response.StatusCode}: {content}");
        }

        return string.IsNullOrEmpty(content) ? default : JsonDocument.Parse(content).RootElement.Clone();
    }
}
