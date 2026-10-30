using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Talks to the Identity Provider. Discovery is fetched lazily from the Issuer URL and cached.
/// </summary>
public sealed class IdentityProviderClient
{
    private static readonly TimeSpan DiscoveryRefreshInterval = TimeSpan.FromHours(24);

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
