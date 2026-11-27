using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ResumeDedupe;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Uma unica instancia (singleton) em vez de uma por pedido de qualquer endpoint.
        services.AddSingleton<ResumeDedupeFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ResumeDedupeFilter>());
    }
}
