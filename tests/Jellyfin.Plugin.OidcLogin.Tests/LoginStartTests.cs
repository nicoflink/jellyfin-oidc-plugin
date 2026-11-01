using System;
using System.Buffers.Text;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LoginStartTests : IDisposable
{
    private readonly LoginHarness _login = new();

    [Fact]
    public async Task RedirectsToAuthorizationEndpointWithPkce()
    {
        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        var location = result.AuthorizationUrl!;
        Assert.Equal(StubIdentityProvider.AuthorizationEndpoint.GetLeftPart(UriPartial.Path), location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("jellyfin", query["client_id"]);
        Assert.Equal("https://jellyfin.example.test/OidcLogin/Callback", query["redirect_uri"]);
        Assert.Equal("openid profile groups", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
    }

    [Fact]
    public async Task FlowCookieCarriesStateNonceVerifierAndTenMinuteExpiry()
    {
        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        var query = QueryHelpers.ParseQuery(result.AuthorizationUrl!.Query);
        var flow = new FlowCookieProtector(_login.DataProtection).Unprotect(result.FlowCookie!.Value);
        Assert.NotNull(flow);
        Assert.Equal(query["state"], flow.State);
        Assert.Equal(32, Base64Url.DecodeFromChars(flow.State).Length);
        Assert.Equal(query["nonce"], flow.Nonce);
        Assert.Equal(query["code_challenge"], Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 10, 0, TimeSpan.Zero), flow.ExpiresAt);
    }

    [Fact]
    public async Task EachStartHasItsOwnState()
    {
        var service = _login.CreateService();

        var first = await service.StartAsync(TestContext.Current.CancellationToken);
        var second = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(QueryHelpers.ParseQuery(first.AuthorizationUrl!.Query)["state"], QueryHelpers.ParseQuery(second.AuthorizationUrl!.Query)["state"]);
    }

    [Fact]
    public async Task TamperedFlowCookieIsRejected()
    {
        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        var value = result.FlowCookie!.Value;
        var tampered = value[..^2] + (value[^2] == 'A' ? 'B' : 'A') + value[^1];
        Assert.Null(new FlowCookieProtector(_login.DataProtection).Unprotect(tampered));
        Assert.Null(new FlowCookieProtector(new EphemeralDataProtectionProvider()).Unprotect(value));
    }

    [Fact]
    public async Task FlowCookieIsHostPrefixedAndSecureOnHttps()
    {
        var cookie = (await _login.CreateService().StartAsync(TestContext.Current.CancellationToken)).FlowCookie!;

        Assert.Equal("__Host-OidcLogin-Flow", cookie.Name);
        Assert.Equal("/", cookie.Options.Path);
        Assert.Null(cookie.Options.Domain);
        Assert.True(cookie.Options.HttpOnly);
        Assert.True(cookie.Options.Secure);
        Assert.Equal(SameSiteMode.Lax, cookie.Options.SameSite);
    }

    [Fact]
    public async Task FlowCookieDropsSecureAndPrefixOnLoopbackHttp()
    {
        _login.Configuration.PublicJellyfinUrl = "http://127.0.0.1:8096";

        var cookie = (await _login.CreateService().StartAsync(TestContext.Current.CancellationToken)).FlowCookie!;

        Assert.Equal("OidcLogin-Flow", cookie.Name);
        Assert.Equal("/", cookie.Options.Path);
        Assert.True(cookie.Options.HttpOnly);
        Assert.False(cookie.Options.Secure);
        Assert.Equal(SameSiteMode.Lax, cookie.Options.SameSite);
    }

    [Fact]
    public async Task DiscoveryIsFetchedLazilyFromTheIssuerOnceAndCached()
    {
        var service = _login.CreateService();
        Assert.Empty(_login.IdentityProvider.Requests);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StubIdentityProvider.DiscoveryUrl, _login.IdentityProvider.Requests.First());
        Assert.Single(_login.IdentityProvider.Requests, u => u.AbsolutePath.EndsWith("/openid-configuration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreachableIdentityProviderIsUnavailableAndRecovers()
    {
        var service = _login.CreateService();
        _login.IdentityProvider.IsDown = true;

        var down = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Unavailable, down.Failure?.Category);
        Assert.Null(down.AuthorizationUrl);
        Assert.Null(down.FlowCookie);

        _login.IdentityProvider.IsDown = false;
        var up = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(up.Failure);
        Assert.NotNull(up.AuthorizationUrl);
    }

    [Fact]
    public async Task DiscoveryWithoutAuthorizationEndpointFails()
    {
        _login.IdentityProvider.OmitAuthorizationEndpoint = true;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.FlowCookie);
    }

    [Theory]
    [InlineData(nameof(PluginConfiguration.PublicJellyfinUrl))]
    [InlineData(nameof(PluginConfiguration.IssuerUrl))]
    [InlineData(nameof(PluginConfiguration.ClientId))]
    [InlineData(nameof(PluginConfiguration.ClientSecret))]
    public async Task UnconfiguredPluginFailsWithoutContactingTheIdentityProvider(string missingSetting)
    {
        typeof(PluginConfiguration).GetProperty(missingSetting)!.SetValue(_login.Configuration, string.Empty);

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.FlowCookie);
        Assert.Empty(_login.IdentityProvider.Requests);
    }

    [Theory]
    [InlineData("jellyfin.example.test")]
    [InlineData("ftp://jellyfin.example.test")]
    public async Task PublicJellyfinUrlThatIsNotAnHttpUrlFails(string publicUrl)
    {
        _login.Configuration.PublicJellyfinUrl = publicUrl;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
    }

    public void Dispose() => _login.Dispose();
}
