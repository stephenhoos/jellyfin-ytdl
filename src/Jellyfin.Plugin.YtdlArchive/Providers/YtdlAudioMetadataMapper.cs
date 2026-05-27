using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

internal static class YtdlAudioMetadataMapper
{
    public static void ApplyCommonMetadata(BaseItem item, YouTubeAudioMetadata metadata)
    {
        item.Name = metadata.Title;
        item.OriginalTitle = metadata.OriginalTitle;
        item.Overview = BuildOverview(metadata);
        item.HomePageUrl = metadata.WebUrl;
        item.PremiereDate = metadata.ReleaseDate?.UtcDateTime;
        item.ProductionYear = metadata.ReleaseDate?.Year;
        item.RunTimeTicks = metadata.Runtime?.Ticks;
        item.Genres = metadata.Categories.ToArray();
        item.Tags = metadata.Tags.ToArray();
        item.Studios = NonBlank(metadata.ChannelName);
        item.ForcedSortName = metadata.ReleaseDate is null
            ? metadata.Title
            : $"{metadata.ReleaseDate:yyyyMMdd}-{metadata.Title}";

        if (!string.IsNullOrWhiteSpace(metadata.Id))
        {
            item.ProviderIds[Constants.YouTubeProviderKey] = metadata.Id;
        }
    }

    public static MetadataResult<TItem> CreateResult<TItem>(TItem item)
        where TItem : BaseItem
        => new()
        {
            HasMetadata = true,
            Item = item
        };

    public static string[] NonBlank(string? value)
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
