using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// An Identity Provider behind a stub HTTP handler, serving discovery, a JWKS with one RSA test key, and a token
/// endpoint that answers with <see cref="IdToken"/>.
/// </summary>
internal sealed class StubIdentityProvider : HttpMessageHandler
{
    public const string Issuer = "https://id.example.test";

    public static readonly Uri DiscoveryUrl = new(Issuer + "/.well-known/openid-configuration");

    public static readonly Uri AuthorizationEndpoint = new(Issuer + "/authorize");

    public static readonly Uri TokenEndpoint = new(Issuer + "/token");

    public static readonly Uri JwksUri = new(Issuer + "/jwks");

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public ConcurrentQueue<Uri> Requests { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether every request fails as if the Identity Provider were unreachable.
    /// </summary>
    public bool IsDown { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether discovery leaves out <c>authorization_endpoint</c>.
    /// </summary>
    public bool OmitAuthorizationEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the ID token the token endpoint returns.
    /// </summary>
    public string? IdToken { get; set; }

    /// <summary>
    /// Gets or sets the status the token endpoint answers with.
    /// </summary>
    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>
    /// Gets the last token request: its form fields and <c>Authorization</c> header.
    /// </summary>
    public TokenRequest? LastTokenRequest { get; private set; }

    /// <summary>
    /// Signs <paramref name="claims"/> into an ID token with the key the JWKS publishes, or with a key it doesn't.
    /// </summary>
    public string Sign(IDictionary<string, object> claims, bool withUnpublishedKey = false)
    {
        var key = withUnpublishedKey ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" } : _signingKey;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        if (IsDown)
        {
            throw new HttpRequestException("Connection refused");
        }

        if (request.RequestUri == TokenEndpoint && request.Method == HttpMethod.Post)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastTokenRequest = new TokenRequest(request.Headers.Authorization?.ToString(), form.ToDictionary(p => p.Key, p => p.Value.ToString()));
            var tokenBody = TokenStatus == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new Dictionary<string, string?> { ["access_token"] = "access-token", ["token_type"] = "Bearer", ["id_token"] = IdToken })
                : """{ "error": "invalid_grant" }""";
            return Json(TokenStatus, tokenBody);
        }

        var body = request.RequestUri switch
        {
            var u when u == DiscoveryUrl => $$"""
                {
                  "issuer": "{{Issuer}}",
                  {{(OmitAuthorizationEndpoint ? string.Empty : $"\"authorization_endpoint\": \"{AuthorizationEndpoint}\",")}}
                  "token_endpoint": "{{TokenEndpoint}}",
                  "jwks_uri": "{{JwksUri}}",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """,
            var u when u == JwksUri => Jwks(),
            _ => null,
        };

        return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, body);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _signingKey.Rsa.Dispose();
        }

        base.Dispose(disposing);
    }

    private string Jwks()
    {
        var parameters = _signingKey.Rsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = _signingKey.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
            },
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record TokenRequest(string? Authorization, IReadOnlyDictionary<string, string> Form);
}
