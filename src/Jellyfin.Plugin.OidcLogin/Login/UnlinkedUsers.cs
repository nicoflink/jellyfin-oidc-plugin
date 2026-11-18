namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The enabled Jellyfin users without a Linked Identity: those who can't sign in once Local Login is refused.
/// </summary>
/// <param name="Count">How many there are.</param>
/// <param name="Administrators">How many of them are administrators.</param>
public sealed record UnlinkedUsers(int Count, int Administrators);
