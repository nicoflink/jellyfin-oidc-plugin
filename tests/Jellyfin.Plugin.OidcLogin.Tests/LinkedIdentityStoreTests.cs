using System;
using System.IO;
using Jellyfin.Plugin.OidcLogin.Login;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LinkedIdentityStoreTests : IDisposable
{
    private static readonly DateTimeOffset LinkedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataFolder = Path.Combine(Directory.CreateTempSubdirectory("oidclogin-tests").FullName, "plugin-data");

    [Fact]
    public void LinksSurviveARestartInTheirOwnJsonFile()
    {
        var link = new LinkedIdentity("https://id.example.test", "sub-1", Guid.NewGuid(), LinkedAt, LinkedAt);
        new LinkedIdentityStore(_dataFolder).Add(link);

        Assert.Equal(link, new LinkedIdentityStore(_dataFolder).Find("https://id.example.test", "sub-1"));
        Assert.Equal(["linked-identities.json"], Array.ConvertAll(Directory.GetFiles(_dataFolder), Path.GetFileName));
    }

    [Fact]
    public void SubjectIsKeyedByIssuerAndSub()
    {
        var store = new LinkedIdentityStore(_dataFolder);
        store.Add(new LinkedIdentity("https://id.example.test", "sub-1", Guid.NewGuid(), LinkedAt, LinkedAt));

        Assert.Null(store.Find("https://other.example.test", "sub-1"));
        Assert.Null(store.Find("https://id.example.test", "SUB-1"));
        store.Add(new LinkedIdentity("https://other.example.test", "sub-1", Guid.NewGuid(), LinkedAt, LinkedAt));
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void LinksAreStrictlyOneToOne()
    {
        var store = new LinkedIdentityStore(_dataFolder);
        var userId = Guid.NewGuid();
        store.Add(new LinkedIdentity("https://id.example.test", "sub-1", userId, LinkedAt, LinkedAt));

        Assert.Throws<InvalidOperationException>(() => store.Add(new LinkedIdentity("https://id.example.test", "sub-1", Guid.NewGuid(), LinkedAt, LinkedAt)));
        Assert.Throws<InvalidOperationException>(() => store.Add(new LinkedIdentity("https://id.example.test", "sub-2", userId, LinkedAt, LinkedAt)));
        Assert.Single(new LinkedIdentityStore(_dataFolder).GetAll());
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dataFolder)!, recursive: true);
}
