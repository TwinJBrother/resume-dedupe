using Jellyfin.Plugin.ResumeDedupe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ResumeDedupe;

/// <summary>
/// Plugin 100% server-side: nao altera a interface web nem a imagem base.
/// Funciona para qualquer cliente que use a API padrao do Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Resume Dedupe";

    public override Guid Id => Guid.Parse("7c1d9f4e-2b6a-4e0f-9a53-3d8b1c5e7a21");

    public override string Description =>
        "Deduplica Continuar Assistindo e Next Up (um episodio por serie).";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
