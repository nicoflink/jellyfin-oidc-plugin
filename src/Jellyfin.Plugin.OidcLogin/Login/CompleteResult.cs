namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The outcome of Complete: a Jellyfin session plus the cleared flow cookie, or a failure.
/// </summary>
public sealed class CompleteResult
{
    private CompleteResult(JellyfinSession? session, IssuedCookie? clearedFlowCookie, LoginFailure? failure)
    {
        Session = session;
        ClearedFlowCookie = clearedFlowCookie;
        Failure = failure;
    }

    /// <summary>
    /// Gets the Jellyfin session the page hands to jellyfin-web, unless Complete failed.
    /// </summary>
    public JellyfinSession? Session { get; }

    /// <summary>
    /// Gets the expired flow cookie that removes the browser's one, unless Complete failed.
    /// </summary>
    public IssuedCookie? ClearedFlowCookie { get; }

    /// <summary>
    /// Gets why Complete failed, or <c>null</c> when it succeeded.
    /// </summary>
    public LoginFailure? Failure { get; }

    internal static CompleteResult SignedIn(JellyfinSession session, IssuedCookie clearedFlowCookie) => new(session, clearedFlowCookie, null);

    internal static CompleteResult Fail(LoginFailure failure) => new(null, null, failure);
}
