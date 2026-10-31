using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LoginCallbackTests : IDisposable
{
    private const string Subject = "6a3f1c2e-sub";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
    };

    [Fact]
    public async Task NewSubjectIsProvisionedAndLinkedAndGetsAHandoffCode()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.False(string.IsNullOrEmpty(result.HandoffCode));
        var user = Assert.Single(_users.All);
        Assert.Equal("alice", user.Username);
        var link = Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll());
        Assert.Equal(StubIdentityProvider.Issuer, link.Issuer);
        Assert.Equal(Subject, link.Subject);
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal(_clock.GetUtcNow(), link.LinkedAt);
        Assert.Equal(_clock.GetUtcNow(), link.LastLoginAt);
    }

    [Fact]
    public async Task RedirectsToCompleteWithTheHandoffCodeInTheFragment()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal("https://jellyfin.example.test/OidcLogin/Complete", result.CompleteUrl!.GetLeftPart(UriPartial.Path));
        Assert.Equal("#code=" + result.HandoffCode, result.CompleteUrl.Fragment);
        Assert.Empty(result.CompleteUrl.Query);
    }

    [Fact]
    public async Task RedeemsTheCodeWithClientSecretBasicAndThePkceVerifier()
    {
        _configuration.ClientSecret = "s3cret:with/special";
        var service = CreateService();
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
        var (query, cookie) = await SignInAsync(service, start: start);

        await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var request = _identityProvider.LastTokenRequest!;
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("jellyfin:s3cret%3Awith%2Fspecial")), request.Authorization);
        Assert.Equal("authorization_code", request.Form["grant_type"]);
        Assert.Equal("authorization-code", request.Form["code"]);
        Assert.Equal("https://jellyfin.example.test/OidcLogin/Callback", request.Form["redirect_uri"]);
        var challenge = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query)["code_challenge"];
        Assert.Equal(challenge, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(request.Form["code_verifier"]))));
        Assert.False(request.Form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task ClaimsComeFromTheIdTokenOnly()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.All(_identityProvider.Requests, u => Assert.Contains(u, new[] { StubIdentityProvider.DiscoveryUrl, StubIdentityProvider.JwksUri, StubIdentityProvider.TokenEndpoint }));
    }

    [Fact]
    public async Task LinkedSubjectResolvesToItsUserAndRecordsTheLogin()
    {
        var existing = _users.Add("renamed-in-jellyfin");
        var linkedAt = _clock.GetUtcNow() - TimeSpan.FromDays(3);
        new LinkedIdentityStore(_dataFolder).Add(new LinkedIdentity(StubIdentityProvider.Issuer, Subject, existing.Id, linkedAt, linkedAt));
        var codes = new HandoffCodes(_clock);
        var service = CreateService(codes);
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal(existing.Id, codes.Consume(result.HandoffCode!, cookie));
        Assert.Single(_users.All);
        var link = Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll());
        Assert.Equal(linkedAt, link.LinkedAt);
        Assert.Equal(_clock.GetUtcNow(), link.LastLoginAt);
    }

    [Fact]
    public async Task SecondLoginOfAProvisionedSubjectReachesTheSameUser()
    {
        var codes = new HandoffCodes(_clock);
        var service = CreateService(codes);
        var (firstQuery, firstCookie) = await SignInAsync(service);
        var first = await service.CallbackAsync(firstQuery, firstCookie, TestContext.Current.CancellationToken);
        var (secondQuery, secondCookie) = await SignInAsync(service);

        var second = await service.CallbackAsync(secondQuery, secondCookie, TestContext.Current.CancellationToken);

        Assert.Equal(codes.Consume(first.HandoffCode!, firstCookie), codes.Consume(second.HandoffCode!, secondCookie));
        Assert.Single(_users.All);
    }

    [Fact]
    public async Task StateMismatchIsExpiredAndNeverRedeemsTheCode()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query with { State = "another-state" }, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_identityProvider.LastTokenRequest);
        Assert.Empty(_users.All);
    }

    [Fact]
    public async Task MissingFlowCookieIsExpired()
    {
        var service = CreateService();
        var (query, _) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, null, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_identityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task FlowCookieFromAnotherServerIsExpired()
    {
        var service = CreateService();
        var (query, _) = await SignInAsync(service);
        var forged = new FlowCookieProtector(new EphemeralDataProtectionProvider()).Protect(new FlowState(query.State!, "nonce", "verifier", _clock.GetUtcNow() + TimeSpan.FromMinutes(10)));

        var result = await service.CallbackAsync(query, forged, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_identityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task FlowCookieOlderThanTenMinutesIsExpired()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
    }

    [Fact]
    public async Task IdTokenSignedWithAKeyOutsideTheJwksFails()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, withUnpublishedKey: true);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenFromAnotherIssuerFails()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["iss"] = "https://evil.example.test");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenForAnotherClientFails()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["aud"] = "another-client");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenWhoseAudiencesContainTheClientIsAccepted()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["aud"] = new[] { "another-client", "jellyfin" });

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ExpiredIdTokenFails()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["exp"] = (_clock.GetUtcNow() - TimeSpan.FromMinutes(3)).ToUnixTimeSeconds());

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenWithoutExpFails()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims.Remove("exp"));

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Theory]
    [InlineData("another-nonce")]
    [InlineData(null)]
    public async Task IdTokenWithAnotherNonceFails(string? nonce)
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims =>
        {
            claims.Remove("nonce");
            if (nonce is not null)
            {
                claims["nonce"] = nonce;
            }
        });

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task RejectedCodeFails()
    {
        _identityProvider.TokenStatus = HttpStatusCode.BadRequest;
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task UnreachableTokenEndpointIsUnavailable()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);
        _identityProvider.IsDown = true;

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Unavailable, result.Failure?.Category);
        Assert.Empty(_users.All);
    }

    [Fact]
    public async Task HandoffCodeIsBoundToTheFlowCookieAndSingleUse()
    {
        var codes = new HandoffCodes(_clock);
        var service = CreateService(codes);
        var (query, cookie) = await SignInAsync(service);
        var code = (await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken)).HandoffCode!;
        var (otherQuery, otherCookie) = await SignInAsync(service);
        var otherCode = (await service.CallbackAsync(otherQuery, otherCookie, TestContext.Current.CancellationToken)).HandoffCode!;

        Assert.Null(codes.Consume(otherCode, cookie));
        Assert.Null(codes.Consume(otherCode, otherCookie));
        Assert.Equal(_users.All[0].Id, codes.Consume(code, cookie));
        Assert.Null(codes.Consume(code, cookie));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public async Task HandoffCodeLivesThirtySeconds(int secondsLater, bool accepted)
    {
        var codes = new HandoffCodes(_clock);
        var service = CreateService(codes);
        var (query, cookie) = await SignInAsync(service);
        var code = (await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken)).HandoffCode!;

        _clock.Advance(TimeSpan.FromSeconds(secondsLater));

        Assert.Equal(accepted, codes.Consume(code, cookie) is not null);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    /// <summary>
    /// Starts a login, then plays the Identity Provider: issues an ID token for the Start's nonce and returns
    /// the callback query and flow cookie the browser would bring back.
    /// </summary>
    private async Task<(CallbackQuery Query, string FlowCookie)> SignInAsync(
        OidcLoginService service,
        Action<Dictionary<string, object>>? adjustClaims = null,
        bool withUnpublishedKey = false,
        StartResult? start = null)
    {
        start ??= await service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = _clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = _configuration.ClientId,
            ["sub"] = Subject,
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        };
        adjustClaims?.Invoke(claims);
        _identityProvider.IdToken = _identityProvider.Sign(claims, withUnpublishedKey);
        return (new CallbackQuery("authorization-code", authorize["state"]), start.FlowCookie!.Value);
    }

    private void AssertFailedWithoutProvisioning(CallbackResult result)
    {
        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.CompleteUrl);
        Assert.Empty(_users.All);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    private OidcLoginService CreateService(HandoffCodes? handoffCodes = null)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new OidcLoginService(
            new IdentityProviderClient(httpClientFactory),
            new FlowCookieProtector(_dataProtection),
            _users.Manager,
            new LinkedIdentityStore(_dataFolder),
            handoffCodes ?? new HandoffCodes(_clock),
            _clock,
            () => _configuration,
            NullLogger<OidcLoginService>.Instance);
    }
}
