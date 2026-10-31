using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Who gets in: Just-in-time Provisioning and its username rules, the Required Group and the required <c>amr</c>.
/// </summary>
public sealed class WhoGetsInTests : IDisposable
{
    private readonly LoginHarness _login = new();

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
        _login.Configuration.JustInTimeProvisioning = false;

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task LinkedSubjectWithProvisioningOffSignsIn()
    {
        _login.Configuration.JustInTimeProvisioning = false;
        _login.LinkedUser("alice");

        var result = await LoginAsync();

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task UnlinkedUserWithProvisioningOffIsRefusedOnTheNextLogin()
    {
        Assert.Null((await LoginAsync()).Failure);
        var alice = Assert.Single(_login.Users.All);
        Assert.True(new LinkedIdentityAdmin(_login.LinkedIdentities, _login.Users.Manager, _login.Clock, () => _login.Configuration).Unlink(alice.Id));
        _login.Configuration.JustInTimeProvisioning = false;
        var before = _login.Logger.Entries.Count;

        var result = await LoginAsync();

        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Same(alice, Assert.Single(_login.Users.All));
        Assert.Empty(_login.LinkedIdentities.GetAll());
        Assert.Equal(before + 1, _login.Logger.Entries.Count);
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
        var user = _login.Users.Add(existing);

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result, existingUsers: 1);
        Assert.Equal(existing, Assert.Single(_login.Users.All).Username);
        Assert.Same(user, Assert.Single(_login.Users.All));
    }

    [Fact]
    public async Task CollisionLooksLikeAnyOtherNotSetUp()
    {
        _login.Users.Add("alice");
        var collision = (await LoginAsync()).Failure!;
        _login.Configuration.JustInTimeProvisioning = false;
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
        Assert.Equal(username, Assert.Single(_login.Users.All).Username);
    }

    [Fact]
    public async Task ProvisionedUserGetsARandomPasswordOfAtLeast32Bytes()
    {
        await LoginAsync();
        await LoginAsync(claims => claims["preferred_username"] = "bob", subject: "another-sub");

        var passwords = _login.Users.All.Select(_login.Users.PasswordOf).ToArray();
        Assert.All(passwords, p => Assert.True(p?.Length >= 43, $"Password '{p}' is shorter than 32 base64url-encoded bytes."));
        Assert.NotEqual(passwords[0], passwords[1]);
    }

    [Fact]
    public async Task ProvisioningThatCannotSetThePasswordFailsWithAReferenceAndLeavesNoUser()
    {
        _login.Users.Manager.ChangePassword(Arg.Any<Guid>(), Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("database is locked")));

        var result = await LoginAsync();

        Assert.Equal(FailureCategory.Failed, result.Failure?.Category);
        Assert.Null(result.CompleteUrl);
        Assert.Empty(_login.Users.All);
        Assert.Empty(_login.LinkedIdentities.GetAll());
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(result.Failure!.Reference, entry.Values["Reference"]);
        Assert.Contains("database is locked", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkedUserKeepsItsPassword()
    {
        var alice = _login.LinkedUser("alice");

        await LoginAsync();

        Assert.Null(_login.Users.PasswordOf(alice));
    }

    [Fact]
    public async Task RequiredGroupMemberSignsIn()
    {
        _login.Configuration.RequiredGroup = "jellyfin-users";

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
        _login.Configuration.RequiredGroup = "jellyfin-users";

        var result = await LoginAsync(claims => claims["groups"] = groups.Split(',', StringSplitOptions.RemoveEmptyEntries));

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task AbsentGroupsClaimWithARequiredGroupIsNotSetUp()
    {
        _login.Configuration.RequiredGroup = "jellyfin-users";

        var result = await LoginAsync();

        AssertNotSetUpWithoutProvisioning(result);
    }

    [Fact]
    public async Task RequiredGroupAlsoAppliesToLinkedUsers()
    {
        _login.Configuration.RequiredGroup = "jellyfin-users";
        var alice = _login.LinkedUser("alice");

        var result = await LoginAsync(claims => claims["groups"] = new[] { "family" });

        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Equal(alice.Id, Assert.Single(_login.LinkedIdentities.GetAll()).UserId);
        Assert.Equal(_login.Clock.GetUtcNow() - TimeSpan.FromDays(3), Assert.Single(_login.LinkedIdentities.GetAll()).LastLoginAt);
    }

    [Fact]
    public async Task RequiredGroupIsReadFromTheConfiguredGroupsClaim()
    {
        _login.Configuration.RequiredGroup = "jellyfin-users";
        _login.Configuration.GroupsClaim = "roles";

        var member = await LoginAsync(claims => claims["roles"] = new[] { "jellyfin-users" });

        Assert.Null(member.Failure);
    }

    [Fact]
    public async Task PasskeySignInMeetsTheRequiredAmr()
    {
        _login.Configuration.RequiredAmr = "phr";

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
        _login.Configuration.RequiredAmr = "phr";
        var alice = _login.LinkedUser("alice");

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
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(result.Failure.Reference, entry.Values["Reference"]);
        Assert.Same(alice, Assert.Single(_login.Users.All));
    }

    [Fact]
    public async Task PasskeyRequiredIsCheckedBeforeProvisioning()
    {
        _login.Configuration.RequiredAmr = "phr";

        var result = await LoginAsync(claims => claims["amr"] = new[] { "otp" });

        Assert.Equal(FailureCategory.PasskeyRequired, result.Failure?.Category);
        Assert.Empty(_login.Users.All);
        Assert.Empty(_login.LinkedIdentities.GetAll());
    }

    [Fact]
    public async Task EmptyRequiredAmrAcceptsAnyAuthenticationMethod()
    {
        var result = await LoginAsync(claims => claims["amr"] = new[] { "otp" });

        Assert.Null(result.Failure);
    }

    public void Dispose() => _login.Dispose();

    /// <summary>
    /// Asserts a Not set up refusal with a 403 and exactly one Warning entry, and that nothing was provisioned.
    /// </summary>
    private void AssertNotSetUpWithoutProvisioning(CallbackResult result, int existingUsers = 0)
    {
        Assert.Equal(FailureCategory.NotSetUp, result.Failure?.Category);
        Assert.Equal(403, result.Failure!.StatusCode);
        Assert.Null(result.CompleteUrl);
        Assert.Equal(existingUsers, _login.Users.All.Count);
        Assert.Empty(_login.LinkedIdentities.GetAll());
        var entry = Assert.Single(_login.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(result.Failure.Reference, entry.Values["Reference"]);
    }

    /// <summary>
    /// Runs Start and Callback on a new service, the Identity Provider issuing an ID token for Alice, or for
    /// <paramref name="subject"/>, adjusted by <paramref name="adjustClaims"/>.
    /// </summary>
    private async Task<CallbackResult> LoginAsync(Action<Dictionary<string, object>>? adjustClaims = null, string subject = LoginHarness.Subject)
    {
        using var service = _login.CreateService();
        var (result, _) = await _login.CallbackAsync(service, claims =>
        {
            claims["sub"] = subject;
            adjustClaims?.Invoke(claims);
        });
        return result;
    }
}
