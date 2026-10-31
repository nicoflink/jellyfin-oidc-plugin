using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OidcLogin;

/// <summary>
/// The OIDC Login plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
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
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded Plugin, through which services read the current configuration.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "OIDC Login";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Saves <paramref name="configuration"/>, keeping the stored client secret when it comes without one: the
    /// secret is never sent out, so a configuration posted back without it means "unchanged".
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming && string.IsNullOrEmpty(incoming.ClientSecret))
        {
            incoming.ClientSecret = Configuration.ClientSecret;
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "OidcLogin",
            DisplayName = "OIDC Login",
            EmbeddedResourcePath = "Jellyfin.Plugin.OidcLogin.Web.ConfigPage.html",
        },
    ];
}
