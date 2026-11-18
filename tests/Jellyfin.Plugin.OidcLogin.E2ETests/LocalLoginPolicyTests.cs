using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The Local Login Policy: Local Login refused on both of Jellyfin's routes, Quick Connect unaffected, the
/// login page adapted, and the setting on the configuration page. <c>LocalNetworkOnly</c> is covered by unit tests
/// only: a request from outside the local network can't be produced reliably in the container network.
/// </summary>
[Collection(nameof(ConfigurationChanges))]
public sealed class LocalLoginPolicyTests(E2EEnvironment environment)
{
    private const string Refusal = "Password sign-in is disabled on this server.";

    [Fact]
    public async Task DisabledRefusesBothLocalLoginRoutesAndAllowedAcceptsThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, cancellationToken);
        var adminId = await AdminIdAsync(jellyfin, adminToken);

        await environment.WithConfigurationAsync(c => c["localLoginPolicy"] = "Disabled", async () =>
        {
            var logBefore = (await environment.ReadJellyfinLogAsync()).Length;
            using var byName = await AuthenticateByNameAsync(jellyfin);
            Assert.Equal(HttpStatusCode.Forbidden, byName.StatusCode);
            Assert.Equal(Refusal, await byName.Content.ReadAsStringAsync(cancellationToken));

            using var byId = await AuthenticateByIdAsync(jellyfin, adminId);
            Assert.Equal(HttpStatusCode.Forbidden, byId.StatusCode);
            Assert.Equal(Refusal, await byId.Content.ReadAsStringAsync(cancellationToken));

            // The same refusal for a user that doesn't exist.
            using var unknown = await jellyfin.PostAsAppAsync("Users/AuthenticateByName", new { Username = "nobody", Pw = "guess" }, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(Refusal, await unknown.Content.ReadAsStringAsync(cancellationToken));

            // One entry per refusal, naming neither the username nor the password.
            var log = (await environment.ReadJellyfinLogAsync())[logBefore..];
            var refusals = log.Split('\n').Where(l => l.Contains("refused by the Local Login Policy Disabled", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, refusals.Length);
            Assert.DoesNotContain(refusals, l => l.Contains(E2EEnvironment.JellyfinAdminPassword, StringComparison.Ordinal) || l.Contains("nobody", StringComparison.Ordinal));
        });

        using var allowedByName = await AuthenticateByNameAsync(jellyfin);
        Assert.Equal(HttpStatusCode.OK, allowedByName.StatusCode);
        using var allowedById = await AuthenticateByIdAsync(jellyfin, adminId);
        Assert.Equal(HttpStatusCode.OK, allowedById.StatusCode);
    }

    [Fact]
    public async Task QuickConnectApprovedFromAnOidcSessionStillSignsInWhenDisabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await environment.WithConfigurationAsync(c => c["localLoginPolicy"] = "Disabled", async () =>
        {
            await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
            var page = session.Page;
            await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/web/", StringComparison.Ordinal) && url.Contains("#/home", StringComparison.Ordinal));
            var credentials = JsonDocument.Parse(await page.EvaluateAsync<string>("() => localStorage.getItem('jellyfin_credentials')")).RootElement;
            var aliceToken = credentials.GetProperty("Servers").EnumerateArray().Single().GetProperty("AccessToken").GetString()!;

            using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
            using var initiated = await jellyfin.PostAsAppAsync("QuickConnect/Initiate", null, cancellationToken);
            initiated.EnsureSuccessStatusCode();
            var quickConnect = JsonDocument.Parse(await initiated.Content.ReadAsStringAsync(cancellationToken)).RootElement;
            var code = quickConnect.GetProperty("Code").GetString()!;
            var secret = quickConnect.GetProperty("Secret").GetString()!;

            await jellyfin.PostAsAdminAsync($"QuickConnect/Authorize?code={Uri.EscapeDataString(code)}", new { }, aliceToken, cancellationToken);

            using var signedIn = await jellyfin.PostAsAppAsync("Users/AuthenticateWithQuickConnect", new { Secret = secret }, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            var result = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync(cancellationToken)).RootElement;
            Assert.False(string.IsNullOrEmpty(result.GetProperty("AccessToken").GetString()));
            Assert.Equal(E2EEnvironment.Alice.Username, result.GetProperty("User").GetProperty("Name").GetString());
        });
    }

    [Fact]
    public async Task DisabledHasNoEffectUntilThePluginIsFullyConfigured()
    {
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);

        await environment.WithConfigurationAsync(
            c =>
            {
                c["localLoginPolicy"] = "Disabled";
                c["clientId"] = string.Empty;
            },
            async () =>
            {
                using var byName = await AuthenticateByNameAsync(jellyfin);
                Assert.Equal(HttpStatusCode.OK, byName.StatusCode);
            });
    }

    [Fact]
    public async Task LoginPageHidesLocalLoginAndShowsTheHintWhenDisabled()
    {
        await environment.WithConfigurationAsync(c => c["localLoginPolicy"] = "Disabled", async () =>
        {
            await using var session = await environment.NewSessionAsync();
            var page = session.Page;
            await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/login").ToString());

            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in with PocketID" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Password sign-in is disabled. Use Quick Connect in apps.")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Use Quick Connect" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#loginPage .manualLoginForm")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#loginPage .visualLoginForm")).ToBeHiddenAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Manual Login" })).ToBeHiddenAsync();
            await Assertions.Expect(page.GetByLabel("Password")).ToBeHiddenAsync();
        });
    }

    [Fact]
    public async Task AdminChoosesThePolicySeesLockoutWarningsAndSaves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        await using var session = await environment.NewSessionAsync();
        var page = session.Page;
        await environment.LocalLoginAsAdminAsync(page);

        // Puts the Local Login Policy back afterwards, whatever the page saved.
        await environment.WithConfigurationAsync(_ => { }, async adminToken =>
        {
            var serverConfiguration = await jellyfin.GetAsync("System/Configuration", adminToken, cancellationToken);
            var quickConnectOff = JsonNode.Parse(serverConfiguration.GetRawText())!.AsObject();
            quickConnectOff["QuickConnectAvailable"] = false;
            await jellyfin.PostAsAdminAsync("System/Configuration", quickConnectOff, adminToken, cancellationToken);
            try
            {
                await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/configurationpage?name=OidcLogin").ToString());
                var configPage = page.Locator("#OidcLoginConfigPage");
                var warnings = configPage.Locator("#oidcLocalLoginWarnings");
                var allowed = configPage.GetByRole(AriaRole.Radio, new() { Name = "Allowed" });
                var disabled = configPage.GetByRole(AriaRole.Radio, new() { Name = "Disabled" });
                await Assertions.Expect(allowed).ToBeCheckedAsync();
                await Assertions.Expect(warnings).ToBeEmptyAsync();

                // The setup admin has no Linked Identity, and Quick Connect is off.
                await disabled.CheckAsync();
                await Assertions.Expect(warnings).ToContainTextAsync("no Linked Identity");
                await Assertions.Expect(warnings).ToContainTextAsync("is an administrator");
                await Assertions.Expect(warnings).ToContainTextAsync("Quick Connect is switched off");

                // Warnings don't block saving.
                await configPage.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
                await Assertions.Expect(configPage.Locator("#oidcSaveResult")).ToHaveTextAsync("Saved.");
                var settings = await jellyfin.GetAsync("OidcLogin/Configuration", adminToken, cancellationToken);
                Assert.Equal("Disabled", settings.GetProperty("configuration").GetProperty("localLoginPolicy").GetString());
                var lockoutFacts = settings.GetProperty("lockoutFacts");
                Assert.False(lockoutFacts.GetProperty("quickConnectEnabled").GetBoolean());
                Assert.True(lockoutFacts.GetProperty("unlinkedUsers").GetProperty("administrators").GetInt32() >= 1);
                using (var refused = await AuthenticateByNameAsync(jellyfin))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                }

                await Assertions.Expect(disabled).ToBeCheckedAsync();
                await allowed.CheckAsync();
                await Assertions.Expect(warnings).ToBeEmptyAsync();
                await configPage.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
                await Assertions.Expect(configPage.Locator("#oidcSaveResult")).ToHaveTextAsync("Saved.");
            }
            finally
            {
                await jellyfin.PostAsAdminAsync("System/Configuration", serverConfiguration, adminToken, CancellationToken.None);
            }
        });

        using var accepted = await AuthenticateByNameAsync(jellyfin);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task SavingAnUnknownPolicyIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, cancellationToken);
        var settings = await jellyfin.GetAsync("OidcLogin/Configuration", adminToken, cancellationToken);

        foreach (JsonNode policy in new JsonNode[] { "Everywhere", 7 })
        {
            var configuration = JsonNode.Parse(settings.GetProperty("configuration").GetRawText())!.AsObject();
            configuration["localLoginPolicy"] = policy;
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => jellyfin.PostAsAdminAsync("OidcLogin/Configuration", new { Configuration = configuration }, adminToken, cancellationToken));
            Assert.Contains("400", error.Message, StringComparison.Ordinal);
        }

        var unchanged = await jellyfin.GetAsync("OidcLogin/Configuration", adminToken, cancellationToken);
        Assert.Equal("Allowed", unchanged.GetProperty("configuration").GetProperty("localLoginPolicy").GetString());
    }

    private static Task<HttpResponseMessage> AuthenticateByNameAsync(JellyfinServer jellyfin)
        => jellyfin.PostAsAppAsync(
            "Users/AuthenticateByName",
            new { Username = E2EEnvironment.JellyfinAdminName, Pw = E2EEnvironment.JellyfinAdminPassword },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> AuthenticateByIdAsync(JellyfinServer jellyfin, Guid userId)
        => jellyfin.PostAsAppAsync(
            $"Users/{userId:N}/Authenticate?pw={Uri.EscapeDataString(E2EEnvironment.JellyfinAdminPassword)}",
            null,
            TestContext.Current.CancellationToken);

    private static async Task<Guid> AdminIdAsync(JellyfinServer jellyfin, string adminToken)
    {
        var me = await jellyfin.GetAsync("Users/Me", adminToken, TestContext.Current.CancellationToken);
        return Guid.Parse(me.GetProperty("Id").GetString()!);
    }
}
