using System;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Jellyfin.Plugin.OidcLogin.LoginEntryPoint;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
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
        serviceCollection.AddSingleton<ConnectionTester>();
        serviceCollection.AddSingleton<FlowCookieProtector>();
        serviceCollection.AddSingleton<HandoffCodes>();

        // Resolved after the Plugin is loaded, so its data folder is known.
        serviceCollection.AddSingleton(_ => new LinkedIdentityStore(Plugin.Instance!.DataFolderPath));
        serviceCollection.AddSingleton(services => new LinkedIdentityAdmin(
            services.GetRequiredService<LinkedIdentityStore>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<TimeProvider>(),
            () => Plugin.Instance!.Configuration));
        serviceCollection.AddSingleton(services => new OidcLoginService(
            services.GetRequiredService<IdentityProviderClient>(),
            services.GetRequiredService<FlowCookieProtector>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<ISessionManager>(),
            services.GetRequiredService<LinkedIdentityStore>(),
            services.GetRequiredService<HandoffCodes>(),
            services.GetRequiredService<TimeProvider>(),
            () => Plugin.Instance!.Configuration,
            new JellyfinWebClient(applicationHost.ApplicationVersionString),
            services.GetRequiredService<ILogger<OidcLoginService>>()));

        // Runs for every MVC result; it acts only on the anonymous GET /Branding/Configuration.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add(new BrandingFilter(() => Plugin.Instance!.Configuration)));
    }
}
