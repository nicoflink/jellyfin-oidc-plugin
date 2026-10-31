using System;
using Jellyfin.Plugin.OidcLogin.Api;
using Jellyfin.Plugin.OidcLogin.Login;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class ErrorPageTests
{
    [Fact]
    public void ShowsTheMessageTheReferenceCodeTryAgainAndBackToLogin()
    {
        var html = ErrorPage.Render(FailureCategory.Unavailable, "7K3QX9TZ");

        Assert.Contains("The sign-in service is unavailable right now.", html, StringComparison.Ordinal);
        Assert.Contains("<code id=\"reference\">7K3QX9TZ</code>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"Start\">Try again</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"../web/#/login\">Back to login</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledShowsBackToLoginOnly()
    {
        var html = ErrorPage.Render(FailureCategory.Cancelled, "7K3QX9TZ");

        Assert.Contains("Sign-in was cancelled.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", html, StringComparison.Ordinal);
        Assert.Contains("Back to login", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledAsksToContactTheAdministrator()
    {
        var html = ErrorPage.Render(FailureCategory.Disabled, "7K3QX9TZ");

        Assert.Contains("Your Jellyfin account is disabled. Please contact the server&#39;s administrator.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledOnlyForNowSaysYouCantSignInRightNow()
    {
        var html = ErrorPage.Render(FailureCategory.Disabled, "7K3QX9TZ", notNow: true);

        Assert.Contains("You can&#39;t sign in right now.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", html, StringComparison.Ordinal);
        Assert.Contains("href=\"Start\">Try again</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NotSetUpSaysTheAccountIsNotSetUp()
    {
        Assert.StartsWith("Your account isn't set up for this Jellyfin server", ErrorPage.Message(FailureCategory.NotSetUp), StringComparison.Ordinal);
    }

    [Fact]
    public void PasskeyRequiredAsksForThePasskey()
    {
        Assert.Contains("sign in with your passkey", ErrorPage.Message(FailureCategory.PasskeyRequired), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("7k3qx9tz")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesOutAnythingThatIsNotAReferenceCode(string? reference)
    {
        var html = ErrorPage.Render(FailureCategory.Failed, reference);

        Assert.DoesNotContain("Reference code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FailureCategory.Cancelled)]
    [InlineData(FailureCategory.Expired)]
    [InlineData(FailureCategory.NotSetUp)]
    [InlineData(FailureCategory.Disabled)]
    [InlineData(FailureCategory.PasskeyRequired)]
    [InlineData(FailureCategory.Unavailable)]
    [InlineData(FailureCategory.Failed)]
    public void EveryCategoryHasItsOwnMessage(FailureCategory category)
    {
        var others = Enum.GetValues<FailureCategory>();

        Assert.All(others, other => Assert.Equal(other == category, ErrorPage.Message(other) == ErrorPage.Message(category)));
    }
}
