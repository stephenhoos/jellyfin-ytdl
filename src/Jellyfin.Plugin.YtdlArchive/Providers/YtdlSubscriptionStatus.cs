using Jellyfin.Plugin.YtdlArchive.Services;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

internal static class YtdlSubscriptionStatus
{
    public static string AppendToOverview(string? overview, ChannelSubscription subscription)
    {
        var status = $"YtdlArchive: Subscribed to {TargetLabel(subscription.Target)} ({QualityLabel(subscription)}).";
        return string.IsNullOrWhiteSpace(overview)
            ? status
            : $"{overview.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{status}";
    }

    private static string TargetLabel(string target)
        => target switch
        {
            ArchiveSettings.MusicTarget => "Music",
            ArchiveSettings.PodcastTarget => "Podcast",
            ArchiveSettings.AudiobookTarget => "Audiobook",
            _ => "Other"
        };

    private static string QualityLabel(ChannelSubscription subscription)
    {
        if (!string.Equals(subscription.Quality, "audio", StringComparison.OrdinalIgnoreCase))
        {
            return subscription.Quality;
        }

        return string.IsNullOrWhiteSpace(subscription.AudioFormat)
            ? "audio"
            : subscription.AudioFormat.ToUpperInvariant();
    }
}
