using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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

public sealed class AdminGroupSyncTests : IDisposable
{
    private const string Subject = "6a3f1c2e-sub";

    private readonly StubIdentityProvider _identityProvider = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly InMemoryUsers _users = new();
    private readonly RecordingLogger _logger = new();
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test",
        IssuerUrl = StubIdentityProvider.Issuer,
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
        AdminGroup = "jellyfin-admins",
    };

    [Fact]
    public async Task AdminGroupMemberIsPromoted()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice");

        var result = await LoginAsync(groups: new[] { "family", "jellyfin-admins" });

        Assert.Null(result.Failure);
        Assert.True(_users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task ProvisionedAdminGroupMemberIsAnAdministratorFromTheFirstLogin()
    {
        _users.AddAdministrator("setup-admin");

        var result = await LoginAsync(groups: new[] { "jellyfin-admins" });

        Assert.Null(result.Failure);
        Assert.True(_users.IsSavedAdministrator(_users.Manager.GetUserByName("alice")!));
    }

    [Fact]
    public async Task AdministratorRemovedFromTheAdminGroupIsDemoted()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.False(_users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task GroupNamesMatchExactly()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice");

        await LoginAsync(groups: new[] { "Jellyfin-Admins", "jellyfin-admins-old" });

        Assert.False(_users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task EmptyAdminGroupSettingTurnsSyncOff()
    {
        _configuration.AdminGroup = string.Empty;
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.True(_users.IsSavedAdministrator(alice));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AbsentGroupsClaimSkipsSyncWithAWarning()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice", administrator: true);

        var result = await LoginAsync(groups: null);

        Assert.Null(result.Failure);
        Assert.True(_users.IsSavedAdministrator(alice));
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("groups", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyGroupsClaimIsMembershipOfNoGroup()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice", administrator: true);

        await LoginAsync(groups: Array.Empty<string>());

        Assert.False(_users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task LastAdministratorIsNeverDemotedAndTheLoginSucceedsWithAWarning()
    {
        var alice = LinkedUser("alice", administrator: true);
        _users.Add("bob");

        var result = await LoginAsync(groups: new[] { "family" });

        Assert.Null(result.Failure);
        Assert.True(_users.IsSavedAdministrator(alice));
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("last", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledAdministratorDoesNotCountAsRemaining()
    {
        var disabled = _users.AddAdministrator("disabled-admin");
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        var alice = LinkedUser("alice", administrator: true);

        await LoginAsync(groups: new[] { "family" });

        Assert.True(_users.IsSavedAdministrator(alice));
    }

    [Fact]
    public async Task NoOtherPolicyIsTouched()
    {
        _users.AddAdministrator("setup-admin");
        var alice = LinkedUser("alice");
        alice.SetPermission(PermissionKind.EnableRemoteAccess, false);
        alice.SetPermission(PermissionKind.EnableAllFolders, false);
        alice.MaxParentalRatingScore = 12;
        alice.MaxActiveSessions = 2;

        await LoginAsync(groups: new[] { "jellyfin-admins" });

        Assert.True(_users.IsSavedAdministrator(alice));
        Assert.False(alice.HasPermission(PermissionKind.EnableRemoteAccess));
        Assert.False(alice.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(12, alice.MaxParentalRatingScore);
        Assert.Equal(2, alice.MaxActiveSessions);
        Assert.Equal("alice", alice.Username);
    }

    public void Dispose()
    {
        _identityProvider.Dispose();
        Directory.Delete(_dataFolder, recursive: true);
    }

    private User LinkedUser(string username, bool administrator = false)
    {
        var user = administrator ? _users.AddAdministrator(username) : _users.Add(username);
        var linkedAt = _clock.GetUtcNow() - TimeSpan.FromDays(3);
        new LinkedIdentityStore(_dataFolder).Add(new LinkedIdentity(StubIdentityProvider.Issuer, Subject, user.Id, linkedAt, linkedAt));
        return user;
    }

    /// <summary>
    /// Runs Start, plays the Identity Provider with an ID token carrying <paramref name="groups"/> (no groups claim
    /// when null), and runs Callback.
    /// </summary>
    private async Task<CallbackResult> LoginAsync(object? groups)
    {
        using var service = CreateService();
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
        if (groups is not null)
        {
            claims["groups"] = groups;
        }

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

    private sealed class RecordingLogger : ILogger<OidcLoginService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
