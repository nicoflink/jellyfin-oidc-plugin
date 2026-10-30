using System;
using Jellyfin.Plugin.OidcLogin.Login;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OidcLogin;

/// <summary>
/// Registers the Plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<IdentityProviderClient>();
        serviceCollection.AddSingleton<FlowCookieProtector>();
        serviceCollection.AddSingleton(services => new OidcLoginService(
            services.GetRequiredService<IdentityProviderClient>(),
            services.GetRequiredService<FlowCookieProtector>(),
            services.GetRequiredService<TimeProvider>(),
            () => Plugin.Instance!.Configuration,
            services.GetRequiredService<ILogger<OidcLoginService>>()));
    }
}
