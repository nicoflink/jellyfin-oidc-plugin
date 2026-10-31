using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Every failure cause: its category, its HTTP status, and exactly one log entry tagged with its reference code that
/// holds no tokens, codes, secrets or cookies.
/// </summary>
public sealed partial class FailureHandlingTests : IDisposable
{
    private const string ClientSecret = "client-secret-0123456789";
    private const string AuthorizationCode = "authorization-code-0123456789";

    private readonly LoginHarness _login = new();
    private readonly List<string> _secrets = [ClientSecret, AuthorizationCode];

    public FailureHandlingTests()
    {
        _login.Configuration.ClientSecret = ClientSecret;
    }

    [Fact]
    public async Task UnconfiguredPluginIsFailedAndLoggedAsAnError()
    {
        _login.Configuration.IssuerUrl = string.Empty;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
    }

    [Fact]
    public async Task UnreachableDiscoveryIsUnavailableAndLoggedAsAnError()
    {
        _login.IdentityProvider.IsDown = true;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Unavailable, 502, LogLevel.Error);
    }

    [Fact]
    public async Task DiscoveryWithoutAuthorizationEndpointIsFailedAndLoggedAsAnError()
    {
        _login.IdentityProvider.OmitAuthorizationEndpoint = true;

        var result = await _login.CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
    }

    [Fact]
    public async Task MissingFlowCookieIsExpiredAndLoggedAsInformation()
    {
        var service = _login.CreateService();
        var (query, _) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, null, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task StateMismatchIsExpiredAndLoggedAsInformation()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query with { State = "another-state" }, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task AccessDeniedIsCancelledAndLoggedAsInformationWithItsDescription()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(new CallbackQuery(null, query.State, "access_denied", "The user cancelled"), cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Cancelled, 400, LogLevel.Information);
        Assert.Contains("access_denied", entry.Message, StringComparison.Ordinal);
        Assert.Contains("The user cancelled", entry.Message, StringComparison.Ordinal);
        Assert.Null(_login.IdentityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task AccessDeniedIsCancelledEvenWhileDiscoveryIsUnreachable()
    {
        var (query, cookie) = await SignInAsync(_login.CreateService());

        // Jellyfin restarted since Start, so discovery isn't cached, and the Identity Provider has gone down.
        _login.IdentityProvider.IsDown = true;
        var result = await _login.CreateService().CallbackAsync(new CallbackQuery(null, query.State, "access_denied", "The user cancelled"), cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Cancelled, 400, LogLevel.Information);
    }

    [Fact]
    public async Task AccessDeniedWithAnotherStateIsExpired()
    {
        var service = _login.CreateService();
        var (_, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(new CallbackQuery(null, "another-state", "access_denied", null), cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task OtherIdentityProviderErrorIsFailedAndLoggedSanitisedAndTruncated()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);
        var description = "Client\r\nFAKE LOG LINE\u001b[31m is not allowed " + new string('x', 2000);

        var result = await service.CallbackAsync(new CallbackQuery(null, query.State, "unauthorized_client", description), cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
        Assert.Contains("unauthorized_client", entry.Message, StringComparison.Ordinal);
        Assert.Contains("FAKE LOG LINE", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', entry.Message);
        Assert.DoesNotContain('\r', entry.Message);
        Assert.DoesNotContain('\u001b', entry.Message);
        Assert.DoesNotContain(new string('x', 300), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackWithoutCodeIsFailedAndLoggedAsAWarning()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query with { Code = null }, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
    }

    [Fact]
    public async Task UnreachableTokenEndpointIsUnavailableAndLoggedAsAnError()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);
        _login.IdentityProvider.IsDown = true;

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Unavailable, 502, LogLevel.Error);
    }

    [Fact]
    public async Task RejectedCodeExchangeIsFailedAndLoggedAsAnErrorWithTheTokenError()
    {
        _login.IdentityProvider.TokenStatus = System.Net.HttpStatusCode.BadRequest;
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
        Assert.Contains("invalid_grant", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidIdTokenIsFailedAndLoggedAsAWarning()
    {
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["aud"] = "another-client");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
        Assert.DoesNotContain(LoginHarness.Subject, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusalAfterAValidIdTokenIsLoggedWithTheSubjectAndUsernameClaim()
    {
        _login.Users.Add("alice");
        var service = _login.CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.NotSetUp, 403, LogLevel.Warning);
        Assert.Equal($"'{LoginHarness.Subject}'", entry.Values["Subject"]);
        Assert.Equal("'alice'", entry.Values["Username"]);
    }

    [Fact]
    public async Task ExpiredHandoffCodeIsExpiredAndLoggedAsInformation()
    {
        var service = _login.CreateService();
        var (code, cookie) = await SignInToCompleteAsync(service);
        _login.Clock.Advance(TimeSpan.FromSeconds(30));

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie), TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task CompleteFromAnotherOriginIsFailedAndLoggedAsAWarning()
    {
        var service = _login.CreateService();
        var (code, cookie) = await SignInToCompleteAsync(service);

        var result = await service.CompleteAsync(LoginHarness.Completion(code, cookie) with { Origin = "https://evil.example.test" }, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
    }

    [Fact]
    public async Task EachFailureHasItsOwnReference()
    {
        _login.Configuration.IssuerUrl = string.Empty;
        var service = _login.CreateService();

        var first = await service.StartAsync(TestContext.Current.CancellationToken);
        var second = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Failure!.Reference, second.Failure!.Reference);
    }

    public void Dispose() => _login.Dispose();

    [GeneratedRegex("^[0-9A-Z]{8}$")]
    private static partial Regex ReferenceFormat();

    /// <summary>
    /// Asserts the failure's category and status, and that exactly one entry was logged for it: at
    /// <paramref name="level"/>, tagged with its reference code, naming its category, and free of every secret the
    /// login handled.
    /// </summary>
    private RecordingLogger<OidcLoginService>.Entry AssertLoggedOnce(LoginFailure? failure, FailureCategory category, int status, LogLevel level)
    {
        Assert.NotNull(failure);
        Assert.Equal(category, failure.Category);
        Assert.Equal(status, failure.StatusCode);
        Assert.Matches(ReferenceFormat(), failure.Reference);

        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Equal(failure.Reference, entry.Values["Reference"]);
        Assert.Contains(failure.Reference, entry.Message, StringComparison.Ordinal);
        Assert.Contains(category.ToString(), entry.Message, StringComparison.Ordinal);
        foreach (var secret in _secrets)
        {
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            Assert.All(entry.Values.Values, v => Assert.DoesNotContain(secret, v?.ToString() ?? string.Empty, StringComparison.Ordinal));
        }

        Assert.Null(entry.Exception);
        return entry;
    }

    /// <summary>
    /// Starts a login and plays the Identity Provider (see <see cref="LoginHarness.SignInAsync"/>). Every value the
    /// browser carries back is remembered as a secret the log must not hold.
    /// </summary>
    private async Task<(CallbackQuery Query, string FlowCookie)> SignInAsync(OidcLoginService service, Action<Dictionary<string, object>>? adjustClaims = null)
    {
        var browser = await _login.SignInAsync(service, adjustClaims);
        _secrets.AddRange([_login.IdentityProvider.IdToken!, browser.FlowCookie, browser.Authorize["state"]!, browser.Authorize["nonce"]!, browser.Authorize["code_challenge"]!]);
        return (browser.Query with { Code = AuthorizationCode }, browser.FlowCookie);
    }

    /// <summary>
    /// Runs Start and Callback, returning the handoff code and flow cookie the Complete page holds, the code
    /// remembered as a secret.
    /// </summary>
    private async Task<(string HandoffCode, string FlowCookie)> SignInToCompleteAsync(OidcLoginService service)
    {
        var (query, cookie) = await SignInAsync(service);
        var callback = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);
        _secrets.Add(callback.HandoffCode!);
        return (callback.HandoffCode!, cookie);
    }
}
