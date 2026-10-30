namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The categories an OIDC Login failure is shown as.
/// </summary>
public enum FailureCategory
{
    /// <summary>
    /// The Identity Provider couldn't be reached.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The login can't work, for example because the Plugin isn't configured.
    /// </summary>
    Failed,
}
