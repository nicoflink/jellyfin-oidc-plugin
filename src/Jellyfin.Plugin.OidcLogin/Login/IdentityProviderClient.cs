using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
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

    /// <summary>
    /// How often an unknown <c>kid</c> may re-fetch discovery and the JWKS, so forged tokens can't hammer the
    /// Identity Provider.
    /// </summary>
    private static readonly TimeSpan UnknownKeyRefetchInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonWebTokenHandler IdTokenHandler = new() { MapInboundClaims = false };

    /// <summary>
    /// The only signature algorithms an ID token may use. EdDSA is left out on purpose (see the spec).
    /// </summary>
    private static readonly string[] SignatureAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Lock _lock = new();
    private Discovery? _discovery;
    private DateTimeOffset? _lastUnknownKeyRefetch;

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
    /// <exception cref="IdentityProviderException">Discovery's <c>issuer</c> isn't exactly the Issuer URL.</exception>
    public Task<OpenIdConnectConfiguration> GetDiscoveryAsync(Uri issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        Task<OpenIdConnectConfiguration> fetch;
        lock (_lock)
        {
            // A changed Issuer URL starts over with a fresh cache.
            if (_discovery?.Issuer != issuer)
            {
                _discovery = NewDiscovery(issuer);
                _lastUnknownKeyRefetch = null;
            }

            fetch = _discovery.Manager.GetConfigurationAsync(cancellationToken);
        }

        return RequireIssuerAsync(fetch, issuer);
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
    /// Validates an ID token: an RS256/384/512 or ES256/384/512 signature against the JWKS, <c>iss</c> equal to the
    /// discovery issuer, <c>aud</c> containing the client id (and <c>azp</c> equal to it when there are several),
    /// <c>exp</c> and <c>iat</c> with 2 minutes skew, and the <c>nonce</c> from Start.
    /// </summary>
    /// <remarks>
    /// A <c>kid</c> the cached JWKS doesn't hold re-fetches discovery and the JWKS once, at most every 5 minutes, so a
    /// rotated key works at once.
    /// </remarks>
    /// <param name="idToken">The raw ID token.</param>
    /// <param name="issuer">The configured Issuer URL, which the JWKS is re-fetched from.</param>
    /// <param name="discovery">The discovery document, with its signing keys.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="nonce">The nonce the ID token must echo.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validated token, whose claims are the only ones the Plugin reads.</returns>
    /// <exception cref="IdentityProviderException">The ID token is invalid.</exception>
    /// <exception cref="InvalidOperationException">The JWKS could not be re-fetched.</exception>
    public async Task<JsonWebToken> ValidateIdTokenAsync(string idToken, Uri issuer, OpenIdConnectConfiguration discovery, string clientId, string nonce, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(clock);
        var header = ReadHeader(idToken);
        if (header?.Alg == "EdDSA")
        {
            throw new IdentityProviderException("The ID token is signed with EdDSA, which OIDC Login doesn't support. Rotate the Identity Provider's signing key to RS256 or ES256.");
        }

        if (header?.Kid is { } kid && !discovery.SigningKeys.Any(k => k.KeyId == kid)
            && RefetchForUnknownKey(issuer, clock, cancellationToken) is { } refetch)
        {
            discovery = await refetch.ConfigureAwait(false);
        }

        var result = await IdTokenHandler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidAlgorithms = SignatureAlgorithms,
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

        // OIDC Core 3.1.3.7 steps 4 and 5: several audiences need azp, and an azp must name this client.
        var hasAzp = token.TryGetPayloadValue<string>("azp", out var azp);
        if ((hasAzp || token.Audiences.Skip(1).Any()) && !string.Equals(azp, clientId, StringComparison.Ordinal))
        {
            throw new IdentityProviderException("The ID token has several audiences or an azp, but its azp isn't the client id.");
        }

        if (!token.TryGetPayloadValue<long>("iat", out var issuedAt)
            || DateTimeOffset.FromUnixTimeSeconds(issuedAt) > clock.GetUtcNow() + ClockSkew)
        {
            throw new IdentityProviderException("The ID token has no iat, or one in the future.");
        }

        return token;
    }

    /// <summary>
    /// The ID token read without validation, for its header, or <c>null</c> when it isn't a readable JWS.
    /// </summary>
    private static JsonWebToken? ReadHeader(string idToken)
    {
        try
        {
            return new JsonWebToken(idToken);
        }
        catch (ArgumentException)
        {
            // Malformed: validation reports it.
            return null;
        }
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

    /// <summary>
    /// Replaces the cached discovery with a freshly fetched one, unless that happened less than 5 minutes ago.
    /// </summary>
    /// <returns>The fetch of the fresh discovery, or <c>null</c> when it was re-fetched too recently.</returns>
    private Task<OpenIdConnectConfiguration>? RefetchForUnknownKey(Uri issuer, TimeProvider clock, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (_discovery?.Issuer == issuer && _lastUnknownKeyRefetch is { } last && now < last + UnknownKeyRefetchInterval)
            {
                return null;
            }

            // A new manager fetches before answering; RequestRefresh on the old one would only refresh in the background.
            _discovery = NewDiscovery(issuer);
            _lastUnknownKeyRefetch = now;
            return RequireIssuerAsync(_discovery.Manager.GetConfigurationAsync(cancellationToken), issuer);
        }
    }

    /// <summary>
    /// OIDC Discovery 4.3: the document's <c>issuer</c> must be exactly the URL it was fetched from, or it could
    /// choose the issuer ID tokens are validated against.
    /// </summary>
    private static async Task<OpenIdConnectConfiguration> RequireIssuerAsync(Task<OpenIdConnectConfiguration> fetch, Uri issuer)
    {
        var discovery = await fetch.ConfigureAwait(false);
        if (!string.Equals(discovery.Issuer, issuer.OriginalString, StringComparison.Ordinal))
        {
            throw new IdentityProviderException($"Discovery from {issuer.OriginalString} names the issuer {LogText.Quote(discovery.Issuer ?? string.Empty, 256)}, which must equal the Issuer URL exactly.");
        }

        return discovery;
    }

    private Discovery NewDiscovery(Uri issuer)
    {
        var metadataAddress = IssuerUrl.Discovery(issuer).ToString();
        var retriever = new FactoryDocumentRetriever(_httpClientFactory, requireHttps: !issuer.IsLoopback);
        return new Discovery(
            issuer,
            new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever)
            {
                AutomaticRefreshInterval = DiscoveryRefreshInterval,
            });
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
