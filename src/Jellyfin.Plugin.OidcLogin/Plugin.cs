using System;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OidcLogin;

/// <summary>
/// The OIDC Login plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    private static readonly Guid PluginId = Guid.Parse("d34c4da5-d189-4677-b1a5-b1ea1d0f1dbd");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "OIDC Login";

    /// <inheritdoc />
    public override Guid Id => PluginId;
}
