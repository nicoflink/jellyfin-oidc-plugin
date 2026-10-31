using System;
using System.IO;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LinkedIdentityAdminTests : IDisposable
{
    private static readonly DateTimeOffset LinkedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUsers _users = new();
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly PluginConfiguration _configuration = new() { IssuerUrl = StubIdentityProvider.Issuer };

    [Fact]
    public void ListsEachLinkWithItsJellyfinUser()
    {
        var alice = _users.Add("alice");
        var deletedUserId = Guid.NewGuid();
        var store = new LinkedIdentityStore(_dataFolder);
        store.Add(new LinkedIdentity(StubIdentityProvider.Issuer, "sub-alice", alice.Id, LinkedAt, _clock.GetUtcNow()));
        store.Add(new LinkedIdentity(StubIdentityProvider.Issuer, "sub-gone", deletedUserId, LinkedAt, null));

        var entries = CreateAdmin().List();

        Assert.Collection(
            entries,
            e =>
            {
                Assert.Equal(StubIdentityProvider.Issuer, e.Issuer);
                Assert.Equal("sub-alice", e.Subject);
                Assert.Equal(alice.Id, e.UserId);
                Assert.Equal("alice", e.UserName);
                Assert.Equal(LinkedAt, e.LinkedAt);
                Assert.Equal(_clock.GetUtcNow(), e.LastLoginAt);
            },
            e =>
            {
                Assert.Equal(deletedUserId, e.UserId);
                Assert.Null(e.UserName);
                Assert.Null(e.LastLoginAt);
            });
    }

    [Fact]
    public void LinksAnExistingUserToASubjectOfTheConfiguredIssuer()
    {
        var bob = _users.Add("bob");

        var refusal = CreateAdmin().Link(bob.Id, " sub-bob ");

        Assert.Null(refusal);
        var link = Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll());
        Assert.Equal(new LinkedIdentity(StubIdentityProvider.Issuer, "sub-bob", bob.Id, _clock.GetUtcNow(), null), link);
    }

    [Fact]
    public void RefusesASubjectThatIsAlreadyLinked()
    {
        var alice = _users.Add("alice");
        var bob = _users.Add("bob");
        var admin = CreateAdmin();
        Assert.Null(admin.Link(alice.Id, "sub-alice"));

        var refusal = admin.Link(bob.Id, "sub-alice");

        Assert.Equal("Subject sub-alice is already linked to alice.", refusal);
        Assert.Equal(alice.Id, Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll()).UserId);
    }

    [Fact]
    public void RefusesAUserThatIsAlreadyLinked()
    {
        var alice = _users.Add("alice");
        var admin = CreateAdmin();
        Assert.Null(admin.Link(alice.Id, "sub-alice"));

        var refusal = admin.Link(alice.Id, "sub-other");

        Assert.Equal("alice is already linked to Subject sub-alice.", refusal);
        Assert.Equal("sub-alice", Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll()).Subject);
    }

    [Fact]
    public void RefusesAUserThatDoesNotExist()
    {
        var refusal = CreateAdmin().Link(Guid.NewGuid(), "sub-1");

        Assert.Equal("That Jellyfin user doesn't exist.", refusal);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAnEmptySub(string sub)
    {
        var refusal = CreateAdmin().Link(_users.Add("alice").Id, sub);

        Assert.Equal("Enter the sub.", refusal);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    [Fact]
    public void RefusesWithoutAnIssuerUrl()
    {
        _configuration.IssuerUrl = string.Empty;

        var refusal = CreateAdmin().Link(_users.Add("alice").Id, "sub-1");

        Assert.Equal("Set the Issuer URL first: a Subject is a sub at that issuer.", refusal);
        Assert.Empty(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    [Fact]
    public void UnlinkRemovesOnlyThatUsersLink()
    {
        var alice = _users.Add("alice");
        var bob = _users.Add("bob");
        var admin = CreateAdmin();
        Assert.Null(admin.Link(alice.Id, "sub-alice"));
        Assert.Null(admin.Link(bob.Id, "sub-bob"));

        Assert.True(admin.Unlink(alice.Id));

        Assert.Equal(bob.Id, Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll()).UserId);
        Assert.Null(admin.Link(alice.Id, "sub-alice"));
    }

    [Fact]
    public void UnlinkOfAnUnlinkedUserChangesNothing()
    {
        var admin = CreateAdmin();
        Assert.Null(admin.Link(_users.Add("alice").Id, "sub-alice"));

        Assert.False(admin.Unlink(Guid.NewGuid()));

        Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    public void Dispose() => Directory.Delete(_dataFolder, recursive: true);

    private LinkedIdentityAdmin CreateAdmin() => new(new LinkedIdentityStore(_dataFolder), _users.Manager, _clock, () => _configuration);
}
