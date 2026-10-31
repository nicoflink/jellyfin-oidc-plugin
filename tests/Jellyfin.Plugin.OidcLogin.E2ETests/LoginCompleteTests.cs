using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The whole OIDC Login (scenarios 1 without the button, and 2): from <c>/OidcLogin/Start</c> to signed in on Jellyfin's
/// home page, in the same tab.
/// </summary>
public sealed class LoginCompleteTests(E2EEnvironment environment)
{
    [Fact]
    public async Task PasskeyLoginLandsHomeAsTheProvisionedUserAndARepeatLoginAsTheSameUser()
    {
        var first = await SignInAsync();
        var second = await SignInAsync();

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var users = await jellyfin.GetAsync("Users", adminToken, TestContext.Current.CancellationToken);
        var alice = Assert.Single(users.EnumerateArray(), u => u.GetProperty("Name").GetString() == E2EEnvironment.Alice.Username);
        Assert.False(alice.GetProperty("Policy").GetProperty("IsAdministrator").GetBoolean());
        var aliceId = Guid.Parse(alice.GetProperty("Id").GetString()!);
        Assert.Equal(aliceId, first.UserId);
        Assert.Equal(aliceId, second.UserId);
        Assert.NotEqual(first.AccessToken, second.AccessToken);

        var links = JsonDocument.Parse(await environment.ReadLinkedIdentitiesAsync()).RootElement;
        var link = Assert.Single(links.EnumerateArray(), l => l.GetProperty("Subject").GetString() == E2EEnvironment.Alice.Id);
        Assert.Equal(aliceId, link.GetProperty("UserId").GetGuid());

        // The session looks like the browser's own Local Login would.
        var devices = await jellyfin.GetAsync("Devices", adminToken, TestContext.Current.CancellationToken);
        var device = Assert.Single(devices.GetProperty("Items").EnumerateArray(), d => d.GetProperty("Id").GetString() == second.DeviceId);
        Assert.Equal("Jellyfin Web", device.GetProperty("AppName").GetString());
        Assert.Equal("Chrome", device.GetProperty("Name").GetString());
    }

    /// <summary>
    /// Scenario 2: repeat logins as the same user follow the Admin Group. Runs in this class so it never overlaps the
    /// test above, which expects Alice not to be an administrator.
    /// </summary>
    [Fact]
    public async Task RepeatLoginsPromoteAndDemoteWithTheAdminGroup()
    {
        var (aliceId, _, _) = await SignInAsync();
        try
        {
            await environment.SetAliceGroupsAsync(admin: true);
            Assert.Equal(aliceId, (await SignInAsync()).UserId);
            Assert.True(await IsAdministratorAsync(aliceId));

            await environment.SetAliceGroupsAsync(admin: false);
            Assert.Equal(aliceId, (await SignInAsync()).UserId);
            Assert.False(await IsAdministratorAsync(aliceId));
        }
        finally
        {
            await environment.SetAliceGroupsAsync(admin: false);
        }
    }

    private async Task<bool> IsAdministratorAsync(Guid userId)
    {
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var user = await jellyfin.GetAsync($"Users/{userId}", adminToken, TestContext.Current.CancellationToken);
        return user.GetProperty("Policy").GetProperty("IsAdministrator").GetBoolean();
    }

    /// <summary>
    /// Runs an OIDC Login with Alice's passkey in a fresh browser and returns the credentials jellyfin-web holds on
    /// its home page, checked against the server.
    /// </summary>
    private async Task<(Guid UserId, string AccessToken, string DeviceId)> SignInAsync()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;

        await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        var webHome = new Uri(environment.JellyfinUrl, "web/").ToString();
        await page.WaitForURLAsync(url => url.StartsWith(webHome, StringComparison.Ordinal) && url.Contains("#/home", StringComparison.Ordinal));
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign In" })).ToHaveCountAsync(0);

        var credentials = JsonDocument.Parse(await page.EvaluateAsync<string>("() => localStorage.getItem('jellyfin_credentials')")).RootElement;
        var server = Assert.Single(credentials.GetProperty("Servers").EnumerateArray());
        var accessToken = server.GetProperty("AccessToken").GetString()!;
        var deviceId = await page.EvaluateAsync<string>("() => localStorage.getItem('_deviceId2')");
        Assert.False(string.IsNullOrEmpty(deviceId));

        // The token jellyfin-web stored is a working session for the user it names.
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var me = await jellyfin.GetAsync("Users/Me", accessToken, TestContext.Current.CancellationToken);
        var userId = Guid.Parse(me.GetProperty("Id").GetString()!);
        Assert.Equal(Guid.Parse(server.GetProperty("UserId").GetString()!), userId);
        Assert.Equal(E2EEnvironment.Alice.Username, me.GetProperty("Name").GetString());

        // Complete cleared the flow cookie.
        Assert.DoesNotContain(await session.Context.CookiesAsync([environment.JellyfinUrl.ToString()]), c => c.Name == "OidcLogin-Flow");
        return (userId, accessToken, deviceId);
    }
}
