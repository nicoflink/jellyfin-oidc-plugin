using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Scenario 5 and the session rule: OIDC Login respects the Jellyfin user's state. Disabling Alice would refuse every
/// other test's login, so these run alone.
/// </summary>
[Collection(nameof(ConfigurationChanges))]
public sealed partial class UserLifecycleTests(E2EEnvironment environment)
{
    [Fact]
    public async Task DisabledUserEndsOnTheDisabledErrorPageAndKeepsItsLink()
    {
        await using (var first = await environment.NewSessionAsync(withAlicePasskey: true))
        {
            await SignInAsync(first.Page);
        }

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var aliceId = await AliceIdAsync(jellyfin, adminToken);
        await SetDisabledAsync(jellyfin, adminToken, aliceId, disabled: true);
        try
        {
            await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
            var page = session.Page;
            var completeUrl = new Uri(environment.JellyfinUrl, "OidcLogin/Complete").ToString();
            var errorPage = new Uri(environment.JellyfinUrl, "OidcLogin/Error").ToString();

            var complete = await page.RunAndWaitForResponseAsync(
                () => StartAndSignInAtPocketIdAsync(page),
                r => r.Url == completeUrl && r.Request.Method == "POST");
            Assert.Equal(403, complete.Status);

            // The Complete page carries the endpoint's {category, ref} to the error page.
            await page.WaitForURLAsync(url => url.StartsWith(errorPage, StringComparison.Ordinal));
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(page.Url).Query);
            Assert.Equal("Disabled", query["category"]);
            Assert.Null(query["notNow"]);
            var reference = query["ref"];
            Assert.Matches(ReferenceFormat(), reference);
            var errorResponse = await page.ReloadAsync();
            Assert.Equal(403, errorResponse!.Status);
            await Assertions.Expect(page.Locator("#message")).ToHaveTextAsync("Your Jellyfin account is disabled. Please contact the server's administrator.");
            await Assertions.Expect(page.Locator("#reference")).ToHaveTextAsync(reference!);
            Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('jellyfin_credentials')"));

            // The reference code finds exactly one entry in Jellyfin's log, and it names the cause.
            var log = await environment.ReadJellyfinLogAsync();
            var entry = Assert.Single(log.Split('\n'), line => line.Contains(reference!, StringComparison.Ordinal));
            Assert.Contains("Disabled", entry, StringComparison.Ordinal);
            Assert.Contains("is disabled", entry, StringComparison.Ordinal);

            var links = JsonDocument.Parse(await environment.ReadLinkedIdentitiesAsync()).RootElement;
            var link = Assert.Single(links.EnumerateArray(), l => l.GetProperty("Subject").GetString() == E2EEnvironment.Alice.Id);
            Assert.Equal(aliceId, link.GetProperty("UserId").GetGuid());
        }
        finally
        {
            await SetDisabledAsync(jellyfin, adminToken, aliceId, disabled: false);
        }
    }

    [Fact]
    public async Task ANewLoginInABrowserReplacesItsEarlierSession()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var firstToken = await SignInAsync(session.Page);
        var secondToken = await SignInAsync(session.Page, signedInAtPocketId: true);

        Assert.NotEqual(firstToken, secondToken);
        var deviceId = await session.Page.EvaluateAsync<string>("() => localStorage.getItem('_deviceId2')");
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var devices = await jellyfin.GetAsync("Devices", adminToken, TestContext.Current.CancellationToken);
        Assert.Single(devices.GetProperty("Items").EnumerateArray(), d => d.GetProperty("Id").GetString() == deviceId);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => jellyfin.GetAsync("Users/Me", firstToken, TestContext.Current.CancellationToken));
        await jellyfin.GetAsync("Users/Me", secondToken, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeletingTheUserRemovesItsLinkAndTheNextLoginProvisionsAfresh()
    {
        await using (var first = await environment.NewSessionAsync(withAlicePasskey: true))
        {
            await SignInAsync(first.Page);
        }

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var deletedId = await AliceIdAsync(jellyfin, adminToken);

        await jellyfin.DeleteAsAdminAsync($"Users/{deletedId}", adminToken, TestContext.Current.CancellationToken);

        var links = JsonDocument.Parse(await environment.ReadLinkedIdentitiesAsync()).RootElement;
        Assert.DoesNotContain(links.EnumerateArray(), l => l.GetProperty("Subject").GetString() == E2EEnvironment.Alice.Id);
        await using (var again = await environment.NewSessionAsync(withAlicePasskey: true))
        {
            await SignInAsync(again.Page);
        }

        var provisionedId = await AliceIdAsync(jellyfin, adminToken);
        Assert.NotEqual(deletedId, provisionedId);
        links = JsonDocument.Parse(await environment.ReadLinkedIdentitiesAsync()).RootElement;
        var link = Assert.Single(links.EnumerateArray(), l => l.GetProperty("Subject").GetString() == E2EEnvironment.Alice.Id);
        Assert.Equal(provisionedId, link.GetProperty("UserId").GetGuid());
    }

    private static async Task<Guid> AliceIdAsync(JellyfinServer jellyfin, string adminToken)
    {
        var users = await jellyfin.GetAsync("Users", adminToken, TestContext.Current.CancellationToken);
        var alice = Assert.Single(users.EnumerateArray(), u => u.GetProperty("Name").GetString() == E2EEnvironment.Alice.Username);
        return Guid.Parse(alice.GetProperty("Id").GetString()!);
    }

    private static async Task SetDisabledAsync(JellyfinServer jellyfin, string adminToken, Guid userId, bool disabled)
    {
        var user = await jellyfin.GetAsync($"Users/{userId}", adminToken, TestContext.Current.CancellationToken);
        var policy = JsonNode.Parse(user.GetProperty("Policy").GetRawText())!.AsObject();
        policy["IsDisabled"] = disabled;
        await jellyfin.PostAsAdminAsync($"Users/{userId}/Policy", policy, adminToken, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Starts an OIDC Login in <paramref name="page"/> and signs in at PocketID with Alice's passkey, unless the
    /// browser is still signed in there and PocketID answers straight away.
    /// </summary>
    private async Task StartAndSignInAtPocketIdAsync(IPage page, bool signedInAtPocketId = false)
    {
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
        if (!signedInAtPocketId)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        }
    }

    /// <summary>
    /// Runs an OIDC Login in <paramref name="page"/> up to Jellyfin's home page and returns the stored access token.
    /// </summary>
    private async Task<string> SignInAsync(IPage page, bool signedInAtPocketId = false)
    {
        await StartAndSignInAtPocketIdAsync(page, signedInAtPocketId);
        var webHome = new Uri(environment.JellyfinUrl, "web/").ToString();
        await page.WaitForURLAsync(url => url.StartsWith(webHome, StringComparison.Ordinal) && url.Contains("#/home", StringComparison.Ordinal));
        var credentials = JsonDocument.Parse(await page.EvaluateAsync<string>("() => localStorage.getItem('jellyfin_credentials')")).RootElement;
        return credentials.GetProperty("Servers").EnumerateArray().Single().GetProperty("AccessToken").GetString()!;
    }

    [GeneratedRegex("^[0-9A-Z]{8}$")]
    private static partial Regex ReferenceFormat();
}
