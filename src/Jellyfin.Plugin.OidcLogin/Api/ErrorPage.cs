using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OidcLogin.Login;

namespace Jellyfin.Plugin.OidcLogin.Api;

/// <summary>
/// The page every OIDC Login failure ends on: a short message for its category, its reference code, and the way back.
/// It never shows technical detail; that is only in the reference code's log entry.
/// </summary>
public static partial class ErrorPage
{
    private static readonly string Template = ReadTemplate();

    /// <summary>
    /// Gets the message shown for <paramref name="category"/>.
    /// </summary>
    /// <param name="category">The failure category.</param>
    /// <returns>The message.</returns>
    public static string Message(FailureCategory category) => category switch
    {
        FailureCategory.Cancelled => "Sign-in was cancelled.",
        FailureCategory.Expired => "Your sign-in has expired. Please try again.",
        FailureCategory.NotSetUp => "Your account isn't set up for this Jellyfin server. Please ask its administrator.",
        FailureCategory.Disabled => "Your Jellyfin account is disabled.",
        FailureCategory.PasskeyRequired => "Please sign in with a passkey.",
        FailureCategory.Unavailable => "The sign-in service is unavailable right now. Please try again later.",
        _ => "Sign-in failed.",
    };

    /// <summary>
    /// Renders the page for <paramref name="category"/>. A cancelled login offers only "Back to login".
    /// </summary>
    /// <param name="category">The failure category.</param>
    /// <param name="reference">The reference code; left out unless it has the reference code format.</param>
    /// <returns>The page's HTML.</returns>
    public static string Render(FailureCategory category, string? reference)
    {
        // Links are relative to /OidcLogin/, so they hold under any base path.
        var actions = new StringBuilder();
        if (category != FailureCategory.Cancelled)
        {
            actions.AppendLine("        <a class=\"primary\" href=\"Start\">Try again</a>");
        }

        actions.Append("        <a href=\"../web/#/login\">Back to login</a>");

        var referenceLine = reference is not null && ReferenceFormat().IsMatch(reference)
            ? $"    <p>Reference code: <code id=\"reference\">{reference}</code></p>"
            : string.Empty;

        return Template
            .Replace("{{message}}", WebUtility.HtmlEncode(Message(category)), StringComparison.Ordinal)
            .Replace("{{reference}}", referenceLine, StringComparison.Ordinal)
            .Replace("{{actions}}", actions.ToString(), StringComparison.Ordinal);
    }

    [GeneratedRegex("^[0-9A-Z]{8}$")]
    private static partial Regex ReferenceFormat();

    private static string ReadTemplate()
    {
        using var stream = typeof(ErrorPage).Assembly.GetManifestResourceStream("Jellyfin.Plugin.OidcLogin.Web.Error.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
