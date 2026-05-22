using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YtdlArchive;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<YtdlpManager>();
        serviceCollection.AddSingleton<LibraryReconciler>();
        serviceCollection.AddHostedService<LibraryBootstrapHostedService>();
        serviceCollection.AddHostedService<DownloaderHostedService>();
    }
}
