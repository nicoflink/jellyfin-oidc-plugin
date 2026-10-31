using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// A Linked Identity as the configuration page lists it.
/// </summary>
/// <param name="Issuer">The Identity Provider's issuer.</param>
/// <param name="Subject">The <c>sub</c> claim.</param>
/// <param name="UserId">The Jellyfin user's id.</param>
/// <param name="UserName">The Jellyfin user's name, or <c>null</c> if the user no longer exists.</param>
/// <param name="LinkedAt">When the link was made.</param>
/// <param name="LastLoginAt">When the Subject last signed in, or <c>null</c> if it hasn't through this link.</param>
public sealed record LinkedIdentityEntry(string Issuer, string Subject, Guid UserId, string? UserName, DateTimeOffset LinkedAt, DateTimeOffset? LastLoginAt);
