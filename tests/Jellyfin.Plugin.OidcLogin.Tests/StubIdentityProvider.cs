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
/// An Identity Provider behind a stub HTTP handler, serving discovery, a JWKS with an RSA test key and one EC test key
/// per curve, and a token endpoint that answers with <see cref="IdToken"/>.
/// </summary>
internal sealed class StubIdentityProvider : HttpMessageHandler
{
    public const string Issuer = "https://id.example.test";

    public static readonly Uri DiscoveryUrl = new(Issuer + "/.well-known/openid-configuration");

    public static readonly Uri AuthorizationEndpoint = new(Issuer + "/authorize");

    public static readonly Uri TokenEndpoint = new(Issuer + "/token");

    public static readonly Uri JwksUri = new(Issuer + "/jwks");

    private readonly ECDsaSecurityKey[] _ecKeys =
    [
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "ec-p256" },
        new(ECDsa.Create(ECCurve.NamedCurves.nistP384)) { KeyId = "ec-p384" },
        new(ECDsa.Create(ECCurve.NamedCurves.nistP521)) { KeyId = "ec-p521" },
    ];

    private RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };

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
    /// Gets or sets the discovery <c>scopes_supported</c>; left out when <c>null</c>.
    /// </summary>
    public string[]? ScopesSupported { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether discovery advertises <c>authorization_response_iss_parameter_supported</c>.
    /// </summary>
    public bool AdvertisesIssParameter { get; set; }

    /// <summary>
    /// Gets or sets the <c>issuer</c> discovery claims.
    /// </summary>
    public string DiscoveryIssuer { get; set; } = Issuer;

    /// <summary>
    /// Gets or sets the status discovery answers with.
    /// </summary>
    public HttpStatusCode DiscoveryStatus { get; set; } = HttpStatusCode.OK;

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
        => withUnpublishedKey
            ? Sign(claims, new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = _signingKey.KeyId }, SecurityAlgorithms.RsaSha256))
            : Sign(claims, SecurityAlgorithms.RsaSha256);

    /// <summary>
    /// Signs <paramref name="claims"/> with <paramref name="algorithm"/> (an RS, PS or ES algorithm), using the
    /// published key that fits it.
    /// </summary>
    public string Sign(IDictionary<string, object> claims, string algorithm)
    {
        SecurityKey key = algorithm switch
        {
            SecurityAlgorithms.EcdsaSha256 => _ecKeys[0],
            SecurityAlgorithms.EcdsaSha384 => _ecKeys[1],
            SecurityAlgorithms.EcdsaSha512 => _ecKeys[2],
            _ => _signingKey,
        };
        return Sign(claims, new SigningCredentials(key, algorithm));
    }

    /// <summary>
    /// Signs <paramref name="claims"/> with any credentials, published or not.
    /// </summary>
    public static string Sign(IDictionary<string, object> claims, SigningCredentials credentials)
        => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = credentials,
        });

    /// <summary>
    /// Replaces the RSA signing key with a new one under a new <c>kid</c>, as an Identity Provider rotating its key.
    /// </summary>
    public void RotateSigningKey()
    {
        var old = _signingKey;
        _signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rotated-key" };
        old.Rsa.Dispose();
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

        if (request.RequestUri == DiscoveryUrl && DiscoveryStatus != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(DiscoveryStatus);
        }

        var body = request.RequestUri switch
        {
            var u when u == DiscoveryUrl => $$"""
                {
                  "issuer": "{{DiscoveryIssuer}}",
                  {{(ScopesSupported is null ? string.Empty : $"\"scopes_supported\": {JsonSerializer.Serialize(ScopesSupported)},")}}
                  {{(AdvertisesIssParameter ? "\"authorization_response_iss_parameter_supported\": true," : string.Empty)}}
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
            foreach (var key in _ecKeys)
            {
                key.ECDsa.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private string Jwks()
    {
        var parameters = _signingKey.Rsa.ExportParameters(includePrivateParameters: false);
        var keys = new List<object>
        {
            new { kty = "RSA", use = "sig", kid = _signingKey.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
        };
        foreach (var key in _ecKeys)
        {
            var point = key.ECDsa.ExportParameters(includePrivateParameters: false);
            var curve = key.ECDsa.KeySize switch { 256 => "P-256", 384 => "P-384", _ => "P-521" };
            keys.Add(new { kty = "EC", use = "sig", kid = key.KeyId, crv = curve, x = Base64UrlEncoder.Encode(point.Q.X), y = Base64UrlEncoder.Encode(point.Q.Y) });
        }

        return JsonSerializer.Serialize(new { keys });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record TokenRequest(string? Authorization, IReadOnlyDictionary<string, string> Form);
}
