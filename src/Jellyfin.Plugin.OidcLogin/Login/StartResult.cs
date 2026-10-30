using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The outcome of Start: a redirect to the Identity Provider plus the flow cookie, or a failure.
/// </summary>
public sealed class StartResult
{
    private StartResult(Uri? authorizationUrl, IssuedCookie? flowCookie, LoginFailure? failure)
    {
        AuthorizationUrl = authorizationUrl;
        FlowCookie = flowCookie;
        Failure = failure;
    }

    /// <summary>
    /// Gets the Identity Provider URL to redirect the browser to, unless Start failed.
    /// </summary>
    public Uri? AuthorizationUrl { get; }

    /// <summary>
    /// Gets the flow cookie to set alongside the redirect, unless Start failed.
    /// </summary>
    public IssuedCookie? FlowCookie { get; }

    /// <summary>
    /// Gets why Start failed, or <c>null</c> when it succeeded.
    /// </summary>
    public LoginFailure? Failure { get; }

    internal static StartResult Redirect(Uri authorizationUrl, IssuedCookie flowCookie) => new(authorizationUrl, flowCookie, null);

    internal static StartResult Fail(LoginFailure failure) => new(null, null, failure);
}
