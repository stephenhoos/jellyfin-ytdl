using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlEpisodeLocalProvider : ILocalMetadataProvider<Episode>
{
    public string Name => Constants.PluginName;

    public async Task<MetadataResult<Episode>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadVideoForMediaAsync(info.Path, cancellationToken);
        if (metadata is null)
        {
            return new MetadataResult<Episode>();
        }

        var episode = new Episode
        {
            Name = metadata.Title,
            Overview = metadata.Description,
            PremiereDate = metadata.UploadDate?.UtcDateTime,
            ProductionYear = metadata.UploadDate?.Year,
            RunTimeTicks = metadata.Runtime?.Ticks,
            ForcedSortName = metadata.UploadDate is null
                ? metadata.Title
                : $"{metadata.UploadDate:yyyyMMdd}-{metadata.Title}"
        };

        if (!string.IsNullOrWhiteSpace(metadata.Id))
        {
            episode.ProviderIds[Constants.YouTubeProviderKey] = metadata.Id;
        }

        return new MetadataResult<Episode>
        {
            HasMetadata = true,
            Item = episode
        };
    }
}
