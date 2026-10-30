using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Talks to the Jellyfin under test over its HTTP API.
/// </summary>
internal sealed class JellyfinServer : IDisposable
{
    private const string ClientAuthorization = "MediaBrowser Client=\"OidcLogin E2E\", Device=\"E2E\", DeviceId=\"oidclogin-e2e\", Version=\"1.0.0\"";

    private readonly HttpClient _http;

    public JellyfinServer(Uri baseAddress)
    {
        _http = new HttpClient { BaseAddress = baseAddress };
    }

    /// <summary>
    /// Waits until a fresh Jellyfin is ready for its first-run wizard. Jellyfin starts in two phases, and
    /// <c>/health</c> and <c>/System/Info/Public</c> flap between 200 and 503 in between; the wizard's own
    /// <c>/Startup/User</c> only answers once the final phase is up.
    /// </summary>
    public async Task WaitUntilStartupWizardIsReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync(new Uri("Startup/User", UriKind.Relative), timeoutSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeoutSource.Token);
        }
    }

    /// <summary>
    /// Completes the first-run wizard through the <c>/Startup/*</c> API, creating the setup admin.
    /// </summary>
    public async Task CompleteStartupWizardAsync(string adminName, string adminPassword, CancellationToken cancellationToken)
    {
        await PostAsync("Startup/Configuration", new { UICulture = "en-US", MetadataCountryCode = "US", PreferredMetadataLanguage = "en" }, cancellationToken);
        await PostAsync("Startup/User", new { Name = adminName, Password = adminPassword }, cancellationToken);
        await PostAsync("Startup/RemoteAccess", new { EnableRemoteAccess = true }, cancellationToken);
        await PostAsync("Startup/Complete", null, cancellationToken);
    }

    public async Task<string> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        var session = await PostAsync("Users/AuthenticateByName", new { Username = username, Pw = password }, cancellationToken);
        return session.GetProperty("AccessToken").GetString()!;
    }

    public async Task<JsonElement> GetAsync(string path, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{accessToken}\"");
        return await SendAsync(request, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Authorization", ClientAuthorization);
        return await SendAsync(request, cancellationToken);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Jellyfin {request.Method} {request.RequestUri} returned {(int)response.StatusCode}: {content}");
        }

        return string.IsNullOrEmpty(content) ? default : JsonDocument.Parse(content).RootElement.Clone();
    }
}
