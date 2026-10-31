using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The persisted, one-to-one association between a Subject (issuer plus <c>sub</c>) and a Jellyfin user.
/// </summary>
/// <param name="Issuer">The Identity Provider's issuer.</param>
/// <param name="Subject">The <c>sub</c> claim, unique within <paramref name="Issuer"/>.</param>
/// <param name="UserId">The Jellyfin user's id.</param>
/// <param name="LinkedAt">When the link was made.</param>
/// <param name="LastLoginAt">When the Subject last signed in through OIDC Login.</param>
public sealed record LinkedIdentity(string Issuer, string Subject, Guid UserId, DateTimeOffset LinkedAt, DateTimeOffset LastLoginAt);
