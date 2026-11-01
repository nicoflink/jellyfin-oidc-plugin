using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Reads the claims of a validated ID token the login flow acts on.
/// </summary>
internal static class IdTokenClaims
{
    /// <summary>
    /// Gets the username claim, or <c>null</c> when it is absent, blank or not a string.
    /// </summary>
    /// <param name="idToken">The validated ID token.</param>
    /// <param name="configuration">The Plugin configuration, which names the claim.</param>
    /// <returns>The username claim.</returns>
    public static string? Username(JsonWebToken idToken, PluginConfiguration configuration)
        => idToken.TryGetPayloadValue<string>(configuration.UsernameClaim, out var username) && !string.IsNullOrWhiteSpace(username) ? username : null;

    /// <summary>
    /// Reads a claim holding names, such as the groups claim or <c>amr</c>: a string array, or a single string.
    /// </summary>
    /// <param name="idToken">The validated ID token.</param>
    /// <param name="claim">The claim's name.</param>
    /// <param name="values">The names; non-string array elements are left out.</param>
    /// <returns><c>false</c> when the claim is absent or holds something else.</returns>
    public static bool TryGetStrings(JsonWebToken idToken, string claim, [NotNullWhen(true)] out IReadOnlyCollection<string>? values)
    {
        values = null;
        if (!idToken.TryGetPayloadValue<JsonElement>(claim, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            values = [value.GetString()!];
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            values = value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
        }

        return values is not null;
    }

    /// <summary>
    /// A failure for the person the ID token is about, carrying its Subject and username claim for the log entry.
    /// </summary>
    /// <param name="idToken">The validated ID token.</param>
    /// <param name="configuration">The Plugin configuration, which names the username claim.</param>
    /// <param name="category">The failure category.</param>
    /// <param name="cause">The admin-facing cause.</param>
    /// <returns>The failure.</returns>
    public static LoginFailure Failure(JsonWebToken idToken, PluginConfiguration configuration, FailureCategory category, string cause)
        => new(category, cause)
        {
            Subject = string.IsNullOrEmpty(idToken.Subject) ? null : idToken.Subject,
            Username = Username(idToken, configuration),
        };
}
