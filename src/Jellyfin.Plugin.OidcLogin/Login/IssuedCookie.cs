using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// A cookie to set on the response.
/// </summary>
/// <param name="Name">The cookie name.</param>
/// <param name="Value">The cookie value.</param>
/// <param name="Options">The cookie attributes.</param>
public sealed record IssuedCookie(string Name, string Value, CookieOptions Options);
