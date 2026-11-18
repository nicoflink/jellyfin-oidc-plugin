using System;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.LocalLogin;

/// <summary>
/// Decides whether the Local Login Policy refuses Local Login for a request. Both the refusal itself and the login
/// page's adaptation ask here, so they always agree.
/// </summary>
public sealed class LocalLoginPolicyCheck
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILocalNetwork _localNetwork;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalLoginPolicyCheck"/> class.
    /// </summary>
    /// <param name="configuration">Reads the current plugin configuration.</param>
    /// <param name="localNetwork">Classifies requests as local the way Jellyfin does.</param>
    public LocalLoginPolicyCheck(Func<PluginConfiguration> configuration, ILocalNetwork localNetwork)
    {
        _configuration = configuration;
        _localNetwork = localNetwork;
    }

    /// <summary>
    /// The policy that refuses Local Login for <paramref name="context"/>: never one until the Plugin is fully
    /// configured, whether or not the Identity Provider is reachable.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The refusing policy, or <c>null</c> when Local Login is accepted.</returns>
    public LocalLoginPolicy? RefusedBy(HttpContext context)
    {
        var configuration = _configuration();
        var policy = configuration.LocalLoginPolicy;
        var refused = OidcLoginService.IsConfigured(configuration) && policy switch
        {
            LocalLoginPolicy.Disabled => true,
            LocalLoginPolicy.LocalNetworkOnly => !_localNetwork.IsLocal(context),
            _ => false,
        };
        return refused ? policy : null;
    }
}
