using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// OIDC Login respects the Jellyfin user's state, and Linked Identities follow the user's lifecycle.
/// </summary>
public sealed class UserLifecycleTests : IDisposable
{
    private const string Subject = "6a3f1c2e-sub";
    private const string DeviceId = "TW96aWxsYS81LjAgKFgxMTsgTGludXggeDg2XzY0KSBDaHJvbWUvMTQwfDE3NjE4MjU2MDAwMDA1";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly List<AuthenticationRequest> _sessionRequests = [];
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly LinkedIdentityStore _linkedIdentities;
    private readonly OidcLoginService _service;
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
    };

    public UserLifecycleTests()
    {
        _sessions.AuthenticateDirect(Arg.Any<AuthenticationRequest>()).Returns(call =>
        {
            var request = call.Arg<AuthenticationRequest>();
            _sessionRequests.Add(request);
            return Task.FromResult(new AuthenticationResult
            {
                AccessToken = "session-token-" + _sessionRequests.Count,
                ServerId = "server-id",
                User = new UserDto { Id = request.UserId },
            });
        });
        _linkedIdentities = new LinkedIdentityStore(_dataFolder);
        _service = CreateService();
    }

    [Fact]
    public async Task DisabledUserIsRefusedAndKeepsItsLink()
    {
        var alice = LinkedUser("alice");
        alice.SetPermission(PermissionKind.IsDisabled, true);

        var result = await LoginAsync();

        AssertRefused(result, FailureCategory.Disabled);
        Assert.False(result.Failure!.NotNow);
        Assert.Equal(403, result.Failure.StatusCode);
        Assert.Equal(alice.Id, Assert.Single(_linkedIdentities.GetAll()).UserId);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task WithoutRemoteAccessOnlyTheLocalNetworkSignsIn(bool remoteAccess, bool fromLocalNetwork, bool accepted)
    {
        LinkedUser("alice").SetPermission(PermissionKind.EnableRemoteAccess, remoteAccess);

        var result = await LoginAsync(fromLocalNetwork);

        if (accepted)
        {
            Assert.NotNull(result.Session);
        }
        else
        {
            AssertRefused(result, FailureCategory.Disabled);
            Assert.True(result.Failure!.NotNow);
            Assert.Equal(403, result.Failure.StatusCode);
        }
    }

    [Fact]
    public async Task OutsideTheParentalScheduleIsRefusedRightNow()
    {
        var alice = LinkedUser("alice");

        // Jellyfin reads the schedule against the server's own clock; this one never allows access.
        alice.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 0, -1, alice.Id));

        var result = await LoginAsync();

        AssertRefused(result, FailureCategory.Disabled);
        Assert.True(result.Failure!.NotNow);
    }

    [Fact]
    public async Task WithinTheParentalScheduleSignsIn()
    {
        var alice = LinkedUser("alice");
        alice.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 0, 24, alice.Id));

        var result = await LoginAsync();

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task DeletingTheUserRemovesItsLinkSoTheNextLoginProvisionsAfresh()
    {
        var alice = LinkedUser("alice");
        var other = _users.Add("bob");
        _linkedIdentities.Add(new LinkedIdentity(StubIdentityProvider.Issuer, "bob-sub", other.Id, _clock.GetUtcNow(), _clock.GetUtcNow()));
        _users.Remove(alice);

        await new UserDeletedConsumer(_linkedIdentities).OnEvent(new UserDeletedEventArgs(alice));

        Assert.Equal(other.Id, Assert.Single(_linkedIdentities.GetAll()).UserId);
        var result = await LoginAsync();
        var provisioned = _users.Manager.GetUserByName("alice")!;
        Assert.NotEqual(alice.Id, provisioned.Id);
        Assert.Equal(provisioned.Id, result.Session!.UserId);
        Assert.Equal(provisioned.Id, _linkedIdentities.Find(StubIdentityProvider.Issuer, Subject)!.UserId);
    }

    [Fact]
    public async Task ALinkWhoseUserIsGoneIsDroppedAndTheLoginProvisionsAfresh()
    {
        // The user-deleted event was missed, for example because the Plugin wasn't loaded.
        var alice = LinkedUser("alice");
        _users.Remove(alice);

        var result = await LoginAsync();

        var provisioned = _users.Manager.GetUserByName("alice")!;
        Assert.Equal(provisioned.Id, result.Session!.UserId);
        Assert.Equal(provisioned.Id, Assert.Single(_linkedIdentities.GetAll()).UserId);
    }

    [Fact]
    public async Task RenamingTheUserKeepsItsLink()
    {
        var alice = LinkedUser("alice");
        alice.Username = "alice-renamed";

        var result = await LoginAsync();

        Assert.Equal(alice.Id, result.Session!.UserId);
        Assert.Single(_users.All);
        Assert.Equal(alice.Id, Assert.Single(_linkedIdentities.GetAll()).UserId);
    }

    [Fact]
    public async Task EachSuccessfulLoginUpdatesTheLinksLastLogin()
    {
        LinkedUser("alice");
        var linkedAt = _linkedIdentities.GetAll()[0].LinkedAt;
        _clock.Advance(TimeSpan.FromHours(1));
        await LoginAsync();
        var first = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromHours(1));

        await LoginAsync();

        var link = Assert.Single(_linkedIdentities.GetAll());
        Assert.Equal(linkedAt, link.LinkedAt);
        Assert.NotEqual(first, link.LastLoginAt);
        Assert.Equal(_clock.GetUtcNow(), link.LastLoginAt);
    }

    [Fact]
    public async Task ARefusedLoginLeavesTheLastLogin()
    {
        var alice = LinkedUser("alice");
        var before = _linkedIdentities.GetAll()[0].LastLoginAt;
        alice.SetPermission(PermissionKind.IsDisabled, true);
        _clock.Advance(TimeSpan.FromHours(1));

        await LoginAsync();

        Assert.Equal(before, Assert.Single(_linkedIdentities.GetAll()).LastLoginAt);
    }

    [Fact]
    public async Task EveryLoginInABrowserIsForItsDeviceIdSoJellyfinReplacesItsEarlierSession()
    {
        LinkedUser("alice");

        await LoginAsync();
        await LoginAsync();

        Assert.Equal([DeviceId, DeviceId], _sessionRequests.Select(r => r.DeviceId));
    }

    [Fact]
    public async Task LoginNeverChangesTheUsersPolicy()
    {
        var alice = LinkedUser("alice");
        alice.SetPermission(PermissionKind.EnableAllFolders, false);
        alice.SetPermission(PermissionKind.EnableContentDownloading, false);
        alice.MaxActiveSessions = 3;
        var permissionsBefore = PermissionsOf(alice);
        _users.Manager.ClearReceivedCalls();

        await LoginAsync();
        await LoginAsync();

        Assert.Equal(2, _sessionRequests.Count);
        await _users.Manager.DidNotReceiveWithAnyArgs().UpdateUserAsync(default!);
        await _users.Manager.DidNotReceiveWithAnyArgs().UpdatePolicyAsync(default, default!);
        Assert.Equal(permissionsBefore, PermissionsOf(alice));
        Assert.Equal(3, alice.MaxActiveSessions);
    }

    public void Dispose()
    {
        _service.Dispose();
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    private static Dictionary<PermissionKind, bool> PermissionsOf(User user)
        => Enum.GetValues<PermissionKind>().ToDictionary(kind => kind, user.HasPermission);

    private static void AssertRefused(CompleteResult result, FailureCategory category)
    {
        Assert.Equal(category, result.Failure?.Category);
        Assert.Null(result.Session);
    }

    private User LinkedUser(string username)
    {
        var user = _users.Add(username);
        var linkedAt = _clock.GetUtcNow() - TimeSpan.FromDays(3);
        _linkedIdentities.Add(new LinkedIdentity(StubIdentityProvider.Issuer, Subject, user.Id, linkedAt, linkedAt));
        return user;
    }

    /// <summary>
    /// Runs Start, Callback (as alice at the stub Identity Provider) and Complete from one browser.
    /// </summary>
    private async Task<CompleteResult> LoginAsync(bool fromLocalNetwork = false)
    {
        var start = await _service.StartAsync(TestContext.Current.CancellationToken);
        var authorize = QueryHelpers.ParseQuery(start.AuthorizationUrl!.Query);
        var now = _clock.GetUtcNow();
        _identityProvider.IdToken = _identityProvider.Sign(new Dictionary<string, object>
        {
            ["iss"] = StubIdentityProvider.Issuer,
            ["aud"] = _configuration.ClientId,
            ["sub"] = Subject,
            ["nonce"] = authorize["nonce"].ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["preferred_username"] = "alice",
        });
        var flowCookie = start.FlowCookie!.Value;
        var callback = await _service.CallbackAsync(new CallbackQuery("authorization-code", authorize["state"]), flowCookie, TestContext.Current.CancellationToken);
        Assert.Null(callback.Failure);
        return await _service.CompleteAsync(
            new CompleteRequest(
                "https://jellyfin.example.test",
                "application/json",
                JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = callback.HandoffCode!, ["deviceId"] = DeviceId }),
                flowCookie,
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
                "203.0.113.7",
                fromLocalNetwork),
            TestContext.Current.CancellationToken);
    }

    private OidcLoginService CreateService()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_identityProvider, disposeHandler: false));
        return new OidcLoginService(
            new IdentityProviderClient(httpClientFactory),
            new FlowCookieProtector(_dataProtection),
            _users.Manager,
            _sessions,
            _linkedIdentities,
            new HandoffCodes(_clock),
            _clock,
            () => _configuration,
            new JellyfinWebClient("12.4.0"),
            NullLogger<OidcLoginService>.Instance);
    }
}
