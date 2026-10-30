namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Why an OIDC Login didn't go ahead.
/// </summary>
/// <param name="Category">The category shown to the person.</param>
/// <param name="Cause">The admin-facing cause; never holds tokens, codes, secrets or cookies.</param>
public sealed record LoginFailure(FailureCategory Category, string Cause);
