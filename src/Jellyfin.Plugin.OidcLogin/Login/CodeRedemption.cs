namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What the token endpoint needs to redeem an authorization code.
/// </summary>
/// <param name="Code">The authorization code.</param>
/// <param name="RedirectUri">The redirect URI the code was issued for.</param>
/// <param name="CodeVerifier">The PKCE code verifier.</param>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret.</param>
public sealed record CodeRedemption(string Code, string RedirectUri, string CodeVerifier, string ClientId, string ClientSecret);
