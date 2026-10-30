using System;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// The first leg of an OIDC Login: <c>/OidcLogin/Start</c> sends the browser to PocketID.
/// </summary>
public sealed class LoginStartTests(E2EEnvironment environment)
{
    [Fact]
    public async Task StartLandsOnPocketIdPasskeyPromptWithFlowCookie()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;

        var start = new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString();
        var startRequest = (await page.GotoAsync(start))!.Request;
        while (startRequest.RedirectedFrom is { } previous)
        {
            startRequest = previous;
        }

        Assert.Equal(start, startRequest.Url);
        var startResponse = (await startRequest.ResponseAsync())!;
        Assert.Equal(302, startResponse.Status);
        var authorizationUrl = new Uri(startResponse.Headers["location"]);
        Assert.Equal(environment.PocketIdUrl.Authority, authorizationUrl.Authority);
        await page.WaitForURLAsync(url => new Uri(url).Authority == environment.PocketIdUrl.Authority);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Sign in to {E2EEnvironment.OidcClientId}" })).ToBeVisibleAsync();
        var signIn = page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true });
        await Assertions.Expect(signIn).ToBeVisibleAsync();

        var cookie = Assert.Single(await session.Context.CookiesAsync([environment.JellyfinUrl.ToString()]), c => c.Name == "OidcLogin-Flow");
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.HttpOnly);
        Assert.False(cookie.Secure);
        Assert.Equal(SameSiteAttribute.Lax, cookie.SameSite);

        // The passkey completes the authorization PocketID accepted, so it comes back to the Callback with a code.
        var authorizeState = HttpUtility.ParseQueryString(authorizationUrl.Query)["state"];
        var callback = new Uri(environment.JellyfinUrl, "OidcLogin/Callback").ToString();
        await signIn.ClickAsync();
        await page.WaitForURLAsync(url => url.StartsWith(callback, StringComparison.Ordinal));

        var query = HttpUtility.ParseQueryString(new Uri(page.Url).Query);
        Assert.Null(query["error"]);
        Assert.False(string.IsNullOrEmpty(query["code"]));
        Assert.False(string.IsNullOrEmpty(authorizeState));
        Assert.Equal(authorizeState, query["state"]);
    }
}
