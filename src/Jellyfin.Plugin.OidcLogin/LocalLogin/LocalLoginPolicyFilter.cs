using System;
using System.Net;
using System.Net.Mime;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Enforces the Local Login Policy: refuses Jellyfin's password login actions with <c>403</c> before the action runs.
/// </summary>
public sealed partial class LocalLoginPolicyFilter : IResourceFilter
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Func<HttpContext, bool> _isLocal;
    private readonly ILogger<LocalLoginPolicyFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalLoginPolicyFilter"/> class.
    /// </summary>
    /// <param name="configuration">Reads the current plugin configuration.</param>
    /// <param name="isLocal">Whether Jellyfin classifies a request as coming from its local network.</param>
    /// <param name="logger">The logger.</param>
    public LocalLoginPolicyFilter(Func<PluginConfiguration> configuration, Func<HttpContext, bool> isLocal, ILogger<LocalLoginPolicyFilter> logger)
    {
        _configuration = configuration;
        _isLocal = isLocal;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = _configuration();
        if (!IsPasswordLogin(context.ActionDescriptor)
            || !Refuses(configuration, () => _isLocal(context.HttpContext)))
        {
            return;
        }

        // Nothing from the request but its address: the body and query carry the credentials.
        LogRefused(context.HttpContext.GetNormalizedRemoteIP(), configuration.LocalLoginPolicy);

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentType = MediaTypeNames.Text.Plain + "; charset=utf-8",
            Content = "Password sign-in is disabled on this server.",
        };
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    /// <summary>
    /// Whether the Local Login Policy refuses Local Login for a request: never until the Plugin is fully configured,
    /// whether or not the Identity Provider is reachable.
    /// </summary>
    /// <param name="configuration">The current plugin configuration.</param>
    /// <param name="isLocal">Whether the request comes from Jellyfin's local network; asked only when it matters.</param>
    /// <returns>Whether Local Login is refused.</returns>
    internal static bool Refuses(PluginConfiguration configuration, Func<bool> isLocal)
        => OidcLoginService.IsConfigured(configuration) && configuration.LocalLoginPolicy switch
        {
            LocalLoginPolicy.Disabled => true,
            LocalLoginPolicy.LocalNetworkOnly => !isLocal(),
            _ => false,
        };

    /// <summary>
    /// Jellyfin's two password login actions. The legacy one calls the other as a plain method, not through MVC, so
    /// both are matched.
    /// </summary>
    private static bool IsPasswordLogin(ActionDescriptor action)
        => action is ControllerActionDescriptor { ControllerName: "User", ActionName: "AuthenticateUserByName" or "AuthenticateUser" };

    [LoggerMessage(Level = LogLevel.Information, Message = "Local Login from {RemoteAddress} refused by the Local Login Policy {Policy}")]
    private partial void LogRefused(IPAddress remoteAddress, LocalLoginPolicy policy);
}
