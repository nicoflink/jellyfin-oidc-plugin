using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The Linked Identities section of the configuration page: list, link by hand and unlink.
/// </summary>
[Collection(nameof(ConfigurationChanges))]
public sealed class LinkedIdentitiesTests(E2EEnvironment environment)
{
    [Fact]
    public async Task LinkedIdentitiesApiIsForAdminsOnly()
    {
        using var http = new HttpClient { BaseAddress = environment.JellyfinUrl };

        using var response = await http.GetAsync(new Uri("OidcLogin/LinkedIdentities", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminMovesAliceToAnExistingUserAndBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = E2EEnvironment.Alice.Id;
        var aliceUserId = await environment.SignInWithPasskeyAsync();
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, cancellationToken);
        var carol = await jellyfin.PostAsAdminAsync("Users/New", new { Name = "carol", Password = "carol-password" }, adminToken, cancellationToken);
        var carolId = Guid.Parse(carol.GetProperty("Id").GetString()!);

        await using var session = await environment.NewSessionAsync();
        var page = session.Page;
        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await environment.LocalLoginAsAdminAsync(page);
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/configurationpage?name=OidcLogin").ToString());
        var section = page.Locator("#OidcLoginConfigPage #oidcLinkedIdentitiesSection");
        var rows = section.Locator("#oidcLinkedIdentities tbody tr");

        // The table lists Alice's provisioned link.
        var aliceRow = rows.Filter(new() { HasText = subject });
        await Assertions.Expect(aliceRow).ToHaveCountAsync(1);
        await Assertions.Expect(aliceRow.Locator("td").Nth(1)).ToHaveTextAsync(E2EEnvironment.Alice.Username);
        await Assertions.Expect(aliceRow.Locator("td").Nth(3)).Not.ToHaveTextAsync("Never");

        // One-to-one: neither a linked Subject nor a linked user can be linked again.
        var userPicker = section.GetByLabel("Jellyfin user");
        var sub = section.GetByLabel("Subject (sub)");
        var link = section.GetByRole(AriaRole.Button, new() { Name = "Link", Exact = true });
        var linkResult = section.Locator("#oidcLinkResult");
        await userPicker.SelectOptionAsync(new SelectOptionValue { Label = "carol" });
        await sub.FillAsync(subject);
        await link.ClickAsync();
        await Assertions.Expect(linkResult).ToHaveTextAsync($"Subject {subject} is already linked to alice.");
        await userPicker.SelectOptionAsync(new SelectOptionValue { Label = "alice" });
        await sub.FillAsync("some-other-sub");
        await link.ClickAsync();
        await Assertions.Expect(linkResult).ToHaveTextAsync($"alice is already linked to Subject {subject}.");

        // Unlink Alice, then link her Subject to carol by hand.
        await aliceRow.GetByRole(AriaRole.Button, new() { Name = "Unlink" }).ClickAsync();
        await Assertions.Expect(aliceRow).ToHaveCountAsync(0);
        await userPicker.SelectOptionAsync(new SelectOptionValue { Label = "carol" });
        await sub.FillAsync(subject);
        await link.ClickAsync();
        await Assertions.Expect(linkResult).ToHaveTextAsync("Linked.");
        var carolRow = rows.Filter(new() { HasText = subject });
        await Assertions.Expect(carolRow.Locator("td").Nth(1)).ToHaveTextAsync("carol");
        await Assertions.Expect(carolRow.Locator("td").Nth(3)).ToHaveTextAsync("Never");

        // Alice's next OIDC Login reaches carol.
        Assert.Equal(carolId, await environment.SignInWithPasskeyAsync());
        await page.ReloadAsync();
        await Assertions.Expect(carolRow.Locator("td").Nth(3)).Not.ToHaveTextAsync("Never");

        // After unlinking, her next login provisions a new user (named alice again once the old one is gone).
        await carolRow.GetByRole(AriaRole.Button, new() { Name = "Unlink" }).ClickAsync();
        await Assertions.Expect(rows.Filter(new() { HasText = subject })).ToHaveCountAsync(0);
        await jellyfin.DeleteAsAdminAsync($"Users/{aliceUserId}", adminToken, cancellationToken);
        await jellyfin.DeleteAsAdminAsync($"Users/{carolId}", adminToken, cancellationToken);
        var provisioned = await environment.SignInWithPasskeyAsync();
        Assert.NotEqual(aliceUserId, provisioned);
        Assert.NotEqual(carolId, provisioned);
        var me = await jellyfin.GetAsync($"Users/{provisioned}", adminToken, cancellationToken);
        Assert.Equal(E2EEnvironment.Alice.Username, me.GetProperty("Name").GetString());
    }
}
