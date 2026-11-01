namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The query the Identity Provider sends the browser back to <c>/OidcLogin/Callback</c> with.
/// </summary>
/// <param name="Code">The authorization code.</param>
/// <param name="State">The <c>state</c> echoed from Start.</param>
/// <param name="Error">The <c>error</c> the Identity Provider answered with instead of a code.</param>
/// <param name="ErrorDescription">The <c>error_description</c> that came with <paramref name="Error"/>.</param>
/// <param name="Iss">The RFC 9207 <c>iss</c> naming the Identity Provider that answered, when it sends one.</param>
public sealed record CallbackQuery(string? Code, string? State, string? Error = null, string? ErrorDescription = null, string? Iss = null);
