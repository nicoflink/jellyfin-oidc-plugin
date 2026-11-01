using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What jellyfin-web says about itself when it signs in: its app name, version and device name. Sessions the
/// Plugin creates carry the same, so they look like the browser's own Local Login.
/// </summary>
public sealed class JellyfinWebClient
{
    /// <summary>
    /// jellyfin-web's browser tokens in its order of precedence, with the device names it gives them.
    /// </summary>
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("edg", "Edge Chromium"),
        ("edga", "Edge Chromium"),
        ("edgios", "Edge Chromium"),
        ("edge", "Edge"),
        ("opera", "Opera"),
        ("opr", "Opera"),
        ("chrome", "Chrome"),
        ("safari", "Safari"),
        ("firefox", "Firefox"),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinWebClient"/> class.
    /// </summary>
    /// <param name="appVersion">The version jellyfin-web reports: the server's, since the server bundles it.</param>
    public JellyfinWebClient(string appVersion)
    {
        AppVersion = appVersion;
    }

    /// <summary>
    /// Gets the app name jellyfin-web reports.
    /// </summary>
    public static string App => "Jellyfin Web";

    /// <summary>
    /// Gets the app version jellyfin-web reports.
    /// </summary>
    public string AppVersion { get; }

    /// <summary>
    /// Names the device the way jellyfin-web's <c>getDeviceName</c> does for a desktop or mobile browser: the
    /// browser's name, plus " iPad", " iPhone" or " Android".
    /// </summary>
    /// <param name="userAgent">The browser's <c>User-Agent</c>.</param>
    /// <returns>The device name.</returns>
    public static string DeviceName(string? userAgent)
    {
        // jellyfin-web strips "motorola edge" so the phone isn't mistaken for the Edge browser.
        var ua = (userAgent ?? string.Empty).ToLowerInvariant().Replace("motorola edge", string.Empty, StringComparison.Ordinal);
        var (token, name) = Array.Find(Browsers, b => HasVersionedToken(ua, b.Token));
        name ??= ua.Contains("webkit", StringComparison.Ordinal) ? "Safari" : "Web Browser";

        // jellyfin-web gives legacy Edge no platform, and checks ipad, iphone, windows, android in that order.
        if (token == "edge")
        {
            return name;
        }

        if (ua.Contains("ipad", StringComparison.Ordinal))
        {
            return name + " iPad";
        }

        if (ua.Contains("iphone", StringComparison.Ordinal))
        {
            return name + " iPhone";
        }

        if (!ua.Contains("windows", StringComparison.Ordinal) && ua.Contains("android", StringComparison.Ordinal))
        {
            return name + " Android";
        }

        return name;
    }

    /// <summary>
    /// Whether <paramref name="ua"/> holds <paramref name="token"/> followed by a space or slash and a version,
    /// as jellyfin-web's <c>/(token)[ /]([\w.]+)/</c> requires.
    /// </summary>
    private static bool HasVersionedToken(string ua, string token)
    {
        for (var i = ua.IndexOf(token, StringComparison.Ordinal); i >= 0; i = ua.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            var separator = i + token.Length;
            if (separator + 1 < ua.Length
                && ua[separator] is ' ' or '/'
                && (char.IsAsciiLetterOrDigit(ua[separator + 1]) || ua[separator + 1] is '_' or '.'))
            {
                return true;
            }
        }

        return false;
    }
}
