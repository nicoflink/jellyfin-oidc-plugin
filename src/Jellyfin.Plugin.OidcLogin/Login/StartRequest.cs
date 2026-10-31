namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What Start needs to know about the browser's request.
/// </summary>
/// <param name="Host">The request's <c>Host</c>, with its port if any; <c>null</c> when it is already known to be the Public Jellyfin URL's host.</param>
/// <param name="RedirectedToPublicHost">Whether Start already sent this browser to the Public Jellyfin URL's host once.</param>
public sealed record StartRequest(string? Host, bool RedirectedToPublicHost = false);
