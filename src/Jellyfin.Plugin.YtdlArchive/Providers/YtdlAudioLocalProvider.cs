using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlAudioLocalProvider : ILocalMetadataProvider<Audio>
{
    public string Name => Constants.PluginName;

    public async Task<MetadataResult<Audio>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadAudioForMediaAsync(info.Path, cancellationToken);
        if (metadata is null)
        {
            return new MetadataResult<Audio>();
        }

        var item = new Audio
        {
            Name = metadata.Title,
            OriginalTitle = metadata.OriginalTitle,
            Album = metadata.Album,
            Artists = NonBlank(metadata.Artist),
            AlbumArtists = NonBlank(metadata.Artist),
            Overview = BuildOverview(metadata),
            HomePageUrl = metadata.WebUrl,
            PremiereDate = metadata.ReleaseDate?.UtcDateTime,
            ProductionYear = metadata.ReleaseDate?.Year,
            RunTimeTicks = metadata.Runtime?.Ticks,
            Genres = metadata.Categories.ToArray(),
            Tags = metadata.Tags.ToArray(),
            Studios = NonBlank(metadata.ChannelName),
            ForcedSortName = metadata.ReleaseDate is null
                ? metadata.Title
                : $"{metadata.ReleaseDate:yyyyMMdd}-{metadata.Title}"
        };

        if (!string.IsNullOrWhiteSpace(metadata.Id))
        {
            item.ProviderIds[Constants.YouTubeProviderKey] = metadata.Id;
        }

        return new MetadataResult<Audio>
        {
            HasMetadata = true,
            Item = item
        };
    }

    private static string[] NonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string? BuildOverview(YouTubeAudioMetadata metadata)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Description))
        {
            lines.Add(metadata.Description);
        }

        var stats = new List<string>();
        if (metadata.ViewCount.HasValue)
        {
            stats.Add($"{metadata.ViewCount.Value:N0} YouTube views");
        }

        if (metadata.LikeCount.HasValue)
        {
            stats.Add($"{metadata.LikeCount.Value:N0} likes");
        }

        if (!string.IsNullOrWhiteSpace(metadata.WebUrl))
        {
            stats.Add(metadata.WebUrl);
        }

        if (stats.Count > 0)
        {
            lines.Add(string.Join(" | ", stats));
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, lines);
    }
}
