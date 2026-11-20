using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ResumeDedupe;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singleton + AddService: uma unica instancia. Filters.Add<T>() criaria o filtro
        // (com 3 resolucoes de DI) a cada pedido de QUALQUER endpoint da API.
        serviceCollection.AddSingleton<ResumeDedupeFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ResumeDedupeFilter>());
    }
}
