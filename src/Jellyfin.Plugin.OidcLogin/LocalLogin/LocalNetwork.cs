using System.Net;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Classifies requests as local the way Jellyfin's own local-network rules do.
/// </summary>
public sealed class LocalNetwork : ILocalNetwork
{
    private readonly INetworkManager _networkManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalNetwork"/> class.
    /// </summary>
    /// <param name="networkManager">Jellyfin's network manager, which knows its local networks.</param>
    public LocalNetwork(INetworkManager networkManager)
    {
        _networkManager = networkManager;
    }

    /// <summary>
    /// A request is local when its normalised remote address, which honours Known Proxies, is loopback or inside
    /// Jellyfin's configured local network.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether the request is local.</returns>
    public bool IsLocal(HttpContext context)
    {
        var remoteAddress = context.GetNormalizedRemoteIP();
        return IPAddress.IsLoopback(remoteAddress) || _networkManager.IsInLocalNetwork(remoteAddress);
    }
}
