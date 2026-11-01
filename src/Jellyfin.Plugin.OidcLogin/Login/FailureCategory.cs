namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The categories an OIDC Login failure is shown as.
/// </summary>
public enum FailureCategory
{
    /// <summary>
    /// The person cancelled at the Identity Provider (<c>access_denied</c>).
    /// </summary>
    Cancelled,

    /// <summary>
    /// The login is no longer valid: the flow cookie is missing, invalid or expired, <c>state</c> doesn't match, or
    /// the handoff code expired.
    /// </summary>
    Expired,

    /// <summary>
    /// The Identity Provider vouched for the person, but they have no Jellyfin user to sign in as.
    /// </summary>
    NotSetUp,

    /// <summary>
    /// The person's Jellyfin user may not sign in.
    /// </summary>
    Disabled,

    /// <summary>
    /// The person didn't sign in at the Identity Provider with a passkey, which is required.
    /// </summary>
    PasskeyRequired,

    /// <summary>
    /// The Identity Provider couldn't be reached.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The login can't work, for example because the Plugin isn't configured or the ID token is invalid.
    /// </summary>
    Failed,
}
