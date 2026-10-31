using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// The ID token and discovery rules that keep a misbehaving or misconfigured Identity Provider from ever producing
/// a session.
/// </summary>
public sealed class IdTokenAndDiscoveryHardeningTests : IDisposable
{
    private readonly LoginHarness _login = new();

    [Fact]
    public async Task MultipleAudiencesWithAzpEqualToTheClientIdAreAccepted()
    {
        var result = await CallbackAsync(claims =>
        {
            claims["aud"] = new[] { "another-client", "jellyfin" };
            claims["azp"] = "jellyfin";
        });

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task MultipleAudiencesWithoutAzpFail()
    {
        var result = await CallbackAsync(claims => claims["aud"] = new[] { "another-client", "jellyfin" });

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task MultipleAudiencesWithAzpOfAnotherClientFail()
    {
        var result = await CallbackAsync(claims =>
        {
            claims["aud"] = new[] { "another-client", "jellyfin" };
            claims["azp"] = "another-client";
        });

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task AzpOfAnotherClientFailsEvenWithASingleAudience()
    {
        var result = await CallbackAsync(claims => claims["azp"] = "another-client");

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-3, false)]
    public async Task ExpIsCheckedWithTwoMinutesSkew(int expMinutesFromNow, bool accepted)
    {
        var result = await CallbackAsync(claims => claims["exp"] = (_login.Clock.GetUtcNow() + TimeSpan.FromMinutes(expMinutesFromNow)).ToUnixTimeSeconds());

        AssertAccepted(result, accepted);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public async Task IatIsCheckedWithTwoMinutesSkew(int iatMinutesFromNow, bool accepted)
    {
        var result = await CallbackAsync(claims => claims["iat"] = (_login.Clock.GetUtcNow() + TimeSpan.FromMinutes(iatMinutesFromNow)).ToUnixTimeSeconds());

        AssertAccepted(result, accepted);
    }

    [Fact]
    public async Task IdTokenWithoutIatFails()
    {
        var result = await CallbackAsync(claims => claims.Remove("iat"));

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Theory]
    [InlineData(SecurityAlgorithms.RsaSha256)]
    [InlineData(SecurityAlgorithms.RsaSha384)]
    [InlineData(SecurityAlgorithms.RsaSha512)]
    [InlineData(SecurityAlgorithms.EcdsaSha256)]
    [InlineData(SecurityAlgorithms.EcdsaSha384)]
    [InlineData(SecurityAlgorithms.EcdsaSha512)]
    public async Task RsaAndEcdsaSignaturesAreAccepted(string algorithm)
    {
        var result = await CallbackSignedAsync(claims => _login.IdentityProvider.Sign(claims, algorithm));

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task RsaPssSignatureFails()
    {
        var result = await CallbackSignedAsync(claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSsaPssSha256));

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task HmacSignatureFails()
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" };

        var result = await CallbackSignedAsync(claims => StubIdentityProvider.Sign(claims, new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task UnsignedIdTokenFails()
    {
        var result = await CallbackSignedAsync(claims => UnsignedToken("none", claims));

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task EdDsaSignatureFailsWithAnAdminFacingMessageToRotateTheKey()
    {
        var result = await CallbackSignedAsync(claims => UnsignedToken("EdDSA", claims) + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64)));

        _login.AssertFailedWithoutProvisioning(result);
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Contains("EdDSA", entry.Message, StringComparison.Ordinal);
        Assert.Contains("rotate", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RS256", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownKidIsValidatedAgainstTheCachedJwks()
    {
        var service = _login.CreateService();

        await LoginAsync(service, claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        var second = await LoginAsync(service, claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        Assert.Null(second.Failure);
        Assert.Equal(1, JwksFetches());
    }

    [Fact]
    public async Task UnknownKidRefetchesTheJwksOnceSoARotatedKeyIsAccepted()
    {
        var service = _login.CreateService();
        await LoginAsync(service, claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        _login.IdentityProvider.RotateSigningKey();

        var result = await LoginAsync(service, claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        Assert.Null(result.Failure);
        Assert.Equal(2, JwksFetches());
    }

    [Fact]
    public async Task UnknownKidMissingAfterTheRefetchFailsAndIsNotRefetchedAgainRightAway()
    {
        var service = _login.CreateService();
        var unpublished = new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "unpublished-key" }, SecurityAlgorithms.RsaSha256);

        var first = await LoginAsync(service, claims => StubIdentityProvider.Sign(claims, unpublished));
        var second = await LoginAsync(service, claims => StubIdentityProvider.Sign(claims, unpublished));

        Assert.Equal(FailureCategory.Failed, first.Failure?.Category);
        Assert.Equal(FailureCategory.Failed, second.Failure?.Category);
        Assert.Equal(2, JwksFetches());
        Assert.Empty(_login.Users.All);
    }

    [Theory]
    [InlineData(StubIdentityProvider.Issuer + "/")]
    [InlineData("https://ID.example.test")]
    [InlineData("https://evil.example.test")]
    public async Task DiscoveryWhoseIssuerIsNotExactlyTheIssuerUrlFailsAtStart(string discoveryIssuer)
    {
        _login.IdentityProvider.DiscoveryIssuer = discoveryIssuer;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.FlowCookie);
        Assert.Equal(LogLevel.Error, Assert.Single(_login.Logger.Entries).Level);
    }

    [Fact]
    public async Task DiscoveryWhoseIssuerChangedSinceStartFailsAtCallback()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        _login.IdentityProvider.DiscoveryIssuer = "https://evil.example.test";
        _login.IdentityProvider.RotateSigningKey();
        _login.IdentityProvider.IdToken = _login.IdentityProvider.Sign(
            new Dictionary<string, object>
            {
                ["iss"] = "https://evil.example.test",
                ["aud"] = _login.Configuration.ClientId,
                ["sub"] = LoginHarness.Subject,
                ["nonce"] = NonceOf(cookie),
                ["iat"] = _login.Clock.GetUtcNow().ToUnixTimeSeconds(),
                ["exp"] = (_login.Clock.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
                ["preferred_username"] = "alice",
            },
            SecurityAlgorithms.RsaSha256);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task HttpIssuerUrlOnANonLoopbackHostFailsWithoutContactingIt()
    {
        _login.Configuration.IssuerUrl = "http://id.example.test";

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Empty(_login.IdentityProvider.Requests);
        Assert.False(OidcLoginService.IsConfigured(_login.Configuration));
    }

    [Theory]
    [InlineData("http://localhost:1411")]
    [InlineData("http://127.0.0.1:1411")]
    [InlineData("http://[::1]:1411")]
    public async Task HttpIssuerUrlOnALoopbackHostIsAllowed(string issuerUrl)
    {
        _login.Configuration.IssuerUrl = issuerUrl;

        await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.True(OidcLoginService.IsConfigured(_login.Configuration));
        Assert.Equal(new Uri(issuerUrl + "/.well-known/openid-configuration"), Assert.Single(_login.IdentityProvider.Requests));
    }

    [Fact]
    public async Task CallbackIssEqualToTheIssuerIsAccepted()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = StubIdentityProvider.Issuer }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("https://evil.example.test")]
    [InlineData(StubIdentityProvider.Issuer + "/")]
    [InlineData("")]
    public async Task CallbackIssOfAnotherIssuerFailsWithoutRedeemingTheCode(string iss)
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = iss }, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task CallbackIssOfAnotherIssuerFailsEvenOnAnErrorResponse()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(
            query with { Code = null, Error = "access_denied", Iss = "https://evil.example.test" },
            cookie,
            TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
    }

    [Fact]
    public async Task CallbackWithoutIssFailsWithoutRedeemingTheCodeWhenDiscoveryAdvertisesIt()
    {
        _login.IdentityProvider.AdvertisesIssParameter = true;
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = null }, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task CallbackWithoutIssFailsEvenOnAnErrorResponseWhenDiscoveryAdvertisesIt()
    {
        _login.IdentityProvider.AdvertisesIssParameter = true;
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(
            query with { Code = null, Error = "access_denied", Iss = null },
            cookie,
            TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
    }

    [Fact]
    public async Task CallbackIssEqualToTheIssuerIsAcceptedWhenDiscoveryAdvertisesIt()
    {
        _login.IdentityProvider.AdvertisesIssParameter = true;
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = StubIdentityProvider.Issuer }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task CallbackWithoutIssIsAcceptedWhenDiscoveryDoesNotAdvertiseIt()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = null }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("192.168.1.10:8096")]
    [InlineData("jellyfin.example.test:8096")]
    [InlineData("other.example.test")]
    public async Task StartOnAnotherHostRedirectsToThePublicHostFirst(string requestHost)
    {
        var result = await _login.CreateService().StartAsync(new StartRequest(requestHost), TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal(new Uri("https://jellyfin.example.test/OidcLogin/Start?redirected=true"), result.PublicHostUrl);
        Assert.Null(result.AuthorizationUrl);
        Assert.Null(result.FlowCookie);
        Assert.Empty(_login.IdentityProvider.Requests);
    }

    [Fact]
    public async Task StartRedirectsToThePublicHostUnderItsBaseUrl()
    {
        _login.Configuration.PublicJellyfinUrl = "https://example.test/jellyfin/";

        var result = await _login.CreateService().StartAsync(new StartRequest("192.168.1.10:8096"), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://example.test/jellyfin/OidcLogin/Start?redirected=true"), result.PublicHostUrl);
    }

    [Theory]
    [InlineData("jellyfin.example.test")]
    [InlineData("JELLYFIN.example.test")]
    [InlineData("jellyfin.example.test:443")]
    public async Task StartOnThePublicHostGoesToTheIdentityProvider(string requestHost)
    {
        var result = await _login.CreateService().StartAsync(new StartRequest(requestHost), TestContext.Current.CancellationToken);

        Assert.Null(result.PublicHostUrl);
        Assert.NotNull(result.AuthorizationUrl);
        Assert.True(result.FlowCookie!.Options.Secure);
    }

    [Fact]
    public async Task StartAlreadyRedirectedGoesToTheIdentityProviderEvenIfAProxyRewroteTheHost()
    {
        var result = await _login.CreateService().StartAsync(new StartRequest("127.0.0.1:8096", RedirectedToPublicHost: true), TestContext.Current.CancellationToken);

        Assert.Null(result.PublicHostUrl);
        Assert.NotNull(result.AuthorizationUrl);
    }

    [Fact]
    public async Task FlowCookieSecureFollowsThePublicUrlSchemeNotTheRequestHost()
    {
        _login.Configuration.PublicJellyfinUrl = "http://127.0.0.1:8096";

        var result = await _login.CreateService().StartAsync(new StartRequest("127.0.0.1:8096"), TestContext.Current.CancellationToken);

        Assert.False(result.FlowCookie!.Options.Secure);
        Assert.Equal(FlowCookieProtector.InsecureCookieName, result.FlowCookie.Name);
    }

    public void Dispose() => _login.Dispose();

    private void AssertAccepted(CallbackResult result, bool accepted)
    {
        if (accepted)
        {
            Assert.Null(result.Failure);
        }
        else
        {
            _login.AssertFailedWithoutProvisioning(result);
        }
    }

    /// <summary>
    /// Runs Start and Callback on a new service, the Identity Provider issuing an RS256 ID token with the claims
    /// <paramref name="adjustClaims"/> leaves.
    /// </summary>
    private Task<CallbackResult> CallbackAsync(Action<Dictionary<string, object>>? adjustClaims = null)
        => CallbackSignedAsync(claims => _login.IdentityProvider.Sign(Adjusted(claims, adjustClaims), SecurityAlgorithms.RsaSha256));

    /// <summary>
    /// Runs Start and Callback on a new service, the Identity Provider issuing the ID token <paramref name="sign"/>
    /// makes of the claims.
    /// </summary>
    private Task<CallbackResult> CallbackSignedAsync(Func<Dictionary<string, object>, string> sign)
        => LoginAsync(_login.CreateService(), sign);

    private async Task<CallbackResult> LoginAsync(OidcLoginService service, Func<Dictionary<string, object>, string> sign)
    {
        var (query, cookie) = await _login.SignInAsync(service, sign: sign);
        return await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);
    }

    private string NonceOf(string flowCookie) => new FlowCookieProtector(_login.DataProtection).Unprotect(flowCookie)!.Nonce;

    private int JwksFetches() => _login.IdentityProvider.Requests.Count(u => u == StubIdentityProvider.JwksUri);

    /// <summary>
    /// A JWS header and payload with <paramref name="algorithm"/> and an empty signature, which the caller may append.
    /// </summary>
    private static string UnsignedToken(string algorithm, Dictionary<string, object> claims)
        => Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = algorithm, typ = "JWT", kid = "ed-key" }))
            + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims)) + ".";

    private static Dictionary<string, object> Adjusted(Dictionary<string, object> claims, Action<Dictionary<string, object>>? adjust)
    {
        adjust?.Invoke(claims);
        return claims;
    }
}
