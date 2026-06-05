using Jellyfin.Plugin.YtdlArchive.Ids;
using Jellyfin.Plugin.YtdlArchive.Metadata;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using System.IO;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlSeriesLocalProvider : ILocalMetadataProvider<Series>
{
    private readonly ChannelSubscriptionManager? _subscriptions;

    public YtdlSeriesLocalProvider(ChannelSubscriptionManager? subscriptions = null)
    {
        _subscriptions = subscriptions;
    }

    public string Name => Constants.PluginName;

    public async Task<MetadataResult<Series>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadFirstVideoInDirectoryAsync(info.Path, cancellationToken);
        var folderName = Path.GetFileName(info.Path);
        var channelName = metadata?.ChannelName ?? StripBracketedId(folderName);
        var channelId = metadata?.ChannelId ?? YouTubeIdParser.FindChannelId(folderName);

        if (string.IsNullOrWhiteSpace(channelName) && string.IsNullOrWhiteSpace(channelId))
        {
            return new MetadataResult<Series>();
        }

        var overview = metadata?.Description;
        var subscription = _subscriptions is null
            ? null
            : await _subscriptions.FindByChannelIdAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (subscription is not null)
        {
            overview = YtdlSubscriptionStatus.AppendToOverview(overview, subscription);
        }

        var series = new Series
        {
            Name = channelName,
            Overview = overview
        };

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            series.ProviderIds[Constants.YouTubeProviderKey] = channelId;
        }

        return new MetadataResult<Series>
        {
            HasMetadata = true,
            Item = series
        };
    }

    private static string StripBracketedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bracket = value.LastIndexOf('[');
        return bracket > 0 ? value[..bracket].Trim() : value;
    }

}
