using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The Login Entry Point: the Branding filter adds the button for jellyfin-web only (scenario 3).
/// </summary>
public sealed class LoginEntryPointTests(E2EEnvironment environment)
{
    [Fact]
    public async Task JellyfinWebNamesItselfOnTheAnonymousBrandingRequestAndShowsTheButton()
    {
        await using var session = await environment.NewSessionAsync();
        var page = session.Page;

        var branding = new Uri(environment.JellyfinUrl, "Branding/Configuration").ToString();
        var request = await page.RunAndWaitForRequestAsync(
            () => page.GotoAsync(new Uri(environment.JellyfinUrl, "web/#/login").ToString()),
            r => r.Url.StartsWith(branding, StringComparison.Ordinal));

        var authorization = await request.HeaderValueAsync("authorization");
        Assert.NotNull(authorization);
        Assert.Contains("Client=\"Jellyfin Web\"", WebUtility.UrlDecode(authorization), StringComparison.Ordinal);
        Assert.DoesNotContain("Token=", authorization, StringComparison.Ordinal);

        var response = (await request.ResponseAsync())!;
        Assert.Contains("Authorization", response.Headers["vary"], StringComparison.OrdinalIgnoreCase);

        var button = page.GetByRole(AriaRole.Button, new() { Name = "Sign in with PocketID" });
        await Assertions.Expect(button).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("form.oidc-login-entry")).ToHaveAttributeAsync("action", new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
    }

    [Theory]
    [InlineData("MediaBrowser Client=\"Jellyfin Android\", Device=\"Pixel 9\", DeviceId=\"oidclogin-e2e-android\", Version=\"2.6.3\"")]
    [InlineData(null)]
    public async Task NoButtonForOtherClients(string? authorization)
    {
        using var http = new HttpClient { BaseAddress = environment.JellyfinUrl };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("Branding/Configuration", UriKind.Relative));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Contains("Authorization", response.Headers.Vary);

        var branding = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        // Jellyfin leaves out a null disclaimer.
        var disclaimer = branding.TryGetProperty("LoginDisclaimer", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        Assert.DoesNotContain("OidcLogin/Start", disclaimer, StringComparison.Ordinal);
    }
}
