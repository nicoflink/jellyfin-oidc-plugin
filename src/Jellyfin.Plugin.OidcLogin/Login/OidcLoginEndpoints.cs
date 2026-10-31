using Jellyfin.Plugin.OidcLogin.Configuration;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The absolute URLs of the Plugin's <c>/OidcLogin/</c> endpoints, always on the Public Jellyfin URL and never
/// derived from a request.
/// </summary>
internal static class OidcLoginEndpoints
{
    /// <summary>
    /// Gets the URL of <paramref name="endpoint"/> under the Public Jellyfin URL.
    /// </summary>
    /// <param name="configuration">The Plugin configuration.</param>
    /// <param name="endpoint">The endpoint, such as <c>Callback</c>, with any query or fragment.</param>
    /// <returns>The absolute URL.</returns>
    public static string Url(PluginConfiguration configuration, string endpoint)
        => configuration.PublicJellyfinUrl.TrimEnd('/') + "/OidcLogin/" + endpoint;
}
