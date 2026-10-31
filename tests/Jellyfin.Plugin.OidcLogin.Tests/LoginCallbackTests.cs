using System;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LoginCallbackTests : IDisposable
{
    private readonly LoginHarness _login = new();

    [Fact]
    public async Task NewSubjectIsProvisionedAndLinkedAndGetsAHandoffCode()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.False(string.IsNullOrEmpty(result.HandoffCode));
        var user = Assert.Single(_login.Users.All);
        Assert.Equal("alice", user.Username);
        var link = Assert.Single(_login.LinkedIdentities.GetAll());
        Assert.Equal(StubIdentityProvider.Issuer, link.Issuer);
        Assert.Equal(LoginHarness.Subject, link.Subject);
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal(_login.Clock.GetUtcNow(), link.LinkedAt);
        Assert.Equal(_login.Clock.GetUtcNow(), link.LastLoginAt);
    }

    [Fact]
    public async Task RedirectsToCompleteWithTheHandoffCodeInTheFragment()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal("https://jellyfin.example.test/OidcLogin/Complete", result.CompleteUrl!.GetLeftPart(UriPartial.Path));
        Assert.Equal("#code=" + result.HandoffCode, result.CompleteUrl.Fragment);
        Assert.Empty(result.CompleteUrl.Query);
    }

    [Fact]
    public async Task RedeemsTheCodeWithClientSecretBasicAndThePkceVerifier()
    {
        _login.Configuration.ClientSecret = "s3cret:with/special";
        var service = _login.CreateService();
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
        var (query, cookie) = await _login.SignInAsync(service, start: start);

        await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var request = _login.IdentityProvider.LastTokenRequest!;
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
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);

        await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.All(_login.IdentityProvider.Requests, u => Assert.Contains(u, new[] { StubIdentityProvider.DiscoveryUrl, StubIdentityProvider.JwksUri, StubIdentityProvider.TokenEndpoint }));
    }

    [Fact]
    public async Task LinkedSubjectResolvesToItsUser()
    {
        var existing = _login.Users.Add("renamed-in-jellyfin");
        var linkedAt = _login.Clock.GetUtcNow() - TimeSpan.FromDays(3);
        _login.LinkedIdentities.Add(new LinkedIdentity(StubIdentityProvider.Issuer, LoginHarness.Subject, existing.Id, linkedAt, linkedAt));
        var codes = new HandoffCodes(_login.Clock);
        var service = _login.CreateService(handoffCodes: codes);
        var (query, cookie) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal(existing.Id, codes.Consume(result.HandoffCode!, cookie)?.UserId);
        Assert.Single(_login.Users.All);
        var link = Assert.Single(_login.LinkedIdentities.GetAll());
        Assert.Equal(linkedAt, link.LinkedAt);

        // The login only counts once Complete signs in.
        Assert.Equal(linkedAt, link.LastLoginAt);
    }

    [Fact]
    public async Task SecondLoginOfAProvisionedSubjectReachesTheSameUser()
    {
        var codes = new HandoffCodes(_login.Clock);
        var service = _login.CreateService(handoffCodes: codes);
        var (firstQuery, firstCookie) = await _login.SignInAsync(service);
        var first = await service.CallbackAsync(firstQuery, firstCookie, TestContext.Current.CancellationToken);
        var (secondQuery, secondCookie) = await _login.SignInAsync(service);

        var second = await service.CallbackAsync(secondQuery, secondCookie, TestContext.Current.CancellationToken);

        Assert.Equal(codes.Consume(first.HandoffCode!, firstCookie)?.UserId, codes.Consume(second.HandoffCode!, secondCookie)?.UserId);
        Assert.Single(_login.Users.All);
    }

    [Fact]
    public async Task StateMismatchIsExpiredAndNeverRedeemsTheCode()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query with { State = "another-state" }, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
        Assert.Empty(_login.Users.All);
    }

    [Fact]
    public async Task MissingFlowCookieIsExpired()
    {
        var service = _login.CreateService();
        var (query, _) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query, null, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task FlowCookieFromAnotherServerIsExpired()
    {
        var service = _login.CreateService();
        var (query, _) = await _login.SignInAsync(service);
        var forged = new FlowCookieProtector(new EphemeralDataProtectionProvider()).Protect(new FlowState(query.State!, "nonce", "verifier", _login.Clock.GetUtcNow() + TimeSpan.FromMinutes(10)));

        var result = await service.CallbackAsync(query, forged, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task FlowCookieOlderThanTenMinutesIsExpired()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);
        _login.Clock.Advance(TimeSpan.FromMinutes(10));

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Expired, result.Failure?.Category);
    }

    [Fact]
    public async Task IdTokenSignedWithAKeyOutsideTheJwksFails()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, sign: claims => _login.IdentityProvider.Sign(claims, withUnpublishedKey: true));

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenFromAnotherIssuerFails()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, claims => claims["iss"] = "https://evil.example.test");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenForAnotherClientFails()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, claims => claims["aud"] = "another-client");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task ExpiredIdTokenFails()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, claims => claims["exp"] = (_login.Clock.GetUtcNow() - TimeSpan.FromMinutes(3)).ToUnixTimeSeconds());

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task IdTokenWithoutExpFails()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, claims => claims.Remove("exp"));

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Theory]
    [InlineData("another-nonce")]
    [InlineData(null)]
    public async Task IdTokenWithAnotherNonceFails(string? nonce)
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service, claims =>
        {
            claims.Remove("nonce");
            if (nonce is not null)
            {
                claims["nonce"] = nonce;
            }
        });

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task RejectedCodeFails()
    {
        _login.IdentityProvider.TokenStatus = HttpStatusCode.BadRequest;
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        _login.AssertFailedWithoutProvisioning(result);
    }

    [Fact]
    public async Task UnreachableTokenEndpointIsUnavailable()
    {
        var service = _login.CreateService();
        var (query, cookie) = await _login.SignInAsync(service);
        _login.IdentityProvider.IsDown = true;

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        Assert.Equal(FailureCategory.Unavailable, result.Failure?.Category);
        Assert.Empty(_login.Users.All);
    }

    [Fact]
    public async Task HandoffCodeIsBoundToTheFlowCookieAndSingleUse()
    {
        var codes = new HandoffCodes(_login.Clock);
        var service = _login.CreateService(handoffCodes: codes);
        var (query, cookie) = await _login.SignInAsync(service);
        var code = (await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken)).HandoffCode!;
        var (otherQuery, otherCookie) = await _login.SignInAsync(service);
        var otherCode = (await service.CallbackAsync(otherQuery, otherCookie, TestContext.Current.CancellationToken)).HandoffCode!;

        Assert.Null(codes.Consume(otherCode, cookie));
        Assert.Null(codes.Consume(otherCode, otherCookie));
        Assert.Equal(_login.Users.All[0].Id, codes.Consume(code, cookie)?.UserId);
        Assert.Null(codes.Consume(code, cookie));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public async Task HandoffCodeLivesThirtySeconds(int secondsLater, bool accepted)
    {
        var codes = new HandoffCodes(_login.Clock);
        var service = _login.CreateService(handoffCodes: codes);
        var (query, cookie) = await _login.SignInAsync(service);
        var code = (await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken)).HandoffCode!;

        _login.Clock.Advance(TimeSpan.FromSeconds(secondsLater));

        Assert.Equal(accepted, codes.Consume(code, cookie) is not null);
    }

    public void Dispose() => _login.Dispose();
}
