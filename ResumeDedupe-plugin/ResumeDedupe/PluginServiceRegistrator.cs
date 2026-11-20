using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ResumeDedupe;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Filtro MVC global. Os plugins sao registrados antes do AddMvc do servidor,
        // entao Configure<MvcOptions> se combina com as opcoes do Jellyfin.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<ResumeDedupeFilter>());
    }
}
