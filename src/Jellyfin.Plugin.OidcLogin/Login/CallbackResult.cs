using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The outcome of Callback: a redirect to Complete carrying a handoff code, or a failure.
/// </summary>
public sealed class CallbackResult
{
    private CallbackResult(Uri? completeUrl, LoginFailure? failure)
    {
        CompleteUrl = completeUrl;
        Failure = failure;
    }

    /// <summary>
    /// Gets the <c>/OidcLogin/Complete</c> URL to redirect the browser to, with the handoff code in its fragment,
    /// unless Callback failed.
    /// </summary>
    public Uri? CompleteUrl { get; }

    /// <summary>
    /// Gets the handoff code in <see cref="CompleteUrl"/>, unless Callback failed.
    /// </summary>
    public string? HandoffCode => CompleteUrl?.Fragment["#code=".Length..];

    /// <summary>
    /// Gets why Callback failed, or <c>null</c> when it succeeded.
    /// </summary>
    public LoginFailure? Failure { get; }

    internal static CallbackResult Handoff(Uri completeUrl) => new(completeUrl, null);

    internal static CallbackResult Fail(LoginFailure failure) => new(null, failure);
}
