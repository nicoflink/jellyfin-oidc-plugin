using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// A Jellyfin session, as jellyfin-web keeps it in <c>localStorage.jellyfin_credentials</c>.
/// </summary>
/// <param name="AccessToken">The session's access token.</param>
/// <param name="UserId">The signed-in Jellyfin user.</param>
/// <param name="ServerId">The Jellyfin server's id.</param>
public sealed record JellyfinSession(string AccessToken, Guid UserId, string ServerId);
