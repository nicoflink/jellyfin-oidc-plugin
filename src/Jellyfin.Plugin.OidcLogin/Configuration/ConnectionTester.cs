using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Jellyfin.Plugin.OidcLogin.Configuration;

/// <summary>
/// Test connection: fetches discovery from an Issuer URL, bypassing the login flow's cache, and reports whether
/// sign-in could work with it.
/// </summary>
public sealed class ConnectionTester
{
    private const string GroupsScope = "groups";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionTester"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Creates the HTTP clients used to reach the Identity Provider.</param>
    public ConnectionTester(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Tests the connection to the Identity Provider at <paramref name="issuerUrl"/>.
    /// </summary>
    /// <param name="issuerUrl">The Issuer URL as the admin entered it, possibly unsaved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<ConnectionTestResult> TestAsync(string issuerUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuerUrl);
        var issuer = issuerUrl.Trim();
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || (issuerUri.Scheme != Uri.UriSchemeHttps && issuerUri.Scheme != Uri.UriSchemeHttp))
        {
            return Failed("Enter the Issuer URL as an absolute https URL, such as https://id.example.com.");
        }

        if (!IssuerUrl.IsAllowed(issuerUri))
        {
            return Failed("The Issuer URL must use https; plain http is only allowed for loopback hosts.");
        }

        var discoveryUrl = IssuerUrl.Discovery(issuerUri);
        OpenIdConnectConfiguration discovery;
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await httpClient.GetAsync(discoveryUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"Discovery at {discoveryUrl} answered with HTTP status {(int)response.StatusCode}. Check the Issuer URL.");
            }

            discovery = OpenIdConnectConfiguration.Create(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException e)
        {
            return Failed($"Couldn't reach {discoveryUrl}: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"Couldn't reach {discoveryUrl}: the request timed out.");
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return Failed($"{discoveryUrl} didn't answer with a discovery document.");
        }

        if (!string.Equals(discovery.Issuer, issuer, StringComparison.Ordinal))
        {
            return Failed($"Discovery names the issuer \"{discovery.Issuer}\", which must equal the Issuer URL exactly.");
        }

        var missing = new[]
        {
            ("authorization_endpoint", discovery.AuthorizationEndpoint),
            ("token_endpoint", discovery.TokenEndpoint),
            ("jwks_uri", discovery.JwksUri),
        }.Where(e => string.IsNullOrEmpty(e.Item2)).Select(e => e.Item1).ToArray();
        if (missing.Length > 0)
        {
            return Failed($"Discovery is missing {string.Join(", ", missing)}.");
        }

        string[] warnings = discovery.ScopesSupported.Contains(GroupsScope, StringComparer.Ordinal)
            ? []
            : [$"The Identity Provider doesn't offer the \"{GroupsScope}\" scope, so Required Group and Admin Group won't work."];
        return new ConnectionTestResult(true, $"Connected to {discovery.Issuer}.", warnings);
    }

    private static ConnectionTestResult Failed(string message) => new(false, message, []);
}
