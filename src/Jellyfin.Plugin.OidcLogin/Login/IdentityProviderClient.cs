using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Talks to the Identity Provider. Discovery is fetched lazily from the Issuer URL and cached.
/// </summary>
public sealed class IdentityProviderClient
{
    private static readonly TimeSpan DiscoveryRefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonWebTokenHandler IdTokenHandler = new() { MapInboundClaims = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Lock _lock = new();
    private Discovery? _discovery;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityProviderClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Creates the HTTP clients used to reach the Identity Provider.</param>
    public IdentityProviderClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the Identity Provider's discovery document, fetching it on first use.
    /// </summary>
    /// <param name="issuer">The configured Issuer URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The discovery document, with its signing keys.</returns>
    /// <exception cref="InvalidOperationException">Discovery could not be fetched.</exception>
    public Task<OpenIdConnectConfiguration> GetDiscoveryAsync(Uri issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        lock (_lock)
        {
            // A changed Issuer URL starts over with a fresh cache.
            if (_discovery?.Issuer != issuer)
            {
                var metadataAddress = issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration";
                var retriever = new FactoryDocumentRetriever(_httpClientFactory, requireHttps: !issuer.IsLoopback);
                _discovery = new Discovery(
                    issuer,
                    new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever)
                    {
                        AutomaticRefreshInterval = DiscoveryRefreshInterval,
                    });
            }

            return _discovery.Manager.GetConfigurationAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Exchanges an authorization code for an ID token at the token endpoint, authenticating with
    /// <c>client_secret_basic</c> and proving the login with the PKCE verifier.
    /// </summary>
    /// <param name="tokenEndpoint">The discovery <c>token_endpoint</c>.</param>
    /// <param name="redemption">The code, verifier and client credentials.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The raw ID token.</returns>
    /// <exception cref="HttpRequestException">The token endpoint couldn't be reached.</exception>
    /// <exception cref="IdentityProviderException">The token endpoint rejected the code or answered without an ID token.</exception>
    public async Task<string> RedeemCodeAsync(Uri tokenEndpoint, CodeRedemption redemption, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redemption);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = redemption.Code,
                ["redirect_uri"] = redemption.RedirectUri,
                ["code_verifier"] = redemption.CodeVerifier,
            }),
        };

        // RFC 6749 section 2.3.1: both parts are form-encoded before being joined and base64-encoded.
        var credentials = Uri.EscapeDataString(redemption.ClientId) + ":" + Uri.EscapeDataString(redemption.ClientSecret);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new IdentityProviderException($"The token endpoint rejected the code with status {(int)response.StatusCode} and error {await TokenErrorAsync(response, cancellationToken).ConfigureAwait(false)}.");
        }

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (body.RootElement.TryGetProperty("id_token", out var idToken) && idToken.ValueKind == JsonValueKind.String)
            {
                return idToken.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Reported below, like a missing id_token.
        }

        throw new IdentityProviderException("The token endpoint answered without an id_token.");
    }

    /// <summary>
    /// Validates an ID token: signature against the JWKS, <c>iss</c> equal to the discovery issuer, <c>aud</c>
    /// containing the client id, <c>exp</c>, and the <c>nonce</c> from Start.
    /// </summary>
    /// <param name="idToken">The raw ID token.</param>
    /// <param name="discovery">The discovery document, with its signing keys.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="nonce">The nonce the ID token must echo.</param>
    /// <param name="clock">The clock.</param>
    /// <returns>The validated token, whose claims are the only ones the Plugin reads.</returns>
    /// <exception cref="IdentityProviderException">The ID token is invalid.</exception>
    public static async Task<JsonWebToken> ValidateIdTokenAsync(string idToken, OpenIdConnectConfiguration discovery, string clientId, string nonce, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var result = await IdTokenHandler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = discovery.SigningKeys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,

            // Against the injected clock, which this library version takes no other way.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return expires is { } exp && now < exp + ClockSkew && (notBefore is not { } nbf || nbf - ClockSkew <= now);
            },
        }).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new IdentityProviderException($"The ID token is invalid: {result.Exception?.Message}");
        }

        var token = (JsonWebToken)result.SecurityToken;

        if (!token.TryGetPayloadValue<string>("nonce", out var tokenNonce) || !string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
        {
            throw new IdentityProviderException("The ID token's nonce doesn't match this login.");
        }

        return token;
    }

    /// <summary>
    /// The <c>error</c> of a token error response (RFC 6749 section 5.2), sanitised for the log.
    /// </summary>
    private static async Task<string> TokenErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return LogText.Quote(error.GetString()!, 64);
            }
        }
        catch (JsonException)
        {
            // Reported as unknown, like a body without an error.
        }

        return "unknown";
    }

    private sealed record Discovery(Uri Issuer, ConfigurationManager<OpenIdConnectConfiguration> Manager);

    /// <summary>
    /// Fetches each document with a fresh client from the factory, so Jellyfin's handler rotation applies.
    /// </summary>
    private sealed class FactoryDocumentRetriever(IHttpClientFactory httpClientFactory, bool requireHttps) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            using var httpClient = httpClientFactory.CreateClient(NamedClient.Default);
            return await new HttpDocumentRetriever(httpClient) { RequireHttps = requireHttps }
                .GetDocumentAsync(address, cancel)
                .ConfigureAwait(false);
        }
    }
}
