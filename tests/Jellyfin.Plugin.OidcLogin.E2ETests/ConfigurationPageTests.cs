using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The guided configuration page in Jellyfin's dashboard, and the admin-only configuration API behind it.
/// </summary>
[Collection(nameof(ConfigurationChanges))]
public sealed class ConfigurationPageTests(E2EEnvironment environment)
{
    [Fact]
    public async Task ConfigurationApiReportsTheClientSecretOnlyAsSet()
    {
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);

        var settings = await jellyfin.GetAsync("OidcLogin/Configuration", adminToken, TestContext.Current.CancellationToken);
        Assert.True(settings.GetProperty("clientSecretSet").GetBoolean());
        Assert.Equal(E2EEnvironment.OidcClientId, settings.GetProperty("configuration").GetProperty("clientId").GetString());
        Assert.DoesNotContain(E2EEnvironment.OidcClientSecret, settings.GetRawText(), StringComparison.Ordinal);

        // Jellyfin's own plugin configuration endpoint doesn't leak it either.
        var builtIn = await jellyfin.GetAsync($"Plugins/{BuildManifest.PluginId}/Configuration", adminToken, TestContext.Current.CancellationToken);
        Assert.Equal(E2EEnvironment.OidcClientId, builtIn.GetProperty("ClientId").GetString());
        Assert.DoesNotContain(E2EEnvironment.OidcClientSecret, builtIn.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationApiIsForAdminsOnly()
    {
        using var http = new HttpClient { BaseAddress = environment.JellyfinUrl };

        using var response = await http.GetAsync(new Uri("OidcLogin/Configuration", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminWalksThroughTheGuidedSetupAndSaves()
    {
        // A Linked Identity exists, so changing the Issuer URL would orphan it.
        await SignInWithPasskeyAsync();

        await using var session = await environment.NewSessionAsync();
        var page = session.Page;
        await LocalLoginAsAdminAsync(page);
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/configurationpage?name=OidcLogin").ToString());

        // Step 1: the callback is derived from the Public Jellyfin URL.
        var configPage = page.Locator("#OidcLoginConfigPage");
        await Assertions.Expect(configPage.GetByLabel("Public Jellyfin URL")).ToHaveValueAsync(environment.JellyfinUrl.ToString());
        await Assertions.Expect(configPage.Locator("#oidcCallbackUrl")).ToHaveTextAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Callback").ToString());
        await Assertions.Expect(configPage.GetByText("Enforce PKCE.")).ToBeVisibleAsync();

        // Step 2: the secret is only "set", and Test connection reaches PocketID from Jellyfin.
        await Assertions.Expect(configPage.Locator("#oidcClientSecretStatus")).ToHaveTextAsync("set");
        await Assertions.Expect(configPage.GetByLabel("Client secret")).ToHaveValueAsync(string.Empty);
        await configPage.GetByRole(AriaRole.Button, new() { Name = "Test connection" }).ClickAsync();
        var issuer = environment.PocketIdUrl.ToString().TrimEnd('/');
        await Assertions.Expect(configPage.Locator("#oidcTestResult")).ToContainTextAsync($"Connected to {issuer}.");

        // Step 4: the button checkbox locks while step 2 is incomplete.
        var showButton = configPage.GetByLabel("Show the sign-in button on the login page");
        await Assertions.Expect(showButton).ToBeEnabledAsync();
        await configPage.GetByLabel("Client ID").FillAsync(string.Empty);
        await Assertions.Expect(showButton).ToBeDisabledAsync();
        await Assertions.Expect(configPage.Locator("#oidcButtonMissing")).ToHaveTextAsync("Finish steps 1 and 2 first, missing: Client ID.");
        await configPage.GetByLabel("Client ID").FillAsync(E2EEnvironment.OidcClientId);
        await Assertions.Expect(showButton).ToBeEnabledAsync();

        // Changing the Issuer URL warns, and declining the confirmation saves nothing.
        var issuerUrl = configPage.GetByLabel("Issuer URL");
        await issuerUrl.FillAsync("https://other-id.example.com");
        await Assertions.Expect(configPage.Locator("#oidcIssuerWarning")).ToContainTextAsync("orphans all");
        string? confirmation = null;
        void Dismiss(object? sender, IDialog dialog)
        {
            confirmation = dialog.Message;
            _ = dialog.DismissAsync();
        }

        page.Dialog += Dismiss;
        await configPage.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(configPage.Locator("#oidcSaveResult")).ToBeHiddenAsync();
        page.Dialog -= Dismiss;
        Assert.Contains("orphans all", confirmation, StringComparison.Ordinal);
        await issuerUrl.FillAsync(issuer);
        await Assertions.Expect(configPage.Locator("#oidcIssuerWarning")).ToBeHiddenAsync();

        // Step 3, then Save: the settings persist, and the stored secret is kept.
        await configPage.GetByLabel("Required Group").FillAsync(E2EEnvironment.JellyfinUsersGroup);
        await configPage.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(configPage.Locator("#oidcSaveResult")).ToHaveTextAsync("Saved.");
        Assert.DoesNotContain(E2EEnvironment.OidcClientSecret, await page.ContentAsync(), StringComparison.Ordinal);

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var settings = await jellyfin.GetAsync("OidcLogin/Configuration", adminToken, TestContext.Current.CancellationToken);
        Assert.Equal(E2EEnvironment.JellyfinUsersGroup, settings.GetProperty("configuration").GetProperty("requiredGroup").GetString());
        Assert.Equal(issuer, settings.GetProperty("configuration").GetProperty("issuerUrl").GetString());
        Assert.True(settings.GetProperty("clientSecretSet").GetBoolean());

        // Put the shared environment back; a passkey sign-in still works with the kept secret.
        await configPage.GetByLabel("Required Group").FillAsync(string.Empty);
        await configPage.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(configPage.Locator("#oidcSaveResult")).ToHaveTextAsync("Saved.");
        await SignInWithPasskeyAsync();
    }

    private async Task LocalLoginAsAdminAsync(IPage page)
    {
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/login").ToString());
        var manualLogin = page.GetByRole(AriaRole.Button, new() { Name = "Manual Login" });
        var userName = page.GetByLabel("User");
        await Assertions.Expect(manualLogin.Or(userName).First).ToBeVisibleAsync();
        if (!await userName.IsVisibleAsync())
        {
            await manualLogin.ClickAsync();
        }

        await userName.FillAsync(E2EEnvironment.JellyfinAdminName);
        await page.GetByLabel("Password").FillAsync(E2EEnvironment.JellyfinAdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("#/home", StringComparison.Ordinal));
    }

    private async Task SignInWithPasskeyAsync()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/web/", StringComparison.Ordinal) && url.Contains("#/home", StringComparison.Ordinal));
        var credentials = JsonDocument.Parse(await page.EvaluateAsync<string>("() => localStorage.getItem('jellyfin_credentials')")).RootElement;
        Assert.Single(credentials.GetProperty("Servers").EnumerateArray());
    }
}
