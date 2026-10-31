namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The query the Identity Provider sends the browser back to <c>/OidcLogin/Callback</c> with.
/// </summary>
/// <param name="Code">The authorization code.</param>
/// <param name="State">The <c>state</c> echoed from Start.</param>
public sealed record CallbackQuery(string? Code, string? State);
