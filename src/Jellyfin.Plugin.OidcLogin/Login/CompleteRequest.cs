namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The Complete page's POST, as the browser sent it.
/// </summary>
/// <param name="Origin">The <c>Origin</c> header.</param>
/// <param name="ContentType">The <c>Content-Type</c> header.</param>
/// <param name="Body">The request body: JSON with the handoff <c>code</c> and the <c>deviceId</c>.</param>
/// <param name="FlowCookie">The flow cookie value, if the browser sent one.</param>
/// <param name="UserAgent">The <c>User-Agent</c> header, from which the device name derives.</param>
/// <param name="RemoteEndPoint">The client's address, recorded with the session.</param>
/// <param name="FromLocalNetwork">Whether Jellyfin counts the client's address as in its local network; when unknown, it is remote.</param>
public sealed record CompleteRequest(string? Origin, string? ContentType, string? Body, string? FlowCookie, string? UserAgent, string? RemoteEndPoint, bool FromLocalNetwork = false);
