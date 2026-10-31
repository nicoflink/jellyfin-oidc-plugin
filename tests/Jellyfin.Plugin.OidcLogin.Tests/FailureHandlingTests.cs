using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
    private const string Subject = "6a3f1c2e-sub";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly RecordingLogger<OidcLoginService> _logger = new();
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly List<string> _secrets = [ClientSecret, AuthorizationCode];
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = ClientSecret,
    };

    [Fact]
    public async Task UnconfiguredPluginIsFailedAndLoggedAsAnError()
    {
        _configuration.IssuerUrl = string.Empty;

        var result = await CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
    }

    [Fact]
    public async Task UnreachableDiscoveryIsUnavailableAndLoggedAsAnError()
    {
        _identityProvider.IsDown = true;

        var result = await CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Unavailable, 502, LogLevel.Error);
    }

    [Fact]
    public async Task DiscoveryWithoutAuthorizationEndpointIsFailedAndLoggedAsAnError()
    {
        _identityProvider.OmitAuthorizationEndpoint = true;

        var result = await CreateService().StartAsync(TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
    }

    [Fact]
    public async Task MissingFlowCookieIsExpiredAndLoggedAsInformation()
    {
        var service = CreateService();
        var (query, _) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, null, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task StateMismatchIsExpiredAndLoggedAsInformation()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query with { State = "another-state" }, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task AccessDeniedIsCancelledAndLoggedAsInformationWithItsDescription()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(new CallbackQuery(null, query.State, "access_denied", "The user cancelled"), cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Cancelled, 400, LogLevel.Information);
        Assert.Contains("access_denied", entry.Message, StringComparison.Ordinal);
        Assert.Contains("The user cancelled", entry.Message, StringComparison.Ordinal);
        Assert.Null(_identityProvider.LastTokenRequest);
    }

    [Fact]
    public async Task AccessDeniedWithAnotherStateIsExpired()
    {
        var service = CreateService();
        var (_, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(new CallbackQuery(null, "another-state", "access_denied", null), cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task OtherIdentityProviderErrorIsFailedAndLoggedSanitisedAndTruncated()
    {
        var service = CreateService();
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
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query with { Code = null }, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
    }

    [Fact]
    public async Task UnreachableTokenEndpointIsUnavailableAndLoggedAsAnError()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);
        _identityProvider.IsDown = true;

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Unavailable, 502, LogLevel.Error);
    }

    [Fact]
    public async Task RejectedCodeExchangeIsFailedAndLoggedAsAnErrorWithTheTokenError()
    {
        _identityProvider.TokenStatus = System.Net.HttpStatusCode.BadRequest;
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Error);
        Assert.Contains("invalid_grant", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidIdTokenIsFailedAndLoggedAsAWarning()
    {
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service, claims => claims["aud"] = "another-client");

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
        Assert.DoesNotContain(Subject, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusalAfterAValidIdTokenIsLoggedWithTheSubjectAndUsernameClaim()
    {
        _users.Add("alice");
        var service = CreateService();
        var (query, cookie) = await SignInAsync(service);

        var result = await service.CallbackAsync(query, cookie, TestContext.Current.CancellationToken);

        var entry = AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
        Assert.Equal($"'{Subject}'", entry.Values["Subject"]);
        Assert.Equal("'alice'", entry.Values["Username"]);
    }

    [Fact]
    public async Task ExpiredHandoffCodeIsExpiredAndLoggedAsInformation()
    {
        var service = CreateService();
        var (code, cookie) = await SignInToCompleteAsync(service);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await service.CompleteAsync(Completion(code, cookie), TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Expired, 400, LogLevel.Information);
    }

    [Fact]
    public async Task CompleteFromAnotherOriginIsFailedAndLoggedAsAWarning()
    {
        var service = CreateService();
        var (code, cookie) = await SignInToCompleteAsync(service);

        var result = await service.CompleteAsync(Completion(code, cookie) with { Origin = "https://evil.example.test" }, TestContext.Current.CancellationToken);

        AssertLoggedOnce(result.Failure, FailureCategory.Failed, 500, LogLevel.Warning);
    }

    [Fact]
    public async Task EachFailureHasItsOwnReference()
    {
        _configuration.IssuerUrl = string.Empty;
        var service = CreateService();

        var first = await service.StartAsync(TestContext.Current.CancellationToken);
        var second = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Failure!.Reference, second.Failure!.Reference);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

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

        var entry = Assert.Single(_logger.Entries);
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
    /// Starts a login and plays the Identity Provider, issuing an ID token for the Start's nonce. Every value the
    /// browser carries back is remembered as a secret the log must not hold.
    /// </summary>
    private async Task<(CallbackQuery Query, string FlowCookie)> SignInAsync(OidcLoginService service, Action<Dictionary<string, object>>? adjustClaims = null)
    {
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
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
        _identityProvider.IdToken = _identityProvider.Sign(claims);
        var flowCookie = start.FlowCookie!.Value;
        _secrets.AddRange([_identityProvider.IdToken, flowCookie, authorize["state"]!, authorize["nonce"]!, authorize["code_challenge"]!]);
        return (new CallbackQuery(AuthorizationCode, authorize["state"]), flowCookie);
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

    private static CompleteRequest Completion(string code, string flowCookie) => new(
        "https://jellyfin.example.test",
        "application/json",
        $$"""{ "code": "{{code}}", "deviceId": "device-0123" }""",
        flowCookie,
        null,
        "203.0.113.7");

    private OidcLoginService CreateService()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new OidcLoginService(
            new IdentityProviderClient(httpClientFactory),
            new FlowCookieProtector(_dataProtection),
            _users.Manager,
            Substitute.For<ISessionManager>(),
            new LinkedIdentityStore(_dataFolder),
            new HandoffCodes(_clock),
            _clock,
            () => _configuration,
            new JellyfinWebClient("12.4.0"),
            _logger);
    }
}
