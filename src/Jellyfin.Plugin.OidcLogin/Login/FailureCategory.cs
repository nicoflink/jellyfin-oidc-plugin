namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The categories an OIDC Login failure is shown as.
/// </summary>
public enum FailureCategory
{
    /// <summary>
    /// The login is no longer valid: the flow cookie is missing, invalid or expired, or <c>state</c> doesn't match.
    /// </summary>
    Expired,

    /// <summary>
    /// The Identity Provider couldn't be reached.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The login can't work, for example because the Plugin isn't configured.
    /// </summary>
    Failed,
}
