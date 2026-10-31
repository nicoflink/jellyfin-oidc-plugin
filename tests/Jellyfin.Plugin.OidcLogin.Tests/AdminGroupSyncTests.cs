using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class AdminGroupSyncTests : IDisposable
{
    private readonly LoginHarness _login = new();
    private readonly OidcLoginService _service;

    public AdminGroupSyncTests()
    {
        _login.Configuration.AdminGroup = "jellyfin-admins";
        _service = _login.CreateService();
    }

    [Fact]
    public async Task AdminGroupMemberIsPromoted()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice");

        var result = await LoginAsync(groups: new[] { "family", "jellyfin-admins" });

        Assert.Null(result.Failure);
        Assert.True(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task ProvisionedAdminGroupMemberIsAnAdministratorFromTheFirstLogin()
    {
        _login.Users.AddAdministrator("setup-admin");

        var result = await LoginAsync(groups: new[] { "jellyfin-admins" });

        Assert.Null(result.Failure);
        Assert.True(_login.Users.IsSavedAdministrator(_login.Users.Manager.GetUserByName("alice")!));
    }

    [Fact]
    public async Task AdministratorRemovedFromTheAdminGroupIsDemoted()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.False(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task GroupNamesMatchExactly()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice");

        await LoginAsync(groups: new[] { "Jellyfin-Admins", "jellyfin-admins-old" });

        Assert.False(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task EmptyAdminGroupSettingTurnsSyncOff()
    {
        _login.Configuration.AdminGroup = string.Empty;
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.True(_login.Users.IsSavedAdministrator(alice));
        Assert.DoesNotContain(_login.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AbsentGroupsClaimSkipsSyncWithAWarning()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: null);

        Assert.Null(result.Failure);
        Assert.True(_login.Users.IsSavedAdministrator(alice));
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("groups", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyGroupsClaimIsMembershipOfNoGroup()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice", administrator: true);

        await LoginAsync(groups: Array.Empty<string>());

        Assert.False(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task LastAdministratorIsNeverDemotedAndTheLoginSucceedsWithAWarning()
    {
        var alice = _login.LinkedUser("alice", administrator: true);
        _login.Users.Add("bob");

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.True(_login.Users.IsSavedAdministrator(alice));
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("last", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledAdministratorDoesNotCountAsRemaining()
    {
        var disabled = _login.Users.AddAdministrator("disabled-admin");
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        var alice = _login.LinkedUser("alice", administrator: true);

        await LoginAsync(groups: new[] { "family" });

        Assert.True(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task LoginAbandonedAfterTheCallbackChangesNothing()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice");

        var callback = await CallbackAsync(groups: new[] { "jellyfin-admins" });

        Assert.Null(callback.Failure);
        Assert.False(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task DisabledUserIsRefusedWithoutBeingPromoted()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice");
        alice.SetPermission(PermissionKind.IsDisabled, true);

        var result = await LoginAsync(groups: new[] { "jellyfin-admins" });

        Assert.Equal(FailureCategory.Disabled, result.Failure?.Category);
        Assert.False(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task UserWhoCannotSignInRightNowIsRefusedWithoutBeingDemoted()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice", administrator: true);
        alice.SetPermission(PermissionKind.EnableRemoteAccess, false);

        var result = await LoginAsync(groups: new[] { "family" }, fromLocalNetwork: false);

        Assert.True(result.Failure?.NotNow);
        Assert.True(_login.Users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task NoOtherPolicyIsTouched()
    {
        _login.Users.AddAdministrator("setup-admin");
        var alice = _login.LinkedUser("alice");
        alice.SetPermission(PermissionKind.EnableRemoteAccess, false);
        alice.SetPermission(PermissionKind.EnableAllFolders, false);
        alice.MaxParentalRatingScore = 12;
        alice.MaxActiveSessions = 2;

        await LoginAsync(groups: new[] { "jellyfin-admins" });

        Assert.True(_login.Users.IsSavedAdministrator(alice));
        Assert.False(alice.HasPermission(PermissionKind.EnableRemoteAccess));
        Assert.False(alice.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(12, alice.MaxParentalRatingScore);
        Assert.Equal(2, alice.MaxActiveSessions);
        Assert.Equal("alice", alice.Username);
    }

    public void Dispose()
    {
        _service.Dispose();
        _login.Dispose();
    }

    /// <summary>
    /// Runs Start, Callback (see <see cref="CallbackAsync"/>) and Complete, by default from the local network.
    /// </summary>
    private Task<CompleteResult> LoginAsync(object? groups, bool fromLocalNetwork = true)
        => _login.LoginAsync(_service, claims => WithGroups(claims, groups), fromLocalNetwork);

    /// <summary>
    /// Runs Start and Callback, the Identity Provider issuing an ID token carrying <paramref name="groups"/> (no
    /// groups claim when null).
    /// </summary>
    private async Task<CallbackResult> CallbackAsync(object? groups)
        => (await _login.CallbackAsync(_service, claims => WithGroups(claims, groups))).Result;

    private static void WithGroups(Dictionary<string, object> claims, object? groups)
    {
        if (groups is not null)
        {
            claims["groups"] = groups;
        }
    }
}
