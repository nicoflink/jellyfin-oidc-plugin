using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// The ID token and discovery rules that keep a misbehaving or misconfigured Identity Provider from ever producing
/// a session.
/// </summary>
public sealed class IdTokenAndDiscoveryHardeningTests : IDisposable
{
    private const string Subject = "6a3f1c2e-sub";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly RecordingLogger<OidcLoginService> _logger = new();
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
    };

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

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task MultipleAudiencesWithAzpOfAnotherClientFail()
    {
        var result = await CallbackAsync(claims =>
        {
            claims["aud"] = new[] { "another-client", "jellyfin" };
            claims["azp"] = "another-client";
        });

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task AzpOfAnotherClientFailsEvenWithASingleAudience()
    {
        var result = await CallbackAsync(claims => claims["azp"] = "another-client");

        AssertFailedWithoutProvisioning(result);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-3, false)]
    public async Task ExpIsCheckedWithTwoMinutesSkew(int expMinutesFromNow, bool accepted)
    {
        var result = await CallbackAsync(claims => claims["exp"] = (_clock.GetUtcNow() + TimeSpan.FromMinutes(expMinutesFromNow)).ToUnixTimeSeconds());

        AssertAccepted(result, accepted);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public async Task IatIsCheckedWithTwoMinutesSkew(int iatMinutesFromNow, bool accepted)
    {
        var result = await CallbackAsync(claims => claims["iat"] = (_clock.GetUtcNow() + TimeSpan.FromMinutes(iatMinutesFromNow)).ToUnixTimeSeconds());

        AssertAccepted(result, accepted);
    }

    [Fact]
    public async Task IdTokenWithoutIatFails()
    {
        var result = await CallbackAsync(claims => claims.Remove("iat"));

        AssertFailedWithoutProvisioning(result);
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
        var result = await CallbackSignedAsync(claims => _identityProvider.Sign(claims, algorithm));

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task RsaPssSignatureFails()
    {
        var result = await CallbackSignedAsync(claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSsaPssSha256));

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task HmacSignatureFails()
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" };

        var result = await CallbackSignedAsync(claims => StubIdentityProvider.Sign(claims, new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task UnsignedIdTokenFails()
    {
        var result = await CallbackSignedAsync(claims => UnsignedToken("none", claims));

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task EdDsaSignatureFailsWithAnAdminFacingMessageToRotateTheKey()
    {
        var result = await CallbackSignedAsync(claims => UnsignedToken("EdDSA", claims) + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64)));

        AssertFailedWithoutProvisioning(result);
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains("EdDSA", entry.Message, StringComparison.Ordinal);
        Assert.Contains("rotate", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RS256", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownKidIsValidatedAgainstTheCachedJwks()
    {
        var service = CreateService();

        await LoginAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        var second = await LoginAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        Assert.Null(second.Failure);
        Assert.Equal(1, JwksFetches());
    }

    [Fact]
    public async Task UnknownKidRefetchesTheJwksOnceSoARotatedKeyIsAccepted()
    {
        var service = CreateService();
        await LoginAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        _identityProvider.RotateSigningKey();

        var result = await LoginAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        Assert.Null(result.Failure);
        Assert.Equal(2, JwksFetches());
    }

    [Fact]
    public async Task UnknownKidMissingAfterTheRefetchFailsAndIsNotRefetchedAgainRightAway()
    {
        var service = CreateService();
        var unpublished = new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "unpublished-key" }, SecurityAlgorithms.RsaSha256);

        var first = await LoginAsync(service, claims => StubIdentityProvider.Sign(claims, unpublished));
        var second = await LoginAsync(service, claims => StubIdentityProvider.Sign(claims, unpublished));

        Assert.Equal(FailureCategory.Failed, first.Failure?.Category);
        Assert.Equal(FailureCategory.Failed, second.Failure?.Category);
        Assert.Equal(2, JwksFetches());
        Assert.Empty(_users.All);
    }

    [Theory]
    [InlineData(StubIdentityProvider.Issuer + "/")]
    [InlineData("https://ID.example.test")]
    [InlineData("https://evil.example.test")]
    public async Task DiscoveryWhoseIssuerIsNotExactlyTheIssuerUrlFailsAtStart(string discoveryIssuer)
    {
        _identityProvider.DiscoveryIssuer = discoveryIssuer;

        var result = await CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.FlowCookie);
        Assert.Equal(LogLevel.Error, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task DiscoveryWhoseIssuerChangedSinceStartFailsAtCallback()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));
        _identityProvider.DiscoveryIssuer = "https://evil.example.test";
        _identityProvider.RotateSigningKey();
        _identityProvider.IdToken = _identityProvider.Sign(
            new Dictionary<string, object>
            {
                ["iss"] = "https://evil.example.test",
                ["aud"] = _configuration.ClientId,
                ["sub"] = Subject,
                ["nonce"] = NonceOf(cookie),
                ["iat"] = _clock.GetUtcNow().ToUnixTimeSeconds(),
                ["exp"] = (_clock.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
                ["preferred_username"] = "alice",
            },
            SecurityAlgorithms.RsaSha256);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task HttpIssuerUrlOnANonLoopbackHostFailsWithoutContactingIt()
    {
        _configuration.IssuerUrl = "http://id.example.test";

        var result = await CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Empty(_identityProvider.Requests);
        Assert.False(OidcLoginService.IsConfigured(_configuration));
    }

    [Theory]
    [InlineData("http://localhost:1411")]
    [InlineData("http://127.0.0.1:1411")]
    [InlineData("http://[::1]:1411")]
    public async Task HttpIssuerUrlOnALoopbackHostIsAllowed(string issuerUrl)
    {
        _configuration.IssuerUrl = issuerUrl;

        await CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.True(OidcLoginService.IsConfigured(_configuration));
        Assert.Equal(new Uri(issuerUrl + "/.well-known/openid-configuration"), Assert.Single(_identityProvider.Requests));
    }

    [Fact]
    public async Task CallbackIssEqualToTheIssuerIsAccepted()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = StubIdentityProvider.Issuer }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("https://evil.example.test")]
    [InlineData(StubIdentityProvider.Issuer + "/")]
    [InlineData("")]
    public async Task CallbackIssOfAnotherIssuerFailsWithoutRedeemingTheCode(string iss)
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = iss }, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
        Assert.Null(_identityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task CallbackIssOfAnotherIssuerFailsEvenOnAnErrorResponse()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(
            query with { Code = null, Error = "access_denied", Iss = "https://evil.example.test" },
            cookie,
            TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
    }

    [Fact]
    public async Task CallbackWithoutIssFailsWithoutRedeemingTheCodeWhenDiscoveryAdvertisesIt()
    {
        _identityProvider.AdvertisesIssParameter = true;
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = null }, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
        Assert.Null(_identityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task CallbackWithoutIssFailsEvenOnAnErrorResponseWhenDiscoveryAdvertisesIt()
    {
        _identityProvider.AdvertisesIssParameter = true;
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(
            query with { Code = null, Error = "access_denied", Iss = null },
            cookie,
            TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
    }

    [Fact]
    public async Task CallbackIssEqualToTheIssuerIsAcceptedWhenDiscoveryAdvertisesIt()
    {
        _identityProvider.AdvertisesIssParameter = true;
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = StubIdentityProvider.Issuer }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task CallbackWithoutIssIsAcceptedWhenDiscoveryDoesNotAdvertiseIt()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => _identityProvider.Sign(claims, SecurityAlgorithms.RsaSha256));

        var result = await service.CallbackAsync(query with { Iss = null }, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("192.168.1.10:8096")]
    [InlineData("jellyfin.example.test:8096")]
    [InlineData("other.example.test")]
    public async Task StartOnAnotherHostRedirectsToThePublicHostFirst(string requestHost)
    {
        var result = await CreateService().StartAsync(new StartRequest(requestHost), TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal(new Uri("https://jellyfin.example.test/OidcLogin/Start?redirected=true"), result.PublicHostUrl);
        Assert.Null(result.AuthorizationUrl);
        Assert.Null(result.FlowCookie);
        Assert.Empty(_identityProvider.Requests);
    }

    [Fact]
    public async Task StartRedirectsToThePublicHostUnderItsBaseUrl()
    {
        _configuration.PublicJellyfinUrl = "https://example.test/jellyfin/";

        var result = await CreateService().StartAsync(new StartRequest("192.168.1.10:8096"), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://example.test/jellyfin/OidcLogin/Start?redirected=true"), result.PublicHostUrl);
    }

    [Theory]
    [InlineData("jellyfin.example.test")]
    [InlineData("JELLYFIN.example.test")]
    [InlineData("jellyfin.example.test:443")]
    public async Task StartOnThePublicHostGoesToTheIdentityProvider(string requestHost)
    {
        var result = await CreateService().StartAsync(new StartRequest(requestHost), TestContext.Current.CancellationToken);

        Assert.Null(result.PublicHostUrl);
        Assert.NotNull(result.AuthorizationUrl);
        Assert.True(result.FlowCookie!.Options.Secure);
    }

    [Fact]
    public async Task StartAlreadyRedirectedGoesToTheIdentityProviderEvenIfAProxyRewroteTheHost()
    {
        var result = await CreateService().StartAsync(new StartRequest("127.0.0.1:8096", RedirectedToPublicHost: true), TestContext.Current.CancellationToken);

        Assert.Null(result.PublicHostUrl);
        Assert.NotNull(result.AuthorizationUrl);
    }

    [Fact]
    public async Task FlowCookieSecureFollowsThePublicUrlSchemeNotTheRequestHost()
    {
        _configuration.PublicJellyfinUrl = "http://127.0.0.1:8096";

        var result = await CreateService().StartAsync(new StartRequest("127.0.0.1:8096"), TestContext.Current.CancellationToken);

        Assert.False(result.FlowCookie!.Options.Secure);
        Assert.Equal(FlowCookieProtector.InsecureCookieName, result.FlowCookie.Name);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    private void AssertAccepted(CallbackResult result, bool accepted)
    {
        if (accepted)
        {
            Assert.Null(result.Failure);
        }
        else
        {
            AssertFailedWithoutProvisioning(result);
        }
    }

    private void AssertFailedWithoutProvisioning(CallbackResult result)
    {
        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.CompleteUrl);
        Assert.Empty(_users.All);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    /// <summary>
    /// Runs Start and Callback on a new service, the Identity Provider issuing an RS256 ID token with the claims
    /// <paramref name="adjustClaims"/> leaves.
    /// </summary>
    private Task<CallbackResult> CallbackAsync(Action<Dictionary<string, object>>? adjustClaims = null)
        => CallbackSignedAsync(claims => _identityProvider.Sign(Adjusted(claims, adjustClaims), SecurityAlgorithms.RsaSha256));

    /// <summary>
    /// Runs Start and Callback on a new service, the Identity Provider issuing the ID token <paramref name="sign"/>
    /// makes of the claims.
    /// </summary>
    private Task<CallbackResult> CallbackSignedAsync(Func<Dictionary<string, object>, string> sign)
        => LoginAsync(CreateService(), sign);

    private async Task<CallbackResult> LoginAsync(OidcLoginService service, Func<Dictionary<string, object>, string> sign)
    {
        var (query, cookie) = await SignInAsync(service, sign);
        return await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);
    }

    private string NonceOf(string flowCookie) => new FlowCookieProtector(_dataProtection).Unprotect(flowCookie)!.Nonce;

    private int JwksFetches() => _identityProvider.Requests.Count(u => u == StubIdentityProvider.JwksUri);

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

    /// <summary>
    /// Starts a login, then plays the Identity Provider: <paramref name="sign"/> turns the claims for the Start's
    /// nonce into the ID token the token endpoint returns.
    /// </summary>
    private async Task<(CallbackQuery Query, string FlowCookie)> SignInAsync(OidcLoginService service, Func<Dictionary<string, object>, string> sign)
    {
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = _clock.GetUtcNow();
        _identityProvider.IdToken = sign(new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = _configuration.ClientId,
            ["sub"] = Subject,
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        });
        return (new CallbackQuery("authorization-code", authorize["state"]), start.FlowCookie!.Value);
    }

    private OidcLoginService CreateService(IdentityProviderClient? identityProvider = null)
        => new(
            identityProvider ?? CreateIdentityProviderClient(),
            new FlowCookieProtector(_dataProtection),
            _users.Manager,
            Substitute.For<ISessionManager>(),
            new LinkedIdentityStore(_dataFolder),
            new HandoffCodes(_clock),
            _clock,
            () => _configuration,
            new JellyfinWebClient("12.4.0"),
            _logger);

    private IdentityProviderClient CreateIdentityProviderClient()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new IdentityProviderClient(httpClientFactory);
    }
}
