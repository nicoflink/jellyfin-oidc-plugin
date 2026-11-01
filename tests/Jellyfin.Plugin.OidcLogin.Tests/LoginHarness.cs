using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Everything an <see cref="OidcLoginService"/> runs against in unit tests: the stub Identity Provider, a fake
/// clock, in-memory users, a session manager that records what it is asked, a temporary Linked Identity store, and
/// a configuration for both; plus the browser's part of a login.
/// </summary>
internal sealed class LoginHarness : IDisposable
{
    public const string Subject = "6a3f1c2e-sub";
    public const string PublicOrigin = "https://jellyfin.example.test";
    public const string DeviceId = "TW96aWxsYS81LjAgKFgxMTsgTGludXggeDg2XzY0KSBDaHJvbWUvMTQwfDE3NjE4MjU2MDAwMDA1";
    public const string ChromeUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    public const string AuthorizationCode = "authorization-code";

    private readonly List<AuthenticationRequest> _sessionRequests = [];

    public LoginHarness()
    {
        DataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
        LinkedIdentities = new LinkedIdentityStore(DataFolder);
        Sessions.AuthenticateDirect(Arg.Any<AuthenticationRequest>()).Returns(call =>
        {
            var request = call.Arg<AuthenticationRequest>();
            _sessionRequests.Add(request);
            return Task.FromResult(new AuthenticationResult
            {
                AccessToken = "session-token-" + _sessionRequests.Count,
                ServerId = "server-id",
                User = new UserDto { Id = request.UserId },
            });
        });
    }

    public StubIdentityProvider IdentityProvider { get; } = new();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public EphemeralDataProtectionProvider DataProtection { get; } = new();

    public InMemoryUsers Users { get; } = new();

    public RecordingLogger<OidcLoginService> Logger { get; } = new();

    public ISessionManager Sessions { get; } = Substitute.For<ISessionManager>();

    /// <summary>
    /// Gets every session <see cref="Sessions"/> was asked to create, in order.
    /// </summary>
    public IReadOnlyList<AuthenticationRequest> SessionRequests => _sessionRequests;

    public string DataFolder { get; }

    public LinkedIdentityStore LinkedIdentities { get; }

    public PluginConfiguration Configuration { get; } = new()
    {
        PublicJellyfinUrl = PublicOrigin,
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
    };

    /// <summary>
    /// Creates a service; each has its own discovery cache, as after a Jellyfin restart.
    /// </summary>
    public OidcLoginService CreateService(IdentityProviderClient? identityProvider = null, HandoffCodes? handoffCodes = null) => new(
        identityProvider ?? CreateIdentityProviderClient(),
        new FlowCookieProtector(DataProtection),
        Users.Manager,
        Sessions,
        LinkedIdentities,
        handoffCodes ?? new HandoffCodes(Clock),
        Clock,
        () => Configuration,
        new JellyfinWebClient("12.4.0"),
        Logger);

    public IdentityProviderClient CreateIdentityProviderClient()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(IdentityProvider, disposeHandler: false));
        return new IdentityProviderClient(httpClientFactory);
    }

    /// <summary>
    /// Adds a Jellyfin user linked to <see cref="Subject"/> three days ago.
    /// </summary>
    public User LinkedUser(string username, bool administrator = false)
    {
        var user = administrator ? Users.AddAdministrator(username) : Users.Add(username);
        var linkedAt = Clock.GetUtcNow() - TimeSpan.FromDays(3);
        LinkedIdentities.Add(new LinkedIdentity(StubIdentityProvider.Issuer, Subject, user.Id, linkedAt, linkedAt));
        return user;
    }

    /// <summary>
    /// Starts a login, then plays the Identity Provider: <paramref name="sign"/> (by default the stub's own key)
    /// turns Alice's claims for the Start's nonce, adjusted by <paramref name="adjustClaims"/>, into the ID token the
    /// token endpoint returns. Returns what the browser brings back to the Callback.
    /// </summary>
    public async Task<BrowserAtCallback> SignInAsync(
        OidcLoginService service,
        Action<Dictionary<string, object>>? adjustClaims = null,
        Func<Dictionary<string, object>, string>? sign = null,
        StartResult? start = null)
    {
        start ??= await service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = Clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = Configuration.ClientId,
            ["sub"] = Subject,
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        };
        adjustClaims?.Invoke(claims);
        IdentityProvider.IdToken = sign is null ? IdentityProvider.Sign(claims) : sign(claims);
        return new BrowserAtCallback(new CallbackQuery(AuthorizationCode, authorize["state"]), start.FlowCookie!.Value, authorize);
    }

    /// <summary>
    /// Runs Start and Callback (see <see cref="SignInAsync"/>).
    /// </summary>
    public async Task<(CallbackResult Result, string FlowCookie)> CallbackAsync(OidcLoginService service, Action<Dictionary<string, object>>? adjustClaims = null)
    {
        var (query, flowCookie) = await SignInAsync(service, adjustClaims);
        return (await service.CallbackAsync(query, flowCookie, TestContext.Current.CancellationToken), flowCookie);
    }

    /// <summary>
    /// Runs Start, Callback, which must succeed, and Complete from one browser.
    /// </summary>
    public async Task<CompleteResult> LoginAsync(OidcLoginService service, Action<Dictionary<string, object>>? adjustClaims = null, bool fromLocalNetwork = false)
    {
        var (callback, flowCookie) = await CallbackAsync(service, adjustClaims);
        Assert.Null(callback.Failure);
        return await service.CompleteAsync(Completion(callback.HandoffCode!, flowCookie, fromLocalNetwork: fromLocalNetwork), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs Start and Callback, which must succeed, returning the handoff code and flow cookie the browser holds on
    /// the Complete page.
    /// </summary>
    public async Task<(string HandoffCode, string FlowCookie)> HandoffAsync(OidcLoginService service)
    {
        var (callback, flowCookie) = await CallbackAsync(service);
        Assert.Null(callback.Failure);
        return (callback.HandoffCode!, flowCookie);
    }

    /// <summary>
    /// The Complete page's POST from Chrome, as jellyfin-web's device.
    /// </summary>
    public static CompleteRequest Completion(string code, string? flowCookie, string deviceId = DeviceId, bool fromLocalNetwork = false) => new(
        PublicOrigin,
        "application/json",
        JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = code, ["deviceId"] = deviceId }),
        flowCookie,
        ChromeUserAgent,
        "203.0.113.7",
        fromLocalNetwork);

    /// <summary>
    /// Asserts a Callback failed as Failed without provisioning a user or a link.
    /// </summary>
    public void AssertFailedWithoutProvisioning(CallbackResult result)
    {
        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.CompleteUrl);
        Assert.Empty(Users.All);
        Assert.Empty(LinkedIdentities.GetAll());
    }

    /// <summary>
    /// Asserts Complete refused with <paramref name="category"/>, creating no session and leaving the flow cookie.
    /// </summary>
    public static void AssertRefused(CompleteResult result, FailureCategory category)
    {
        Assert.Equal(category, result.Failure?.Category);
        Assert.Null(result.Session);
        Assert.Null(result.ClearedFlowCookie);
    }

    public void Dispose()
    {
        IdentityProvider.Dispose();
        Directory.Delete(DataFolder, recursive: true);
    }
}
