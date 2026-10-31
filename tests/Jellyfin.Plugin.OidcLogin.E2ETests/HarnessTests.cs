using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Proves the harness itself: both containers are up and wired, and the passkey works at PocketID.
/// </summary>
public sealed class HarnessTests(E2EEnvironment environment)
{
    [Fact]
    public async Task PluginIsLoadedInJellyfin()
    {
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var token = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);

        var plugins = await jellyfin.GetAsync("Plugins", token, TestContext.Current.CancellationToken);

        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "OIDC Login");
        Assert.Equal("Active", plugin.GetProperty("Status").GetString());
    }

    [Fact]
    public void InstallingFromABuildMissingAnArtifactFailsNamingIt()
    {
        var bin = Directory.CreateTempSubdirectory("oidclogin-bin");
        try
        {
            foreach (var artifact in BuildManifest.Artifacts.Skip(1))
            {
                File.WriteAllText(Path.Combine(bin.FullName, artifact), string.Empty);
            }

            var error = Assert.Throws<InvalidOperationException>(() => BuildManifest.ArtifactFiles(bin.FullName));

            Assert.Contains(BuildManifest.Artifacts[0], error.Message, StringComparison.Ordinal);
            Assert.Contains("--no-incremental", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            bin.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JellyfinReachesIssuerAtBrowserUrl()
    {
        var discovery = JsonDocument.Parse(await environment.FetchFromJellyfinAsync(new Uri(environment.PocketIdUrl, ".well-known/openid-configuration")));

        Assert.Equal(environment.PocketIdUrl.ToString().TrimEnd('/'), discovery.RootElement.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task PasskeySignsInAtPocketIdFromFreshContext()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;

        await page.GotoAsync(new Uri(environment.PocketIdUrl, "login").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Authenticate" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/settings", StringComparison.Ordinal));

        var me = await session.Context.APIRequest.GetAsync(new Uri(environment.PocketIdUrl, "api/users/me").ToString());
        Assert.True(me.Ok, $"api/users/me returned {me.Status}");
        var user = await me.JsonAsync();
        Assert.Equal(E2EEnvironment.Alice.Id, user?.GetProperty("id").GetString());
    }

    [Fact]
    public async Task LocalLoginWorksForSetupAdmin()
    {
        await using var session = await environment.NewSessionAsync();
        var page = session.Page;

        await page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/login").ToString());
        await page.GetByLabel("User").FillAsync(E2EEnvironment.JellyfinAdminName);
        await page.GetByLabel("Password").FillAsync(E2EEnvironment.JellyfinAdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("#/home", StringComparison.Ordinal));

        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var token = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var sessions = await jellyfin.GetAsync("Sessions", token, TestContext.Current.CancellationToken);
        Assert.Contains(
            sessions.EnumerateArray(),
            s => s.GetProperty("Client").GetString() == "Jellyfin Web" && s.GetProperty("UserName").GetString() == E2EEnvironment.JellyfinAdminName);
    }
}
