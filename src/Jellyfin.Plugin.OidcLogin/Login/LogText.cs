using System;
using System.Text;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Makes text from outside the Plugin, such as an Identity Provider's <c>error_description</c>, safe to log.
/// </summary>
internal static class LogText
{
    /// <summary>
    /// Quotes <paramref name="value"/> as printable ASCII only, so it can't forge log lines or carry terminal escapes,
    /// cut to <paramref name="maxLength"/> characters.
    /// </summary>
    /// <param name="value">The untrusted text.</param>
    /// <param name="maxLength">The most characters of it to keep.</param>
    /// <returns>The quoted, sanitised text.</returns>
    public static string Quote(string value, int maxLength)
    {
        var kept = value.AsSpan(0, Math.Min(value.Length, maxLength));
        var builder = new StringBuilder(kept.Length + 5);
        builder.Append('\'');
        foreach (var c in kept)
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        builder.Append(value.Length > maxLength ? "...'" : "'");
        return builder.ToString();
    }
}
