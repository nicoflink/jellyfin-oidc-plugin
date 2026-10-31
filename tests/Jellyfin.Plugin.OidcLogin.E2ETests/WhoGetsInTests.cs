using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Scenarios 5 and 7: refusals of who gets in end on the Plugin's error page with a 403 and a reference code.
/// </summary>
[Collection(nameof(ConfigurationChanges))]
public sealed partial class WhoGetsInTests(E2EEnvironment environment)
{
    [Fact]
    public async Task UnlinkedSubjectWithProvisioningOffIsNotSetUp()
    {
        await environment.WithConfigurationAsync(
            configuration => configuration["justInTimeProvisioning"] = false,
            async () =>
            {
                await using var session = await environment.NewSessionAsync();
                await environment.SignInToPocketIdByOneTimeCodeAsync(session.Page, E2EEnvironment.Bob);

                await AssertRefusedAsync(
                    session.Page,
                    "NotSetUp",
                    "Your account isn't set up for this Jellyfin server. Please ask its administrator.");
            });

        // Nothing was provisioned for Bob.
        using var jellyfin = new JellyfinServer(environment.JellyfinUrl);
        var adminToken = await jellyfin.AuthenticateAsync(E2EEnvironment.JellyfinAdminName, E2EEnvironment.JellyfinAdminPassword, TestContext.Current.CancellationToken);
        var users = await jellyfin.GetAsync("Users", adminToken, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(users.EnumerateArray(), u => u.GetProperty("Name").GetString() == E2EEnvironment.Bob.Username);
    }

    [Fact]
    public async Task OneTimeCodeSignInWithPasskeyRequiredIsPasskeyRequired()
    {
        await environment.WithConfigurationAsync(
            configuration => configuration["requiredAmr"] = "phr",
            async () =>
            {
                await using var session = await environment.NewSessionAsync();
                await environment.SignInToPocketIdByOneTimeCodeAsync(session.Page, E2EEnvironment.Alice);

                await AssertRefusedAsync(
                    session.Page,
                    "PasskeyRequired",
                    "This server only accepts passkeys. Please sign in with your passkey.");
            });
    }

    /// <summary>
    /// Starts an OIDC Login with the PocketID session the page already holds, and asserts it ends on the error page
    /// with a 403, <paramref name="message"/>, and a reference code that finds one log entry for
    /// <paramref name="category"/>.
    /// </summary>
    private async Task AssertRefusedAsync(IPage page, string category, string message)
    {
        var errorPage = new Uri(environment.JellyfinUrl, "OidcLogin/Error").ToString();
        var response = await page.RunAndWaitForResponseAsync(
            async () =>
            {
                await page.GotoAsync(new Uri(environment.JellyfinUrl, "OidcLogin/Start").ToString());
                var signIn = page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true });
                if (!page.Url.StartsWith(errorPage, StringComparison.Ordinal))
                {
                    await signIn.ClickAsync();
                }
            },
            r => r.Url.StartsWith(errorPage, StringComparison.Ordinal));

        Assert.Equal(403, response.Status);
        Assert.Contains($"category={category}", response.Url, StringComparison.Ordinal);
        await Assertions.Expect(page.Locator("#message")).ToHaveTextAsync(message);
        var reference = await page.Locator("#reference").TextContentAsync();
        Assert.Matches(ReferenceFormat(), reference);

        var log = await environment.ReadJellyfinLogAsync();
        var entry = Assert.Single(log.Split('\n'), line => line.Contains(reference!, StringComparison.Ordinal));
        Assert.Contains(category, entry, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[0-9A-Z]{8}$")]
    private static partial Regex ReferenceFormat();
}
