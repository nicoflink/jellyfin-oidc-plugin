using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OidcLogin.Configuration;

/// <summary>
/// OIDC Login plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the URL people use to reach Jellyfin, from which the callback URL and cookie security derive.
    /// </summary>
    public string PublicJellyfinUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Identity Provider's issuer URL; discovery is fetched only from here.
    /// </summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client id registered at the Identity Provider.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client secret registered at the Identity Provider.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the space-separated scopes requested at the Identity Provider.
    /// </summary>
    public string Scopes { get; set; } = "openid profile groups";

    /// <summary>
    /// Gets or sets the ID token claim that names a Jellyfin user at provisioning.
    /// </summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>
    /// Gets or sets the ID token claim holding the person's Identity Provider group names.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>
    /// Gets or sets the Identity Provider group whose members are Jellyfin administrators; empty turns Admin Group
    /// sync off.
    /// </summary>
    public string AdminGroup { get; set; } = string.Empty;
}
