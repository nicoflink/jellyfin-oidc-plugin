using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Whether a request comes from Jellyfin's local network.
/// </summary>
public interface ILocalNetwork
{
    /// <summary>
    /// Whether <paramref name="context"/> comes from Jellyfin's local network.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether the request is local.</returns>
    bool IsLocal(HttpContext context);
}
