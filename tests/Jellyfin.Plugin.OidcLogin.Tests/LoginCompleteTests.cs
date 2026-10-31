using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LoginCompleteTests : IDisposable
{
    private const string PublicOrigin = "https://jellyfin.example.test";
    private const string DeviceId = "TW96aWxsYS81LjAgKFgxMTsgTGludXggeDg2XzY0KSBDaHJvbWUvMTQwfDE3NjE4MjU2MDAwMDA1";
    private const string ChromeUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly List<AuthenticationRequest> _sessionRequests = [];
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = PublicOrigin + "/",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
    };

    public LoginCompleteTests()
    {
        _sessions.AuthenticateDirect(Arg.Any<AuthenticationRequest>()).Returns(call =>
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

    [Fact]
    public async Task HandoffCodeBecomesAJellyfinSessionForTheResolvedUser()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        var user = Assert.Single(_users.All);
        Assert.Equal("session-token-1", result.Session!.AccessToken);
        Assert.Equal(user.Id, result.Session.UserId);
        Assert.Equal("server-id", result.Session.ServerId);
        var request = Assert.Single(_sessionRequests);
        Assert.Equal(user.Id, request.UserId);
        Assert.Equal(DeviceId, request.DeviceId);
    }

    [Fact]
    public async Task SessionIsCreatedAsJellyfinWebWouldCreateIt()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        var request = Assert.Single(_sessionRequests);
        Assert.Equal("Jellyfin Web", request.App);
        Assert.Equal("12.4.0", request.AppVersion);
        Assert.Equal("Chrome", request.DeviceName);
        Assert.Equal("203.0.113.7", request.RemoteEndPoint);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0", "Firefox")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0", "Edge Chromium")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 OPR/124.0.0.0", "Opera")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15", "Safari")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1", "Safari iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 16; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36", "Chrome Android")]
    [InlineData(null, "Web Browser")]
    public async Task DeviceNameIsTheBrowserNameJellyfinWebWouldSend(string? userAgent, string deviceName)
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        await service.CompleteAsync(Request(code, cookie) with { UserAgent = userAgent }, TestContext.Current.CancellationToken);

        Assert.Equal(deviceName, Assert.Single(_sessionRequests).DeviceName);
    }

    [Fact]
    public async Task ClearsTheFlowCookie()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        Assert.Equal(FlowCookieProtector.SecureCookieName, result.ClearedFlowCookie!.Name);
        Assert.Equal("/", result.ClearedFlowCookie.Options.Path);
        Assert.True(result.ClearedFlowCookie.Options.Secure);
    }

    [Fact]
    public async Task ARepeatLoginGetsANewSessionForTheSameUser()
    {
        var service = CreateService();
        var (firstCode, firstCookie) = await SignInAsync(service);
        var first = await service.CompleteAsync(Request(firstCode, firstCookie), TestContext.Current.CancellationToken);
        var (secondCode, secondCookie) = await SignInAsync(service);

        var second = await service.CompleteAsync(Request(secondCode, secondCookie), TestContext.Current.CancellationToken);

        Assert.Equal(first.Session!.UserId, second.Session!.UserId);
        Assert.NotEqual(first.Session.AccessToken, second.Session.AccessToken);
    }

    [Fact]
    public async Task HandoffCodeIsSingleUse()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);
        await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        var reuse = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(reuse, FailureCategory.Expired);
        Assert.Single(_sessionRequests);
    }

    [Fact]
    public async Task HandoffCodePresentedWithAnotherFlowCookieIsRefusedAndUsedUp()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);
        var (_, otherCookie) = await SignInAsync(service);

        var mismatch = await service.CompleteAsync(Request(code, otherCookie), TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(mismatch, FailureCategory.Expired);
        AssertRefused(retry, FailureCategory.Expired);
    }

    [Fact]
    public async Task MissingFlowCookieIsRefused()
    {
        var service = CreateService();
        var (code, _) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, null), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Expired);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public async Task HandoffCodeLivesThirtySeconds(int secondsLater, bool accepted)
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);
        _clock.Advance(TimeSpan.FromSeconds(secondsLater));

        var result = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        if (accepted)
        {
            Assert.NotNull(result.Session);
        }
        else
        {
            AssertRefused(result, FailureCategory.Expired);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://evil.example.test")]
    [InlineData("http://jellyfin.example.test")]
    [InlineData("https://jellyfin.example.test:8443")]
    [InlineData("https://sub.jellyfin.example.test")]
    public async Task RequestFromAnotherOriginIsRefusedWithoutUsingTheCode(string? origin)
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie) with { Origin = origin }, TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task OriginMatchesThePublicJellyfinUrlIgnoringCaseAndItsPath()
    {
        _configuration.PublicJellyfinUrl = "https://Jellyfin.Example.Test/jellyfin/";
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    public async Task NonJsonRequestIsRefusedWithoutUsingTheCode(string? contentType)
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie) with { ContentType = contentType }, TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task JsonWithACharsetIsAccepted()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie) with { ContentType = "application/json; charset=utf-8" }, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("comma,inside")]
    public async Task MalformedDeviceIdIsRefusedWithoutUsingTheCode(string deviceId)
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie, deviceId), TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task OverlongDeviceIdIsRefused()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request(code, cookie, new string('a', 513)), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"deviceId\":\"abc\"}")]
    [InlineData("{\"code\":42,\"deviceId\":\"abc\"}")]
    public async Task MalformedBodyIsRefused(string body)
    {
        var service = CreateService();
        var (_, cookie) = await SignInAsync(service);

        var result = await service.CompleteAsync(Request("unused", cookie) with { Body = body }, TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
    }

    [Fact]
    public async Task UserDeletedSinceTheCallbackFails()
    {
        var service = CreateService();
        var (code, cookie) = await SignInAsync(service);
        _users.Remove(_users.All[0]);

        var result = await service.CompleteAsync(Request(code, cookie), TestContext.Current.CancellationToken);

        AssertRefused(result, FailureCategory.Failed);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    private static CompleteRequest Request(string code, string? flowCookie, string deviceId = DeviceId) => new(
        PublicOrigin,
        "application/json",
        JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = code, ["deviceId"] = deviceId }),
        flowCookie,
        ChromeUserAgent,
        "203.0.113.7");

    private void AssertRefused(CompleteResult result, FailureCategory category)
    {
        Assert.Equal(category, result.Failure?.Category);
        Assert.Null(result.Session);
        Assert.Null(result.ClearedFlowCookie);
    }

    /// <summary>
    /// Runs Start and Callback against the stub Identity Provider, returning the handoff code and flow cookie the
    /// browser holds on the Complete page.
    /// </summary>
    private async Task<(string HandoffCode, string FlowCookie)> SignInAsync(OidcLoginService service)
    {
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = _clock.GetUtcNow();
        _identityProvider.IdToken = _identityProvider.Sign(new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = _configuration.ClientId,
            ["sub"] = "6a3f1c2e-sub",
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        });
        var flowCookie = start.FlowCookie!.Value;
        var callback = await service.CallbackAsync(new CallbackQuery("authorization-code", authorize["state"]), flowCookie, TestContext.Current.CancellationToken);
        return (callback.HandoffCode!, flowCookie);
    }

    private OidcLoginService CreateService()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new OidcLoginService(
            new IdentityProviderClient(httpClientFactory),
            new FlowCookieProtector(_dataProtection),
            _users.Manager,
            _sessions,
            new LinkedIdentityStore(_dataFolder),
            new HandoffCodes(_clock),
            _clock,
            () => _configuration,
            new JellyfinWebClient("12.4.0"),
            NullLogger<OidcLoginService>.Instance);
    }
}
