using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Runs the OIDC Login flow: Start, Callback and Complete.
/// </summary>
public sealed partial class OidcLoginService : IDisposable
{
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private static readonly LoginFailure NotConfigured = new(FailureCategory.Failed, "The Plugin is not configured: Public Jellyfin URL, Issuer URL, client id and client secret are required.");

    private readonly IdentityProviderClient _identityProvider;
    private readonly FlowCookieProtector _flowCookie;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly LinkedIdentityStore _linkedIdentities;
    private readonly HandoffCodes _handoffCodes;
    private readonly TimeProvider _clock;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly JellyfinWebClient _webClient;
    private readonly ILogger<OidcLoginService> _logger;

    /// <summary>
    /// Serializes resolving Subjects to users, so two concurrent first logins can't provision twice and two
    /// concurrent demotions can't both pass the last-admin guard.
    /// </summary>
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginService"/> class.
    /// </summary>
    /// <param name="identityProvider">The Identity Provider client.</param>
    /// <param name="flowCookie">Protects the flow cookie.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="sessionManager">Jellyfin's session manager.</param>
    /// <param name="linkedIdentities">The Linked Identity store.</param>
    /// <param name="handoffCodes">The handoff codes Callback mints for Complete.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="configuration">Reads the current Plugin configuration.</param>
    /// <param name="webClient">What jellyfin-web reports when it signs in.</param>
    /// <param name="logger">The logger.</param>
    public OidcLoginService(
        IdentityProviderClient identityProvider,
        FlowCookieProtector flowCookie,
        IUserManager userManager,
        ISessionManager sessionManager,
        LinkedIdentityStore linkedIdentities,
        HandoffCodes handoffCodes,
        TimeProvider clock,
        Func<PluginConfiguration> configuration,
        JellyfinWebClient webClient,
        ILogger<OidcLoginService> logger)
    {
        _identityProvider = identityProvider;
        _flowCookie = flowCookie;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _linkedIdentities = linkedIdentities;
        _handoffCodes = handoffCodes;
        _clock = clock;
        _configuration = configuration;
        _webClient = webClient;
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
        if (!TryGetSettings(configuration, out var publicUrl, out var issuer))
        {
            return StartResult.Fail(Log(NotConfigured));
        }

        OpenIdConnectConfiguration discovery;
        try
        {
            discovery = await _identityProvider.GetDiscoveryAsync(issuer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            return StartResult.Fail(Log(DiscoveryUnavailable(issuer, e)));
        }

        if (string.IsNullOrEmpty(discovery.AuthorizationEndpoint))
        {
            return StartResult.Fail(Log(new LoginFailure(FailureCategory.Failed, $"Discovery from {issuer} has no authorization_endpoint.")));
        }

        var flow = new FlowState(RandomToken(), RandomToken(), RandomToken(), _clock.GetUtcNow() + FlowLifetime);
        var authorizationUrl = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = CallbackUrl(configuration),
            ["scope"] = configuration.Scopes,
            ["state"] = flow.State,
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))),
            ["code_challenge_method"] = "S256",
        });

        return StartResult.Redirect(new Uri(authorizationUrl), FlowCookie(publicUrl, _flowCookie.Protect(flow)));
    }

    /// <summary>
    /// Completes the Identity Provider leg: checks the flow, redeems the code, validates the ID token, resolves the
    /// Subject to a Jellyfin user (provisioning one if it has no Linked Identity) and mints a handoff code.
    /// </summary>
    /// <param name="query">The callback query.</param>
    /// <param name="flowCookie">The flow cookie value, if the browser sent one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handoff code for Complete, or a failure.</returns>
    public async Task<CallbackResult> CallbackAsync(CallbackQuery query, string? flowCookie, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var configuration = _configuration();
        if (!TryGetSettings(configuration, out _, out var issuer))
        {
            return CallbackResult.Fail(Log(NotConfigured));
        }

        var flow = flowCookie is null ? null : _flowCookie.Unprotect(flowCookie);
        if (flow is null || flow.ExpiresAt <= _clock.GetUtcNow())
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Expired, "The flow cookie is missing, invalid or expired.")));
        }

        if (!string.Equals(query.State, flow.State, StringComparison.Ordinal))
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Expired, "The callback state doesn't match the flow cookie.")));
        }

        if (string.IsNullOrEmpty(query.Code))
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Failed, "The callback has no authorization code.")));
        }

        OpenIdConnectConfiguration discovery;
        try
        {
            discovery = await _identityProvider.GetDiscoveryAsync(issuer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            return CallbackResult.Fail(Log(DiscoveryUnavailable(issuer, e)));
        }

        if (!Uri.TryCreate(discovery.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint))
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Failed, $"Discovery from {issuer} has no token_endpoint.")));
        }

        JsonWebToken idToken;
        try
        {
            var rawIdToken = await _identityProvider.RedeemCodeAsync(
                tokenEndpoint,
                new CodeRedemption(query.Code, CallbackUrl(configuration), flow.CodeVerifier, configuration.ClientId, configuration.ClientSecret),
                cancellationToken).ConfigureAwait(false);
            idToken = await IdentityProviderClient.ValidateIdTokenAsync(rawIdToken, discovery, configuration.ClientId, flow.Nonce, _clock).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Unavailable, $"The token endpoint {tokenEndpoint} could not be reached: {e.Message}")));
        }
        catch (IdentityProviderException e)
        {
            return CallbackResult.Fail(Log(new LoginFailure(FailureCategory.Failed, e.Message)));
        }

        var (user, failure) = await ResolveUserAsync(idToken, configuration).ConfigureAwait(false);
        if (failure is not null)
        {
            return CallbackResult.Fail(Log(failure));
        }

        // The code travels in the fragment, so it never reaches a server log or a Referer header.
        var handoffCode = _handoffCodes.Mint(user!.Id, flowCookie!);
        return CallbackResult.Handoff(new Uri(configuration.PublicJellyfinUrl.TrimEnd('/') + "/OidcLogin/Complete#code=" + handoffCode));
    }

    /// <summary>
    /// Completes an OIDC Login: checks the request came from the Complete page, consumes the handoff code with the
    /// flow cookie it was minted for, and creates a Jellyfin session as jellyfin-web would.
    /// </summary>
    /// <param name="request">The Complete page's POST.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Jellyfin session and the cleared flow cookie, or a failure.</returns>
    public async Task<CompleteResult> CompleteAsync(CompleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = _configuration();
        if (!TryGetSettings(configuration, out var publicUrl, out _))
        {
            return CompleteResult.Fail(Log(NotConfigured));
        }

        // Checked before the code is touched, so a forged cross-site request can't use up someone's code.
        if (!IsOrigin(request.Origin, publicUrl))
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, "The Complete request didn't come from the Public Jellyfin URL's origin.")));
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, "The Complete request isn't JSON.")));
        }

        if (!TryReadCompleteBody(request.Body, out var handoffCode, out var deviceId))
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, "The Complete request has no code or deviceId.")));
        }

        if (!DeviceIdFormat().IsMatch(deviceId))
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, "The Complete request's device id is malformed.")));
        }

        if (request.FlowCookie is null || _handoffCodes.Consume(handoffCode, request.FlowCookie) is not { } userId)
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Expired, "The handoff code is unknown, used, expired or presented without its flow cookie.")));
        }

        if (_userManager.GetUserById(userId) is not { } user)
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, $"Jellyfin user {userId} was deleted during the login.")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        AuthenticationResult session;
        try
        {
            session = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                Username = user.Username,
                App = JellyfinWebClient.App,
                AppVersion = _webClient.AppVersion,
                DeviceId = deviceId,
                DeviceName = JellyfinWebClient.DeviceName(request.UserAgent),
                RemoteEndPoint = request.RemoteEndPoint,
            }).ConfigureAwait(false);
        }
        catch (SecurityException e)
        {
            return CompleteResult.Fail(Log(new LoginFailure(FailureCategory.Failed, $"Jellyfin refused a session for user {user.Id}: {e.Message}")));
        }

        var clearedFlowCookie = FlowCookie(publicUrl, string.Empty);
        clearedFlowCookie.Options.Expires = DateTimeOffset.UnixEpoch;
        return CompleteResult.SignedIn(new JellyfinSession(session.AccessToken, user.Id, session.ServerId), clearedFlowCookie);
    }

    /// <inheritdoc />
    public void Dispose() => _resolveLock.Dispose();

    /// <summary>
    /// Whether <paramref name="configuration"/> has everything an OIDC Login needs: a Public Jellyfin URL, an Issuer
    /// URL, a client id and a client secret.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns><c>true</c> when an OIDC Login can start.</returns>
    public static bool IsConfigured(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TryGetSettings(configuration, out _, out _);
    }

    private static LoginFailure DiscoveryUnavailable(Uri issuer, InvalidOperationException e)
        => new(FailureCategory.Unavailable, $"Discovery could not be fetched from {issuer}: {e.InnerException?.Message ?? e.Message}");

    private static bool TryGetSettings(PluginConfiguration configuration, [NotNullWhen(true)] out Uri? publicUrl, [NotNullWhen(true)] out Uri? issuer)
    {
        issuer = null;
        return TryGetHttpUrl(configuration.PublicJellyfinUrl, out publicUrl)
            && TryGetHttpUrl(configuration.IssuerUrl, out issuer)
            && !string.IsNullOrEmpty(configuration.ClientId)
            && !string.IsNullOrEmpty(configuration.ClientSecret);
    }

    private static bool TryGetHttpUrl(string value, [NotNullWhen(true)] out Uri? url)
        => Uri.TryCreate(value, UriKind.Absolute, out url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// The flow cookie: <c>__Host-</c> and <c>Secure</c> when the Public Jellyfin URL is https, never derived from
    /// the request.
    /// </summary>
    private static IssuedCookie FlowCookie(Uri publicUrl, string value)
    {
        var secure = publicUrl.Scheme == Uri.UriSchemeHttps;
        return new IssuedCookie(
            secure ? FlowCookieProtector.SecureCookieName : FlowCookieProtector.InsecureCookieName,
            value,
            new CookieOptions { Path = "/", HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax });
    }

    private static bool IsOrigin(string? origin, Uri publicUrl)
        => Uri.TryCreate(origin, UriKind.Absolute, out var originUrl)
            && string.Equals(originUrl.GetLeftPart(UriPartial.Authority), publicUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static bool TryReadCompleteBody(string? body, [NotNullWhen(true)] out string? handoffCode, [NotNullWhen(true)] out string? deviceId)
    {
        handoffCode = null;
        deviceId = null;
        try
        {
            using var json = JsonDocument.Parse(body ?? string.Empty);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && json.RootElement.TryGetProperty("deviceId", out var device) && device.ValueKind == JsonValueKind.String)
            {
                handoffCode = code.GetString()!;
                deviceId = device.GetString()!;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// The device ids jellyfin-web generates (base64 with <c>=</c> turned into <c>1</c>, or a timestamp), plus
    /// base64url. Nothing that could break Jellyfin's <c>Authorization</c> header or the Devices page.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9+/=_-]{1,512}$")]
    private static partial Regex DeviceIdFormat();

    private static string CallbackUrl(PluginConfiguration configuration) => configuration.PublicJellyfinUrl.TrimEnd('/') + "/OidcLogin/Callback";

    /// <summary>
    /// Generates 32 random bytes, base64url-encoded: used for <c>state</c>, <c>nonce</c> and the PKCE verifier.
    /// </summary>
    private static string RandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Resolves the ID token's Subject to a Jellyfin user through its Linked Identity, provisioning a user and a link
    /// when the Subject has none, then syncs the user's Admin Group membership.
    /// </summary>
    private async Task<(User? User, LoginFailure? Failure)> ResolveUserAsync(JsonWebToken idToken, PluginConfiguration configuration)
    {
        var issuer = idToken.Issuer;
        var subject = idToken.Subject;
        if (string.IsNullOrEmpty(subject))
        {
            return (null, new LoginFailure(FailureCategory.Failed, "The ID token has no sub claim."));
        }

        await _resolveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (_linkedIdentities.Find(issuer, subject) is { } link)
            {
                if (_userManager.GetUserById(link.UserId) is not { } linkedUser)
                {
                    return (null, new LoginFailure(FailureCategory.Failed, $"Subject {subject} is linked to Jellyfin user {link.UserId}, which no longer exists."));
                }

                _linkedIdentities.RecordLogin(issuer, subject, now);
                await SyncAdminGroupAsync(linkedUser, idToken, configuration).ConfigureAwait(false);
                return (linkedUser, null);
            }

            if (!idToken.TryGetPayloadValue<string>(configuration.UsernameClaim, out var username) || string.IsNullOrWhiteSpace(username))
            {
                return (null, new LoginFailure(FailureCategory.Failed, $"Subject {subject} can't be provisioned: the ID token has no {configuration.UsernameClaim} claim."));
            }

            if (_userManager.GetUserByName(username) is not null)
            {
                return (null, new LoginFailure(FailureCategory.Failed, $"Subject {subject} can't be provisioned: a Jellyfin user named '{username}' already exists."));
            }

            var user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            _linkedIdentities.Add(new LinkedIdentity(issuer, subject, user.Id, now, now));
            await SyncAdminGroupAsync(user, idToken, configuration).ConfigureAwait(false);
            return (user, null);
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    /// <summary>
    /// Sets <see cref="PermissionKind.IsAdministrator"/> to Admin Group membership, and nothing else. Skipped when
    /// the setting is empty or the groups claim is absent; the last enabled administrator is never demoted. Runs
    /// under <see cref="_resolveLock"/>, so the admin count can't change between counting and demoting.
    /// </summary>
    private async Task SyncAdminGroupAsync(User user, JsonWebToken idToken, PluginConfiguration configuration)
    {
        if (string.IsNullOrEmpty(configuration.AdminGroup))
        {
            return;
        }

        if (!TryGetGroups(idToken, configuration.GroupsClaim, out var groups))
        {
            LogAdminGroupSkipped(user.Id, configuration.GroupsClaim);
            return;
        }

        var member = groups.Contains(configuration.AdminGroup, StringComparer.Ordinal);
        if (member == user.HasPermission(PermissionKind.IsAdministrator))
        {
            return;
        }

        if (!member && !_userManager.GetUsers().Any(u => u.Id != user.Id && u.HasPermission(PermissionKind.IsAdministrator) && !u.HasPermission(PermissionKind.IsDisabled)))
        {
            LogLastAdministratorKept(user.Id, configuration.AdminGroup);
            return;
        }

        user.SetPermission(PermissionKind.IsAdministrator, member);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the groups claim: a string array, or a single string.
    /// </summary>
    private static bool TryGetGroups(JsonWebToken idToken, string claim, [NotNullWhen(true)] out IReadOnlyCollection<string>? groups)
    {
        groups = null;
        if (!idToken.TryGetPayloadValue<JsonElement>(claim, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            groups = [value.GetString()!];
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            groups = value.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).ToArray();
        }

        return groups is not null;
    }

    private LoginFailure Log(LoginFailure failure)
    {
        // Expired is a normal outcome (a stale tab, a second browser); everything else needs an admin's attention.
        if (failure.Category == FailureCategory.Expired)
        {
            LogExpired(failure.Cause);
        }
        else
        {
            LogFailure(failure.Category, failure.Cause);
        }

        return failure;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "OIDC Login failed ({Category}): {Cause}")]
    private partial void LogFailure(FailureCategory category, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin Group sync skipped for Jellyfin user {UserId}: the ID token has no {GroupsClaim} claim")]
    private partial void LogAdminGroupSkipped(Guid userId, string groupsClaim);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jellyfin user {UserId} is not in Admin Group {AdminGroup} but stays an administrator: it is the last one")]
    private partial void LogLastAdministratorKept(Guid userId, string adminGroup);

    [LoggerMessage(Level = LogLevel.Information, Message = "OIDC Login expired: {Cause}")]
    private partial void LogExpired(string cause);
}
