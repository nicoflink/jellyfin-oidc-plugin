using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Login;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LoginCompleteTests : IDisposable
{
    private readonly LoginHarness _login = new();

    public LoginCompleteTests()
    {
        _login.Configuration.PublicJellyfinUrl = LoginHarness.PublicOrigin + "/";
    }

    [Fact]
    public async Task HandoffCodeBecomesAJellyfinSessionForTheResolvedUser()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        var user = Assert.Single(_login.Users.All);
        Assert.Equal("session-token-1", result.Session!.AccessToken);
        Assert.Equal(user.Id, result.Session.UserId);
        Assert.Equal("server-id", result.Session.ServerId);
        var request = Assert.Single(_login.SessionRequests);
        Assert.Equal(user.Id, request.UserId);
        Assert.Equal(LoginHarness.DeviceId, request.DeviceId);
    }

    [Fact]
    public async Task SessionIsCreatedAsJellyfinWebWouldCreateIt()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        var request = Assert.Single(_login.SessionRequests);
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
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        await service.CompleteAsync(LoginHarness.Completion(code, cookie) with { UserAgent = userAgent }, TestContext.Current.CancellationToken);

        Assert.Equal(deviceName, Assert.Single(_login.SessionRequests).DeviceName);
    }

    [Fact]
    public async Task ClearsTheFlowCookie()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        Assert.Equal(FlowCookieProtector.SecureCookieName, result.ClearedFlowCookie!.Name);
        Assert.Equal("/", result.ClearedFlowCookie.Options.Path);
        Assert.True(result.ClearedFlowCookie.Options.Secure);
    }

    [Fact]
    public async Task ARepeatLoginGetsANewSessionForTheSameUser()
    {
        var service = _login.CreateService();
        var (firstCode, firstCookie) = await _login.HandoffAsync(service);
        var first = await service.CompleteAsync(LoginHarness.Completion(firstCode, firstCookie), TestContext.Current.CancellationToken);
        var (secondCode, secondCookie) = await _login.HandoffAsync(service);

        var second = await service.CompleteAsync(LoginHarness.Completion(secondCode, secondCookie), TestContext.Current.CancellationToken);

        Assert.Equal(first.Session!.UserId, second.Session!.UserId);
        Assert.NotEqual(first.Session.AccessToken, second.Session.AccessToken);
    }

    [Fact]
    public async Task HandoffCodeIsSingleUse()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);
        await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        var reuse = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(reuse, FailureCategory.Expired);
        Assert.Single(_login.SessionRequests);
    }

    [Fact]
    public async Task HandoffCodePresentedWithAnotherFlowCookieIsRefusedAndUsedUp()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);
        var (_, otherCookie) = await _login.HandoffAsync(service);

        var mismatch = await service.CompleteAsync(LoginHarness.Completion(code, otherCookie), TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(mismatch, FailureCategory.Expired);
        LoginHarness.AssertRefused(retry, FailureCategory.Expired);
    }

    [Fact]
    public async Task MissingFlowCookieIsRefused()
    {
        var service = _login.CreateService();
        var (code, _) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, null), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Expired);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public async Task HandoffCodeLivesThirtySeconds(int secondsLater, bool accepted)
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);
        _login.Clock.Advance(TimeSpan.FromSeconds(secondsLater));

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        if (accepted)
        {
            Assert.NotNull(result.Session);
        }
        else
        {
            LoginHarness.AssertRefused(result, FailureCategory.Expired);
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
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie) with { Origin = origin }, TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task OriginMatchesThePublicJellyfinUrlIgnoringCaseAndItsPath()
    {
        _login.Configuration.PublicJellyfinUrl = "https://Jellyfin.Example.Test/jellyfin/";
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    public async Task NonJsonRequestIsRefusedWithoutUsingTheCode(string? contentType)
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie) with { ContentType = contentType }, TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task JsonWithACharsetIsAccepted()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie) with { ContentType = "application/json; charset=utf-8" }, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("comma,inside")]
    public async Task MalformedDeviceIdIsRefusedWithoutUsingTheCode(string deviceId)
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie, deviceId), TestContext.Current.CancellationToken);
        var retry = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Failed);
        Assert.NotNull(retry.Session);
    }

    [Fact]
    public async Task OverlongDeviceIdIsRefused()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie, new string('a', 513)), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"deviceId\":\"abc\"}")]
    [InlineData("{\"code\":42,\"deviceId\":\"abc\"}")]
    public async Task MalformedBodyIsRefused(string body)
    {
        var service = _login.CreateService();
        var (_, cookie) = await _login.HandoffAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion("unused", cookie) with { Body = body }, TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Failed);
    }

    [Fact]
    public async Task UserDeletedSinceTheCallbackIsExpiredSoThePersonJustRetries()
    {
        var service = _login.CreateService();
        var (code, cookie) = await _login.HandoffAsync(service);
        _login.Users.Remove(_login.Users.All[0]);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        LoginHarness.AssertRefused(result, FailureCategory.Expired);
    }

    public void Dispose() => _login.Dispose();
}
