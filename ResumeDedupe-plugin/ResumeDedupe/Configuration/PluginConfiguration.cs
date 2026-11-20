using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ResumeDedupe.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Continue Watching: limita episodios por serie.</summary>
    public bool DedupeContinueWatching { get; set; } = true;

    /// <summary>Next Up: esconde series que ja aparecem em Continue Watching.</summary>
    public bool HideNextUpInContinueWatching { get; set; } = true;

    /// <summary>Quantos episodios por serie manter em Continue Watching (minimo 1).</summary>
    public int MaxEpisodesPerSeries { get; set; } = 1;
}
