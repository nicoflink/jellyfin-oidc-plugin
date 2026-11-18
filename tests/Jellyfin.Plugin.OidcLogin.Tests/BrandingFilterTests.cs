using System.Collections.Generic;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.LocalLogin;
using Jellyfin.Plugin.OidcLogin.LoginEntryPoint;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class BrandingFilterTests
{
    private const string JellyfinWeb = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\", DeviceId=\"abc\", Version=\"12.4.0\"";

    /// <summary>
    /// A same-tab form (jellyfin-web forces disclaimer links into a new tab) on one line, so Markdown reads it as one
    /// HTML block, with only the Plugin's own class names and inline styles.
    /// </summary>
    private const string Button =
        "<form class=\"oidc-login-entry\" action=\"https://jellyfin.example.test/OidcLogin/Start\" method=\"get\">"
        + "<button type=\"submit\" class=\"oidc-login-button\" style=\"display:block;box-sizing:border-box;width:100%;"
        + "margin:1.25em 0 0;padding:0.9em 1em;border:0;border-radius:0.2em;background:#00a4dc;color:#fff;font:inherit;"
        + "font-weight:600;cursor:pointer\">Sign in with PocketID</button></form>";

    private const string Hint = "<p class=\"oidc-login-hint\" style=\"margin:1em 0 0;text-align:center;opacity:0.8\">"
        + "Password sign-in is disabled. Use Quick Connect in apps.</p>";

    /// <summary>
    /// Hides jellyfin-web's Local Login: the manual login form, its "Manual Login" button and the user tiles, leaving the Quick
    /// Connect button and the disclaimer.
    /// </summary>
    private const string HideLocalLoginCss =
        "#loginPage .manualLoginForm,#loginPage .visualLoginForm,#loginPage .btnManual{display:none!important}";

    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test/",
        IssuerUrl = "https://id.example.test",
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
        ShowSignInButton = true,
    };

    private readonly FakeLocalNetwork _localNetwork = new();

    [Fact]
    public void AppendsSameTabFormButtonAfterTheAdminsTextForJellyfinWeb()
    {
        Assert.Equal("Welcome home.\n\n" + Button, Filter(JellyfinWeb, "Welcome home."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ButtonIsTheWholeDisclaimerWhenTheAdminHasNone(string? loginDisclaimer)
    {
        Assert.Equal(Button, Filter(JellyfinWeb, loginDisclaimer));
    }

    [Fact]
    public void ReadsTheClientNameUrlEncodedAsJellyfinDoes()
    {
        Assert.Equal(Button, Filter("MediaBrowser Client=\"Jellyfin%20Web\", Device=\"Chrome\"", null));
    }

    [Fact]
    public void StartUrlKeepsThePublicJellyfinUrlsBasePath()
    {
        _configuration.PublicJellyfinUrl = "https://example.test/jellyfin";

        Assert.Contains("action=\"https://example.test/jellyfin/OidcLogin/Start\"", Filter(JellyfinWeb, null), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MediaBrowser Client=\"Jellyfin Android\", Device=\"Pixel\", DeviceId=\"abc\", Version=\"2.6.3\"")]
    [InlineData("MediaBrowser Client=\"Jellyfin Media Player\", Device=\"desktop\"")]
    [InlineData("MediaBrowser Client=\"jellyfin web\", Device=\"Chrome\"")]
    [InlineData("MediaBrowser Client=\"Jellyfin Web 2\", Device=\"Chrome\"")]
    [InlineData("MediaBrowser Device=\"Chrome\", DeviceId=\"abc\"")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesTheDisclaimerAloneForAnyOtherClient(string? authorization)
    {
        Assert.Equal("Welcome home.", Filter(authorization, "Welcome home."));
    }

    [Fact]
    public void LeavesTheDisclaimerAloneWhenTheButtonSettingIsOff()
    {
        _configuration.ShowSignInButton = false;

        Assert.Equal("Welcome home.", Filter(JellyfinWeb, "Welcome home."));
    }

    [Theory]
    [InlineData("PublicJellyfinUrl")]
    [InlineData("IssuerUrl")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public void LeavesTheDisclaimerAloneUntilThePluginIsFullyConfigured(string missing)
    {
        typeof(PluginConfiguration).GetProperty(missing)!.SetValue(_configuration, string.Empty);

        Assert.Equal("Welcome home.", Filter(JellyfinWeb, "Welcome home."));
    }

    [Theory]
    [InlineData(JellyfinWeb)]
    [InlineData("MediaBrowser Client=\"Jellyfin Android\"")]
    [InlineData(null)]
    public void VariesByAuthorizationWhetherOrNotTheButtonIsAdded(string? authorization)
    {
        var httpContext = new DefaultHttpContext();

        Filter(authorization, "Welcome home.", httpContext);

        Assert.Equal(["Authorization"], httpContext.Response.Headers.Vary.ToArray());
    }

    [Fact]
    public void KeepsAnExistingVary()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Headers.Vary = "Accept-Encoding";

        Filter(JellyfinWeb, null, httpContext);

        Assert.Equal(["Accept-Encoding", "Authorization"], httpContext.Response.Headers.Vary.ToArray());
    }

    [Theory]
    [InlineData(LocalLoginPolicy.Disabled, true)]
    [InlineData(LocalLoginPolicy.Disabled, false)]
    [InlineData(LocalLoginPolicy.LocalNetworkOnly, false)]
    public void HidesLocalLoginAndAddsTheHintBelowTheButtonWhenLocalLoginIsRefused(LocalLoginPolicy policy, bool local)
    {
        _configuration.LocalLoginPolicy = policy;
        _localNetwork.Local = local;

        var branding = FilterBranding(JellyfinWeb, "Welcome home.");

        Assert.Equal("Welcome home.\n\n" + Button + "\n\n" + Hint, branding.LoginDisclaimer);
        Assert.Equal("body{}\n" + HideLocalLoginCss, branding.CustomCss);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HintAndCssStandAloneWithoutAdminContent(string? adminContent)
    {
        _configuration.LocalLoginPolicy = LocalLoginPolicy.Disabled;
        _configuration.ShowSignInButton = false;

        var branding = FilterBranding(JellyfinWeb, adminContent, customCss: adminContent);

        Assert.Equal(Hint, branding.LoginDisclaimer);
        Assert.Equal(HideLocalLoginCss, branding.CustomCss);
    }

    [Theory]
    [InlineData(LocalLoginPolicy.Allowed, false)]
    [InlineData(LocalLoginPolicy.LocalNetworkOnly, true)]
    public void LeavesTheCssAndButtonAloneWhenLocalLoginIsAccepted(LocalLoginPolicy policy, bool local)
    {
        _configuration.LocalLoginPolicy = policy;
        _localNetwork.Local = local;

        var branding = FilterBranding(JellyfinWeb, "Welcome home.");

        Assert.Equal("Welcome home.\n\n" + Button, branding.LoginDisclaimer);
        Assert.Equal("body{}", branding.CustomCss);
    }

    [Theory]
    [InlineData("MediaBrowser Client=\"Jellyfin Android\", Device=\"Pixel\"")]
    [InlineData(null)]
    public void LeavesTheBrandingAloneForOtherClientsWhenLocalLoginIsRefused(string? authorization)
    {
        _configuration.LocalLoginPolicy = LocalLoginPolicy.Disabled;

        var branding = FilterBranding(authorization, "Welcome home.");

        Assert.Equal("Welcome home.", branding.LoginDisclaimer);
        Assert.Equal("body{}", branding.CustomCss);
    }

    [Fact]
    public void LeavesTheBrandingAloneUntilThePluginIsFullyConfiguredWhateverThePolicy()
    {
        _configuration.LocalLoginPolicy = LocalLoginPolicy.Disabled;
        _configuration.ClientSecret = string.Empty;

        var branding = FilterBranding(JellyfinWeb, "Welcome home.");

        Assert.Equal("Welcome home.", branding.LoginDisclaimer);
        Assert.Equal("body{}", branding.CustomCss);
    }

    [Fact]
    public void IgnoresOtherResults()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = JellyfinWeb;
        var result = new ObjectResult("Welcome home.");
        var context = new ResultExecutingContext(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>(), result, controller: new object());

        new BrandingFilter(() => _configuration, new LocalLoginPolicyCheck(() => _configuration, _localNetwork)).OnResultExecuting(context);

        Assert.Equal("Welcome home.", Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(0, httpContext.Response.Headers.Vary.Count);
    }

    private string? Filter(string? authorization, string? loginDisclaimer, HttpContext? httpContext = null)
    {
        var branding = FilterBranding(authorization, loginDisclaimer, httpContext);
        Assert.Equal("body{}", branding.CustomCss);
        return branding.LoginDisclaimer;
    }

    private BrandingOptionsDto FilterBranding(string? authorization, string? loginDisclaimer, HttpContext? httpContext = null, string? customCss = "body{}")
    {
        httpContext ??= new DefaultHttpContext();
        if (authorization is not null)
        {
            httpContext.Request.Headers.Authorization = authorization;
        }

        var result = new ObjectResult(new BrandingOptionsDto { LoginDisclaimer = loginDisclaimer, CustomCss = customCss, SplashscreenEnabled = true });
        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            result,
            controller: new object());

        new BrandingFilter(() => _configuration, new LocalLoginPolicyCheck(() => _configuration, _localNetwork)).OnResultExecuting(context);

        var dto = Assert.IsType<BrandingOptionsDto>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.True(dto.SplashscreenEnabled);
        return dto;
    }
}
