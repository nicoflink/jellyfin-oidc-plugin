using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// An Identity Provider behind a stub HTTP handler, serving discovery and an empty JWKS.
/// </summary>
internal sealed class StubIdentityProvider : HttpMessageHandler
{
    public const string Issuer = "https://id.example.test";

    public static readonly Uri DiscoveryUrl = new(Issuer + "/.well-known/openid-configuration");

    public static readonly Uri AuthorizationEndpoint = new(Issuer + "/authorize");

    public ConcurrentQueue<Uri> Requests { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether every request fails as if the Identity Provider were unreachable.
    /// </summary>
    public bool IsDown { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether discovery leaves out <c>authorization_endpoint</c>.
    /// </summary>
    public bool OmitAuthorizationEndpoint { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        if (IsDown)
        {
            throw new HttpRequestException("Connection refused");
        }

        var body = request.RequestUri switch
        {
            var u when u == DiscoveryUrl => $$"""
                {
                  "issuer": "{{Issuer}}",
                  {{(OmitAuthorizationEndpoint ? string.Empty : $"\"authorization_endpoint\": \"{AuthorizationEndpoint}\",")}}
                  "token_endpoint": "{{Issuer}}/token",
                  "jwks_uri": "{{Issuer}}/jwks",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """,
            var u when u == new Uri(Issuer + "/jwks") => """{ "keys": [] }""",
            _ => null,
        };

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
