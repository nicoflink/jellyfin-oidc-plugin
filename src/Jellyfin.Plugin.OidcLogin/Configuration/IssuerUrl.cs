using System;

namespace Jellyfin.Plugin.OidcLogin.Configuration;

/// <summary>
/// The rules for an Issuer URL, shared by the login flow and Test connection.
/// </summary>
internal static class IssuerUrl
{
    /// <summary>
    /// Whether <paramref name="issuer"/> may be used: https, or plain http only on a loopback host.
    /// </summary>
    /// <param name="issuer">The Issuer URL, already known to be an absolute http or https URL.</param>
    /// <returns><c>true</c> when the Plugin may talk to it.</returns>
    public static bool IsAllowed(Uri issuer) => issuer.Scheme == Uri.UriSchemeHttps || issuer.IsLoopback;

    /// <summary>
    /// Gets the OpenID Connect discovery document's URL for <paramref name="issuer"/>.
    /// </summary>
    /// <param name="issuer">The Issuer URL.</param>
    /// <returns>The discovery URL.</returns>
    public static Uri Discovery(Uri issuer) => new(issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration");
}
