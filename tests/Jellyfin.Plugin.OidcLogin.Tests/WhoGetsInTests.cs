using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
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
/// Who gets in: Just-in-time Provisioning and its username rules, the Required Group and the required <c>amr</c>.
/// </summary>
public sealed class WhoGetsInTests : IDisposable
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
    public void JustInTimeProvisioningIsOnByDefault()
    {
        Assert.True(new PluginConfiguration().JustInTimeProvisioning);
        Assert.Empty(new PluginConfiguration().RequiredGroup);
        Assert.Empty(new PluginConfiguration().RequiredAmr);
    }

    [Fact]
    public async Task UnlinkedSubjectWithProvisioningOffIsNotSetUp()
    {
        _configuration.JustInTimeProvisioning = false;

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task LinkedSubjectWithProvisioningOffSignsIn()
    {
        _configuration.JustInTimeProvisioning = false;
        LinkedUser("alice");

        var result = await LoginAsync();

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task UnlinkedUserWithProvisioningOffIsRefusedOnTheNextLogin()
    {
        Assert.Null((await LoginAsync()).Failure);
        var alice = Assert.Single(_users.All);
        Assert.True(new LinkedIdentityAdmin(new LinkedIdentityStore(_dataFolder), _users.Manager, _clock, () => _configuration).Unlink(alice.Id));
        _configuration.JustInTimeProvisioning = false;
        var before = _logger.Entries.Count;

        var result = await LoginAsync();

        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Same(alice, Assert.Single(_users.All));
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
        Assert.Equal(before + 1, _logger.Entries.Count);
    }

    [Fact]
    public async Task MissingUsernameClaimIsNotSetUp()
    {
        var result = await LoginAsync(claims => claims.Remove("preferred_username"));

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" alice")]
    [InlineData("alice ")]
    [InlineData("al/ice")]
    [InlineData("<alice>")]
    public async Task InvalidUsernameClaimIsNotSetUp(string username)
    {
        var result = await LoginAsync(claims => claims["preferred_username"] = username);

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task UsernameClaimThatIsNotAStringIsNotSetUp()
    {
        var result = await LoginAsync(claims => claims["preferred_username"] = 42);

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice")]
    [InlineData("ALICE")]
    public async Task UsernameCollisionIsNotSetUpWithoutASuffix(string existing)
    {
        var user = _users.Add(existing);

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result, existingUsers: 1);
        Assert.Equal(existing, Assert.Single(_users.All).Username);
        Assert.Same(user, Assert.Single(_users.All));
    }

    [Fact]
    public async Task CollisionLooksLikeAnyOtherNotSetUp()
    {
        _users.Add("alice");
        var collision = (await LoginAsync()).Failure!;
        _configuration.JustInTimeProvisioning = false;
        var notProvisioned = (await LoginAsync(subject: "another-sub")).Failure!;

        Assert.Equal(notProvisioned.Category, collision.Category);
        Assert.Equal(notProvisioned.StatusCode, collision.StatusCode);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("alice.smith@example.test")]
    [InlineData("o'brien-2_x+y")]
    [InlineData("zoë")]
    public async Task ValidUsernameClaimIsProvisionedAsIs(string username)
    {
        var result = await LoginAsync(claims => claims["preferred_username"] = username);

        Assert.Null(result.Failure);
        Assert.Equal(username, Assert.Single(_users.All).Username);
    }

    [Fact]
    public async Task ProvisionedUserGetsARandomPasswordOfAtLeast32Bytes()
    {
        await LoginAsync();
        await LoginAsync(claims => claims["preferred_username"] = "bob", subject: "another-sub");

        var passwords = _users.All.Select(_users.PasswordOf).ToArray();
        Assert.All(passwords, p => Assert.True(p?.Length >= 43, $"Password '{p}' is shorter than 32 base64url-encoded bytes."));
        Assert.NotEqual(passwords[0], passwords[1]);
    }

    [Fact]
    public async Task LinkedUserKeepsItsPassword()
    {
        var alice = LinkedUser("alice");

        await LoginAsync();

        Assert.Null(_users.PasswordOf(alice));
    }

    [Fact]
    public async Task RequiredGroupMemberSignsIn()
    {
        _configuration.RequiredGroup = "jellyfin-users";

        var result = await LoginAsync(claims => claims["groups"] = new[] { "family", "jellyfin-users" });

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("family")]
    [InlineData("Jellyfin-Users")]
    [InlineData("jellyfin-users-old,family")]
    [InlineData("")]
    public async Task NonMemberOfTheRequiredGroupIsNotSetUp(string groups)
    {
        _configuration.RequiredGroup = "jellyfin-users";

        var result = await LoginAsync(claims => claims["groups"] = groups.Split(',', StringSplitOptions.RemoveEmptyEntries));

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task AbsentGroupsClaimWithARequiredGroupIsNotSetUp()
    {
        _configuration.RequiredGroup = "jellyfin-users";

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task RequiredGroupAlsoAppliesToLinkedUsers()
    {
        _configuration.RequiredGroup = "jellyfin-users";
        var alice = LinkedUser("alice");

        var result = await LoginAsync(claims => claims["groups"] = new[] { "family" });

        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Equal(alice.Id, Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll()).UserId);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromDays(3), Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll()).LastLoginAt);
    }

    [Fact]
    public async Task RequiredGroupIsReadFromTheConfiguredGroupsClaim()
    {
        _configuration.RequiredGroup = "jellyfin-users";
        _configuration.GroupsClaim = "roles";

        var member = await LoginAsync(claims => claims["roles"] = new[] { "jellyfin-users" });

        Assert.Null(member.Failure);
    }

    [Fact]
    public async Task PasskeySignInMeetsTheRequiredAmr()
    {
        _configuration.RequiredAmr = "phr";

        var result = await LoginAsync(claims => claims["amr"] = new[] { "phr" });

        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("otp")]
    [InlineData("otp,pwd")]
    [InlineData("")]
    [InlineData(null)]
    public async Task OtherOrNoAuthenticationMethodIsPasskeyRequired(string? amr)
    {
        _configuration.RequiredAmr = "phr";
        var alice = LinkedUser("alice");

        var result = await LoginAsync(claims =>
        {
            if (amr is not null)
            {
                claims["amr"] = amr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
        });

        Assert.Equal(FailureCategory.PasskeyRequired, result.Failure?.Category);
        Assert.Equal(403, result.Failure!.StatusCode);
        Assert.Null(result.CompleteUrl);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(result.Failure.Reference, entry.Values["Reference"]);
        Assert.Same(alice, Assert.Single(_users.All));
    }

    [Fact]
    public async Task PasskeyRequiredIsCheckedBeforeProvisioning()
    {
        _configuration.RequiredAmr = "phr";

        var result = await LoginAsync(claims => claims["amr"] = new[] { "otp" });

        Assert.Equal(FailureCategory.PasskeyRequired, result.Failure?.Category);
        Assert.Empty(_users.All);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    [Fact]
    public async Task EmptyRequiredAmrAcceptsAnyAuthenticationMethod()
    {
        var result = await LoginAsync(claims => claims["amr"] = new[] { "otp" });

        Assert.Null(result.Failure);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    /// <summary>
    /// Asserts a Not set up refusal with a 403 and exactly one Warning entry, and that nothing was provisioned.
    /// </summary>
    private void AssertNotSetUpWithoutProvisioning(CallbackResult result, int existingUsers = 0)
    {
        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Equal(403, result.Failure!.StatusCode);
        Assert.Null(result.CompleteUrl);
        Assert.Equal(existingUsers, _users.All.Count);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(result.Failure.Reference, entry.Values["Reference"]);
    }

    private User LinkedUser(string username)
    {
        var user = _users.Add(username);
        var linkedAt = _clock.GetUtcNow() - TimeSpan.FromDays(3);
        new LinkedIdentityStore(_dataFolder).Add(new LinkedIdentity(StubIdentityProvider.Issuer, Subject, user.Id, linkedAt, linkedAt));
        return user;
    }

    /// <summary>
    /// Runs Start, plays the Identity Provider with an ID token for Alice (adjusted by
    /// <paramref name="adjustClaims"/>), and runs Callback.
    /// </summary>
    private async Task<CallbackResult> LoginAsync(Action<Dictionary<string, object>>? adjustClaims = null, string subject = Subject)
    {
        using var service = CreateService();
        var start = await service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = _clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = _configuration.ClientId,
            ["sub"] = subject,
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        };
        adjustClaims?.Invoke(claims);
        _identityProvider.IdToken = _identityProvider.Sign(claims);
        return await service.CallbackAsync(new CallbackQuery("authorization-code", authorize["state"]), start.FlowCookie!.Value, TestContext.Current.CancellationToken);
    }

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
