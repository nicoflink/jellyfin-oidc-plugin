using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Runs the OIDC Login flow, starting with Start; Callback and Complete join it as they are built.
/// </summary>
public sealed partial class OidcLoginService
{
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private readonly IdentityProviderClient _identityProvider;
    private readonly FlowCookieProtector _flowCookie;
    private readonly TimeProvider _clock;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILogger<OidcLoginService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginService"/> class.
    /// </summary>
    /// <param name="identityProvider">The Identity Provider client.</param>
    /// <param name="flowCookie">Protects the flow cookie.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="configuration">Reads the current Plugin configuration.</param>
    /// <param name="logger">The logger.</param>
    public OidcLoginService(
        IdentityProviderClient identityProvider,
        FlowCookieProtector flowCookie,
        TimeProvider clock,
        Func<PluginConfiguration> configuration,
        ILogger<OidcLoginService> logger)
    {
        _identityProvider = identityProvider;
        _flowCookie = flowCookie;
        _clock = clock;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Starts an OIDC Login: a redirect to the Identity Provider's authorization endpoint plus the flow cookie.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The redirect and flow cookie, or a failure.</returns>
    public async Task<StartResult> StartAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration();
        if (!TryGetHttpUrl(configuration.PublicJellyfinUrl, out var publicUrl)
            || !TryGetHttpUrl(configuration.IssuerUrl, out var issuer)
            || string.IsNullOrEmpty(configuration.ClientId)
            || string.IsNullOrEmpty(configuration.ClientSecret))
        {
            return Fail(new LoginFailure(FailureCategory.Failed, "The Plugin is not configured: Public Jellyfin URL, Issuer URL, client id and client secret are required."));
        }

        OpenIdConnectConfiguration discovery;
        try
        {
            discovery = await _identityProvider.GetDiscoveryAsync(issuer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            return Fail(new LoginFailure(FailureCategory.Unavailable, $"Discovery could not be fetched from {issuer}: {e.InnerException?.Message ?? e.Message}"));
        }

        if (string.IsNullOrEmpty(discovery.AuthorizationEndpoint))
        {
            return Fail(new LoginFailure(FailureCategory.Failed, $"Discovery from {issuer} has no authorization_endpoint."));
        }

        var flow = new FlowState(RandomToken(), RandomToken(), RandomToken(), _clock.GetUtcNow() + FlowLifetime);
        var authorizationUrl = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = configuration.PublicJellyfinUrl.TrimEnd('/') + "/OidcLogin/Callback",
            ["scope"] = configuration.Scopes,
            ["state"] = flow.State,
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))),
            ["code_challenge_method"] = "S256",
        });

        var secure = publicUrl.Scheme == Uri.UriSchemeHttps;
        var cookie = new IssuedCookie(
            secure ? FlowCookieProtector.SecureCookieName : FlowCookieProtector.InsecureCookieName,
            _flowCookie.Protect(flow),
            new CookieOptions { Path = "/", HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax });
        return StartResult.Redirect(new Uri(authorizationUrl), cookie);
    }

    private static bool TryGetHttpUrl(string value, [NotNullWhen(true)] out Uri? url)
        => Uri.TryCreate(value, UriKind.Absolute, out url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Generates 32 random bytes, base64url-encoded: used for <c>state</c>, <c>nonce</c> and the PKCE verifier.
    /// </summary>
    private static string RandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private StartResult Fail(LoginFailure failure)
    {
        LogFailure(failure.Category, failure.Cause);
        return StartResult.Fail(failure);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "OIDC Login failed ({Category}): {Cause}")]
    private partial void LogFailure(FailureCategory category, string cause);
}
