using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.OidcLogin.Configuration;

/// <summary>
/// Where Local Login is accepted. It never restricts OIDC Login or Quick Connect.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalLoginPolicy>))]
public enum LocalLoginPolicy
{
    /// <summary>
    /// Local Login works everywhere.
    /// </summary>
    Allowed,

    /// <summary>
    /// Local Login works only for requests Jellyfin classifies as coming from its local network.
    /// </summary>
    LocalNetworkOnly,

    /// <summary>
    /// Local Login is refused everywhere.
    /// </summary>
    Disabled,
}
