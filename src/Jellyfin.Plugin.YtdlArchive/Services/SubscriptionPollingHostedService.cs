using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class SubscriptionPollingHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(12);

    private readonly ChannelSubscriptionManager _subscriptions;
    private readonly DownloaderHostedService _downloader;
    private readonly ILogger<SubscriptionPollingHostedService> _logger;

    public SubscriptionPollingHostedService(
        ChannelSubscriptionManager subscriptions,
        DownloaderHostedService downloader,
        ILogger<SubscriptionPollingHostedService> logger)
    {
        _subscriptions = subscriptions;
        _downloader = downloader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckSubscriptionsAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckSubscriptionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelSubscription> subscriptions;
        try
        {
            subscriptions = await _subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YtdlArchive could not load channel subscriptions");
            return;
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                var newVideos = await _subscriptions.FindNewVideosAsync(subscription, cancellationToken).ConfigureAwait(false);
                var queued = new List<string>();
                foreach (var video in newVideos)
                {
                    var result = await _downloader.QueueDownloadAsync(
                        new DownloadQueueRequest(video.Url, subscription.Quality, subscription.AudioFormat, subscription.Target, subscription.ChapterPercent),
                        cancellationToken).ConfigureAwait(false);

                    if (result.Queued || string.Equals(result.Reason, "already downloading", StringComparison.Ordinal))
                    {
                        queued.Add(video.Id);
                        _logger.LogInformation("YtdlArchive queued subscribed video {Title} from {ChannelName}", video.Title, subscription.ChannelName);
                    }
                    else
                    {
                        _logger.LogWarning("YtdlArchive could not queue subscribed video {VideoUrl}: {Error}", video.Url, result.Error ?? result.Reason);
                    }
                }

                await _subscriptions.MarkCheckedAsync(subscription, queued, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "YtdlArchive subscription check failed for {ChannelName}", subscription.ChannelName);
            }
        }
    }
}

public sealed record DownloadQueueRequest(string Url, string Quality, string? AudioFormat, string Target, int? ChapterPercent);

public sealed record DownloadQueueResult(bool Queued, string? Reason, string? Error, string? SaveTo, string? Target);
