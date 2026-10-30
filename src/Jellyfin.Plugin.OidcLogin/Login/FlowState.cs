using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What one OIDC Login carries from Start to Callback, inside the flow cookie.
/// </summary>
/// <param name="State">The <c>state</c> sent to the Identity Provider.</param>
/// <param name="Nonce">The <c>nonce</c> the ID token must echo.</param>
/// <param name="CodeVerifier">The PKCE code verifier.</param>
/// <param name="ExpiresAt">When this login stops being accepted.</param>
public sealed record FlowState(string State, string Nonce, string CodeVerifier, DateTimeOffset ExpiresAt);
