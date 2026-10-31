using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Login;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// OIDC Login respects the Jellyfin user's state, and Linked Identities follow the user's lifecycle.
/// </summary>
public sealed class UserLifecycleTests : IDisposable
{
    private readonly LoginHarness _login = new();
    private readonly OidcLoginService _service;

    public UserLifecycleTests()
    {
        _service = _login.CreateService();
    }

    [Fact]
    public async Task DisabledUserIsRefusedAndKeepsItsLink()
    {
        var alice = _login.LinkedUser("alice");
        alice.SetPermission(PermissionKind.IsDisabled, true);

        var result = await LoginAsync();

        LoginHarness.AssertRefused(result, FailureCategory.Disabled);
        Assert.False(result.Failure!.NotNow);
        Assert.Equal(403, result.Failure.StatusCode);
        Assert.Equal(alice.Id, Assert.Single(_login.LinkedIdentities.GetAll()).UserId);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task WithoutRemoteAccessOnlyTheLocalNetworkSignsIn(bool remoteAccess, bool fromLocalNetwork, bool accepted)
    {
        _login.LinkedUser("alice").SetPermission(PermissionKind.EnableRemoteAccess, remoteAccess);

        var result = await LoginAsync(fromLocalNetwork);

        if (accepted)
        {
            Assert.NotNull(result.Session);
        }
        else
        {
            LoginHarness.AssertRefused(result, FailureCategory.Disabled);
            Assert.True(result.Failure!.NotNow);
            Assert.Equal(403, result.Failure.StatusCode);
        }
    }

    [Fact]
    public async Task OutsideTheParentalScheduleIsRefusedRightNow()
    {
        var alice = _login.LinkedUser("alice");

        // Jellyfin reads the schedule against the server's own clock; this one never allows access.
        alice.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 0, -1, alice.Id));

        var result = await LoginAsync();

        LoginHarness.AssertRefused(result, FailureCategory.Disabled);
        Assert.True(result.Failure!.NotNow);
    }

    [Fact]
    public async Task WithinTheParentalScheduleSignsIn()
    {
        var alice = _login.LinkedUser("alice");
        alice.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 0, 24, alice.Id));

        var result = await LoginAsync();

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task DeletingTheUserRemovesItsLinkSoTheNextLoginProvisionsAfresh()
    {
        var alice = _login.LinkedUser("alice");
        var other = _login.Users.Add("bob");
        _login.LinkedIdentities.Add(new LinkedIdentity(StubIdentityProvider.Issuer, "bob-sub", other.Id, _login.Clock.GetUtcNow(), _login.Clock.GetUtcNow()));
        _login.Users.Remove(alice);

        await new UserDeletedConsumer(_login.LinkedIdentities).OnEvent(new UserDeletedEventArgs(alice));

        Assert.Equal(other.Id, Assert.Single(_login.LinkedIdentities.GetAll()).UserId);
        var result = await LoginAsync();
        var provisioned = _login.Users.Manager.GetUserByName("alice")!;
        Assert.NotEqual(alice.Id, provisioned.Id);
        Assert.Equal(provisioned.Id, result.Session!.UserId);
        Assert.Equal(provisioned.Id, _login.LinkedIdentities.Find(StubIdentityProvider.Issuer, LoginHarness.Subject)!.UserId);
    }

    [Fact]
    public async Task ALinkWhoseUserIsGoneIsDroppedAndTheLoginProvisionsAfresh()
    {
        // The user-deleted event was missed, for example because the Plugin wasn't loaded.
        var alice = _login.LinkedUser("alice");
        _login.Users.Remove(alice);

        var result = await LoginAsync();

        var provisioned = _login.Users.Manager.GetUserByName("alice")!;
        Assert.Equal(provisioned.Id, result.Session!.UserId);
        Assert.Equal(provisioned.Id, Assert.Single(_login.LinkedIdentities.GetAll()).UserId);
    }

    [Fact]
    public async Task RenamingTheUserKeepsItsLink()
    {
        var alice = _login.LinkedUser("alice");
        alice.Username = "alice-renamed";

        var result = await LoginAsync();

        Assert.Equal(alice.Id, result.Session!.UserId);
        Assert.Single(_login.Users.All);
        Assert.Equal(alice.Id, Assert.Single(_login.LinkedIdentities.GetAll()).UserId);
    }

    [Fact]
    public async Task EachSuccessfulLoginUpdatesTheLinksLastLogin()
    {
        _login.LinkedUser("alice");
        var linkedAt = _login.LinkedIdentities.GetAll()[0].LinkedAt;
        _login.Clock.Advance(TimeSpan.FromHours(1));
        await LoginAsync();
        var first = _login.Clock.GetUtcNow();
        _login.Clock.Advance(TimeSpan.FromHours(1));

        await LoginAsync();

        var link = Assert.Single(_login.LinkedIdentities.GetAll());
        Assert.Equal(linkedAt, link.LinkedAt);
        Assert.NotEqual(first, link.LastLoginAt);
        Assert.Equal(_login.Clock.GetUtcNow(), link.LastLoginAt);
    }

    [Fact]
    public async Task ARefusedLoginLeavesTheLastLogin()
    {
        var alice = _login.LinkedUser("alice");
        var before = _login.LinkedIdentities.GetAll()[0].LastLoginAt;
        alice.SetPermission(PermissionKind.IsDisabled, true);
        _login.Clock.Advance(TimeSpan.FromHours(1));

        await LoginAsync();

        Assert.Equal(before, Assert.Single(_login.LinkedIdentities.GetAll()).LastLoginAt);
    }

    [Fact]
    public async Task EveryLoginInABrowserIsForItsDeviceIdSoJellyfinReplacesItsEarlierSession()
    {
        _login.LinkedUser("alice");

        await LoginAsync();
        await LoginAsync();

        Assert.Equal([LoginHarness.DeviceId, LoginHarness.DeviceId], _login.SessionRequests.Select(r => r.DeviceId));
    }

    [Fact]
    public async Task LoginNeverChangesTheUsersPolicy()
    {
        var alice = _login.LinkedUser("alice");
        alice.SetPermission(PermissionKind.EnableAllFolders, false);
        alice.SetPermission(PermissionKind.EnableContentDownloading, false);
        alice.MaxActiveSessions = 3;
        var permissionsBefore = PermissionsOf(alice);
        _login.Users.Manager.ClearReceivedCalls();

        await LoginAsync();
        await LoginAsync();

        Assert.Equal(2, _login.SessionRequests.Count);
        await _login.Users.Manager.DidNotReceiveWithAnyArgs().UpdateUserAsync(default!);
        await _login.Users.Manager.DidNotReceiveWithAnyArgs().UpdatePolicyAsync(default, default!);
        Assert.Equal(permissionsBefore, PermissionsOf(alice));
        Assert.Equal(3, alice.MaxActiveSessions);
    }

    public void Dispose()
    {
        _service.Dispose();
        _login.Dispose();
    }

    private static Dictionary<PermissionKind, bool> PermissionsOf(User user)
        => Enum.GetValues<PermissionKind>().ToDictionary(kind => kind, user.HasPermission);

    private Task<CompleteResult> LoginAsync(bool fromLocalNetwork = false) => _login.LoginAsync(_service, fromLocalNetwork: fromLocalNetwork);
}
