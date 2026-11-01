using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The outcome of Start: a redirect to the Identity Provider plus the flow cookie, a redirect to Start on the Public
/// Jellyfin URL's host, or a failure.
/// </summary>
public sealed class StartResult
{
    private StartResult(Uri? authorizationUrl, IssuedCookie? flowCookie, Uri? publicHostUrl, LoginFailure? failure)
    {
        AuthorizationUrl = authorizationUrl;
        FlowCookie = flowCookie;
        PublicHostUrl = publicHostUrl;
        Failure = failure;
    }

    /// <summary>
    /// Gets the Identity Provider URL to redirect the browser to, unless Start failed or redirects to the public host.
    /// </summary>
    public Uri? AuthorizationUrl { get; }

    /// <summary>
    /// Gets the flow cookie to set alongside the redirect to the Identity Provider.
    /// </summary>
    public IssuedCookie? FlowCookie { get; }

    /// <summary>
    /// Gets Start on the Public Jellyfin URL's host, when the request came to another host: the flow cookie must be
    /// set on the host the Identity Provider returns to.
    /// </summary>
    public Uri? PublicHostUrl { get; }

    /// <summary>
    /// Gets why Start failed, or <c>null</c> when it succeeded.
    /// </summary>
    public LoginFailure? Failure { get; }

    internal static StartResult Redirect(Uri authorizationUrl, IssuedCookie flowCookie) => new(authorizationUrl, flowCookie, null, null);

    internal static StartResult RedirectToPublicHost(Uri publicHostUrl) => new(null, null, publicHostUrl, null);

    internal static StartResult Fail(LoginFailure failure) => new(null, null, null, failure);
}
