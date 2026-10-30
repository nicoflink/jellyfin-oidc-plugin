// PROTOTYPE — throwaway. Hosts configPage.html so its variants render inside a real Jellyfin dashboard.
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace OidcLoginProto;

public class Plugin : BasePlugin<BasePluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "OIDC Login (prototype)";

    public override Guid Id => Guid.Parse("5b0f3c2e-7f1a-4c9e-9a51-0d6f2a1b7e00");

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo { Name = "OidcLogin", EmbeddedResourcePath = "OidcLoginProto.configPage.html" }
    ];
}
