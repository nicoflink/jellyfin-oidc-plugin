using System;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.OidcLogin.Api;

/// <summary>
/// Sends the security headers every Plugin page and endpoint answers with: a CSP that allows only the Plugin's own
/// scripts and inline styles and no framing, no caching, and no referrer.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class PluginPageHeadersAttribute : ResultFilterAttribute
{
    /// <summary>
    /// The Content Security Policy of the Plugin's pages.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <inheritdoc />
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.HttpContext.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
    }
}
