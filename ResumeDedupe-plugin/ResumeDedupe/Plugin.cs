using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ResumeDedupe;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer xml)
    : BasePlugin<BasePluginConfiguration>(paths, xml)
{
    public override string Name => "Resume Dedupe";

    public override Guid Id => new("7c1d9f4e-2b6a-4e0f-9a53-3d8b1c5e7a21");

    public override string Description => "Um episodio por serie em Continuar Assistindo e Next Up.";
}
