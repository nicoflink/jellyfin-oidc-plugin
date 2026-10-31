using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class TestConnectionTests : IDisposable
{
    private readonly StubIdentityProvider _identityProvider = new() { ScopesSupported = ["openid", "profile", "groups"] };

    [Fact]
    public async Task SucceedsWhenDiscoveryMatchesTheIssuer()
    {
        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(StubIdentityProvider.Issuer, result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task WarnsWhenTheGroupsScopeIsNotOffered()
    {
        _identityProvider.ScopesSupported = ["openid", "profile", "email"];

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.True(result.Succeeded, result.Message);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("\"groups\" scope", warning, StringComparison.Ordinal);
        Assert.Contains("Required Group and Admin Group", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarnsWhenDiscoveryListsNoScopes()
    {
        _identityProvider.ScopesSupported = null;

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("\"groups\" scope", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnUnreachableIdentityProvider()
    {
        _identityProvider.IsDown = true;

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.False(result.Succeeded);
        Assert.Contains("Couldn't reach", result.Message, StringComparison.Ordinal);
        Assert.Contains(StubIdentityProvider.DiscoveryUrl.ToString(), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsTheStatusOfAFailedDiscoveryRequest()
    {
        _identityProvider.DiscoveryStatus = HttpStatusCode.NotFound;

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.False(result.Succeeded);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsADiscoveryIssuerThatDiffersFromTheIssuerUrl()
    {
        _identityProvider.DiscoveryIssuer = StubIdentityProvider.Issuer + "/";

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.False(result.Succeeded);
        Assert.Contains($"\"{StubIdentityProvider.Issuer}/\"", result.Message, StringComparison.Ordinal);
        Assert.Contains("must equal the Issuer URL exactly", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsDiscoveryWithoutAnAuthorizationEndpoint()
    {
        _identityProvider.OmitAuthorizationEndpoint = true;

        var result = await TestAsync(StubIdentityProvider.Issuer);

        Assert.False(result.Succeeded);
        Assert.Contains("authorization_endpoint", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("id.example.test")]
    [InlineData("ftp://id.example.test")]
    public async Task RefusesAnIssuerUrlThatIsNotAnAbsoluteHttpUrl(string issuerUrl)
    {
        var result = await TestAsync(issuerUrl);

        Assert.False(result.Succeeded);
        Assert.Contains("absolute https URL", result.Message, StringComparison.Ordinal);
        Assert.Empty(_identityProvider.Requests);
    }

    [Fact]
    public async Task RefusesPlainHttpExceptOnLoopback()
    {
        var result = await TestAsync("http://id.example.test");

        Assert.False(result.Succeeded);
        Assert.Contains("https", result.Message, StringComparison.Ordinal);
        Assert.Empty(_identityProvider.Requests);
    }

    [Fact]
    public async Task AllowsPlainHttpOnLoopback()
    {
        await TestAsync("http://127.0.0.1:1411");

        Assert.Equal(new Uri("http://127.0.0.1:1411/.well-known/openid-configuration"), Assert.Single(_identityProvider.Requests));
    }

    public void Dispose() => _identityProvider.Dispose();

    private Task<ConnectionTestResult> TestAsync(string issuerUrl)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new ConnectionTester(httpClientFactory).TestAsync(issuerUrl, TestContext.Current.CancellationToken);
    }
}
