using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The second leg of an OIDC Login: <c>/OidcLogin/Callback</c> resolves the Subject and hands over to Complete.
/// </summary>
public sealed class LoginCallbackTests(E2EEnvironment environment)
{
    [Fact]
    public async Task PasskeyLeadsToCompleteWithAHandoffCodeAndALinkedIdentity()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;

        await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        var complete = new Uri(environment.JellyfinUrl, "OidcLogin/Complete").ToString();
        await page.WaitForURLAsync(url => url.StartsWith(complete + "#code=", StringComparison.Ordinal));
        Assert.True(new Uri(page.Url).Fragment.Length > "#code=".Length);

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var token = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var users = await jellyfin.GetAsync("Users", token, TestContext.Current.CancellationToken);
        var alice = users.EnumerateArray().Single(u => u.GetProperty("Name").GetString() == E2EEnvironment.Alice.Username);

        var links = JsonDocument.Parse(await environment.ReadLinkedIdentitiesAsync()).RootElement;
        var link = Assert.Single(links.EnumerateArray(), l => l.GetProperty("Subject").GetString() == E2EEnvironment.Alice.Id);
        Assert.Equal(environment.PocketIdUrl.ToString().TrimEnd('/'), link.GetProperty("Issuer").GetString());
        Assert.Equal(Guid.Parse(alice.GetProperty("Id").GetString()!), link.GetProperty("UserId").GetGuid());
    }
}
