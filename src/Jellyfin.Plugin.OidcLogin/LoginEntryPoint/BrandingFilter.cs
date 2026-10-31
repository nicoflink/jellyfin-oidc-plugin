using System;
using System.Net;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.OidcLogin.LoginEntryPoint;

/// <summary>
/// The Login Entry Point: appends a "Sign in with PocketID" button to the login disclaimer that the anonymous
/// <c>GET /Branding/Configuration</c> returns, at response time. Nothing is written to the Branding config, so the
/// admin's text stays theirs and uninstalling the Plugin leaves nothing behind.
/// </summary>
/// <remarks>
/// jellyfin-web renders the disclaimer as Markdown with HTML and forces every <c>&lt;a&gt;</c> into a new tab, so the
/// button is a <c>&lt;form&gt;</c>, which starts the OIDC Login in the same tab. It carries only Plugin-owned class
/// names and inline styles, so it doesn't depend on jellyfin-web's CSS.
/// </remarks>
public sealed class BrandingFilter : IResultFilter
{
    /// <summary>
    /// The only client that gets the button: wrapper apps (Android, desktop) name themselves and render the disclaimer
    /// too, but can't finish an OIDC Login.
    /// </summary>
    private const string WebClient = "Jellyfin Web";

    private const string ButtonStyle =
        "display:block;box-sizing:border-box;width:100%;margin:1.25em 0 0;padding:0.9em 1em;border:0;border-radius:0.2em;"
        + "background:#00a4dc;color:#fff;font:inherit;font-weight:600;cursor:pointer";

    private readonly Func<PluginConfiguration> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrandingFilter"/> class.
    /// </summary>
    /// <param name="configuration">Reads the current plugin configuration.</param>
    public BrandingFilter(Func<PluginConfiguration> configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result is not ObjectResult { Value: BrandingOptionsDto branding })
        {
            return;
        }

        var response = context.HttpContext.Response;
        response.Headers.Append(HeaderNames.Vary, HeaderNames.Authorization);

        var configuration = _configuration();
        if (!configuration.ShowSignInButton
            || !OidcLoginService.IsConfigured(configuration)
            || !string.Equals(ClientName(context.HttpContext.Request.Headers.Authorization), WebClient, StringComparison.Ordinal))
        {
            return;
        }

        // The controller builds a fresh DTO per request, so changing it changes only this response.
        var button = Button(OidcLoginEndpoints.Url(configuration, "Start"));
        branding.LoginDisclaimer = string.IsNullOrEmpty(branding.LoginDisclaimer) ? button : branding.LoginDisclaimer + "\n\n" + button;
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    /// <summary>
    /// The button, on one line so Markdown reads it as one HTML block.
    /// </summary>
    private static string Button(string startUrl)
        => $"<form class=\"oidc-login-entry\" action=\"{WebUtility.HtmlEncode(startUrl)}\" method=\"get\">"
            + $"<button type=\"submit\" class=\"oidc-login-button\" style=\"{ButtonStyle}\">Sign in with PocketID</button></form>";

    /// <summary>
    /// The <c>Client</c> named in a Jellyfin <c>Authorization</c> header (<c>MediaBrowser Client="…", Device="…"</c>),
    /// URL-decoded the way Jellyfin reads it.
    /// </summary>
    private static string? ClientName(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
        {
            return null;
        }

        var parameters = authorization.AsSpan();
        var space = parameters.IndexOf(' ');
        if (space >= 0)
        {
            parameters = parameters[(space + 1)..];
        }

        foreach (var range in parameters.Split(','))
        {
            var parameter = parameters[range];
            var equals = parameter.IndexOf('=');
            if (equals > 0 && parameter[..equals].Trim().Equals("Client", StringComparison.Ordinal))
            {
                return WebUtility.UrlDecode(parameter[(equals + 1)..].Trim().Trim('"').ToString());
            }
        }

        return null;
    }
}
