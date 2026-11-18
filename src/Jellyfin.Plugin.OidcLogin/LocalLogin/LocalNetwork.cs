using System;
using System.Net;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Classifies requests as local the way Jellyfin's own local-network rules do.
/// </summary>
internal static class LocalNetwork
{
    /// <summary>
    /// A request is local when its normalised remote address, which honours Known Proxies, is loopback or inside
    /// Jellyfin's configured local network.
    /// </summary>
    /// <param name="networkManager">Jellyfin's network manager, which knows its local networks.</param>
    /// <returns>Whether a request is local.</returns>
    public static Func<HttpContext, bool> Classifier(INetworkManager networkManager) => context =>
    {
        var remoteAddress = context.GetNormalizedRemoteIP();
        return IPAddress.IsLoopback(remoteAddress) || networkManager.IsInLocalNetwork(remoteAddress);
    };
}
