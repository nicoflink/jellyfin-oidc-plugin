using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Scenario 6: a login while the Identity Provider is down ends on the Plugin's error page.
/// </summary>
[Collection(nameof(IdentityProviderOutage))]
public sealed partial class FailureHandlingTests(E2EEnvironment environment)
{
    [Fact]
    public async Task LoginWhilePocketIdIsStoppedEndsOnTheUnavailableErrorPage()
    {
        await using var session = await environment.NewSessionAsync(withAlicePasskey: true);
        var page = session.Page;

        // Hold PocketID's final redirect to Callback, so PocketID can be taken down before Callback runs. Playwright
        // doesn't route redirect hops, so the redirect itself is fetched and replayed.
        var authorize = new Uri(environment.PocketIdUrl, "authorize?interaction=").ToString();
        var heldRedirect = new TaskCompletionSource<(IRoute Route, IAPIResponse Response)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(url => url.StartsWith(authorize, StringComparison.Ordinal), async route =>
        {
            var redirect = await route.FetchAsync(new() { MaxRedirects = 0 });
            heldRedirect.TrySetResult((route, redirect));
        });
        await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        var (route, redirect) = await heldRedirect.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.StartsWith(new Uri(environment.JellyfinUrl, "OidcLogin/Callback").ToString(), redirect.Headers["location"], StringComparison.Ordinal);

        await environment.WithPocketIdStoppedAsync(async () =>
        {
            var errorPage = new Uri(environment.JellyfinUrl, "OidcLogin/Error").ToString();
            var response = await page.RunAndWaitForResponseAsync(
                () => route.FulfillAsync(new() { Response = redirect }),
                r => r.Url.StartsWith(errorPage, StringComparison.Ordinal));

            Assert.Equal(502, response.Status);
            var headers = await response.AllHeadersAsync();
            Assert.Contains("frame-ancestors 'none'", headers["content-security-policy"], StringComparison.Ordinal);
            Assert.Equal("no-store", headers["cache-control"]);
            Assert.Equal("no-referrer", headers["referrer-policy"]);

            await Assertions.Expect(page.Locator("#message")).ToHaveTextAsync("The sign-in service is unavailable right now. Please try again later.");
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Try again" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to login" })).ToBeVisibleAsync();
            var reference = await page.Locator("#reference").TextContentAsync();
            Assert.Matches(ReferenceFormat(), reference);

            // The reference code finds exactly one entry in Jellyfin's log, and it names the cause.
            var log = await environment.ReadJellyfinLogAsync();
            var entry = Assert.Single(log.Split('\n'), line => line.Contains(reference!, StringComparison.Ordinal));
            Assert.Contains("Unavailable", entry, StringComparison.Ordinal);
            Assert.DoesNotContain(E2EEnvironment.OidcClientSecret, log, StringComparison.Ordinal);
        });
    }

    [GeneratedRegex("^[0-9A-Z]{8}$")]
    private static partial Regex ReferenceFormat();
}
