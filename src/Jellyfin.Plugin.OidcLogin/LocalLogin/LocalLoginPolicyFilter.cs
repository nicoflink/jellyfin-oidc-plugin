using System;
using System.Net;
using System.Net.Mime;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Enforces the Local Login Policy: refuses Jellyfin's Local Login actions with <c>403</c>. It is a resource filter,
/// so it runs before model binding reads or validates the credentials in the body.
/// </summary>
public sealed partial class LocalLoginPolicyFilter : IResourceFilter
{
    private readonly LocalLoginPolicyCheck _policyCheck;
    private readonly ILogger<LocalLoginPolicyFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalLoginPolicyFilter"/> class.
    /// </summary>
    /// <param name="policyCheck">Decides whether the Local Login Policy refuses a request.</param>
    /// <param name="logger">The logger.</param>
    public LocalLoginPolicyFilter(LocalLoginPolicyCheck policyCheck, ILogger<LocalLoginPolicyFilter> logger)
    {
        _policyCheck = policyCheck;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsLocalLoginAction(context.ActionDescriptor) || _policyCheck.RefusedBy(context.HttpContext) is not { } policy)
        {
            return;
        }

        // Nothing from the request but its address: the body and query carry the credentials.
        LogRefused(context.HttpContext.GetNormalizedRemoteIP(), policy);

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
    /// Jellyfin's two Local Login actions. The legacy one calls the other as a plain method, not through MVC, so both
    /// are matched.
    /// </summary>
    private static bool IsLocalLoginAction(ActionDescriptor action)
        => action is ControllerActionDescriptor { ControllerName: "User", ActionName: "AuthenticateUserByName" or "AuthenticateUser" };

    [LoggerMessage(Level = LogLevel.Information, Message = "Local Login from {RemoteAddress} refused by the Local Login Policy {Policy}")]
    private partial void LogRefused(IPAddress remoteAddress, LocalLoginPolicy policy);
}
