using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class ChannelSubscriptionManager
{
    private const int RecentVideoLimit = 5;
    private static readonly HashSet<string> AllowedSubscriptionHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly YtdlpManager _ytdlpManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ChannelSubscriptionManager> _logger;
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    public ChannelSubscriptionManager(
        YtdlpManager ytdlpManager,
        IApplicationPaths applicationPaths,
        ILogger<ChannelSubscriptionManager> logger)
    {
        _ytdlpManager = ytdlpManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChannelSubscription>> ListAsync(CancellationToken cancellationToken)
    {
        var store = await ReadStoreAsync(cancellationToken).ConfigureAwait(false);
        return store.Subscriptions
            .OrderBy(subscription => subscription.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CurrentStorePath => StorePath();

    public string CurrentLegacyStorePath => LegacyStorePath();

    public async Task<ChannelSubscription?> FindByChannelIdAsync(string? channelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        var store = await ReadStoreAsync(cancellationToken).ConfigureAwait(false);
        return store.Subscriptions.FirstOrDefault(subscription =>
            string.Equals(subscription.ChannelId, channelId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ChannelSubscription> SubscribeAsync(SubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            throw new ArgumentException("Missing url", nameof(request));
        }

        if (!IsAllowedSubscriptionUrl(request.Url))
        {
            throw new ArgumentException("Only YouTube URLs can be subscribed to", nameof(request));
        }

        var target = ArchiveSettings.NormalizeTarget(request.Target);
        if (target.Length == 0)
        {
            throw new ArgumentException("Unsupported target", nameof(request));
        }

        var metadata = await ReadChannelMetadataAsync(request.Url.Trim(), cancellationToken).ConfigureAwait(false);
        var recent = await ReadRecentVideosAsync(metadata.ChannelUrl, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var subscription = new ChannelSubscription
        {
            ChannelId = metadata.ChannelId,
            ChannelName = metadata.ChannelName,
            ChannelUrl = metadata.ChannelUrl,
            Quality = string.IsNullOrWhiteSpace(request.Quality) ? "best" : request.Quality.Trim(),
            AudioFormat = string.IsNullOrWhiteSpace(request.AudioFormat) ? null : request.AudioFormat.Trim(),
            Target = target,
            ChapterPercent = request.ChapterPercent,
            CreatedUtc = now,
            LastCheckedUtc = now,
            SeenVideoIds = request.DownloadExistingVideos
                ? []
                : recent
                    .Select(video => video.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(RecentVideoLimit)
                    .ToList()
        };

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var index = store.Subscriptions.FindIndex(existing => SameSubscription(existing, subscription));
            if (index >= 0)
            {
                subscription.CreatedUtc = store.Subscriptions[index].CreatedUtc;
                if (!request.DownloadExistingVideos)
                {
                    subscription.SeenVideoIds = store.Subscriptions[index].SeenVideoIds
                        .Union(subscription.SeenVideoIds, StringComparer.OrdinalIgnoreCase)
                        .Take(50)
                        .ToList();
                }

                store.Subscriptions[index] = subscription;
            }
            else
            {
                store.Subscriptions.Add(subscription);
            }

            await WriteStoreUnlockedAsync(store, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storeLock.Release();
        }

        _logger.LogInformation("YtdlArchive subscribed to {ChannelName} ({ChannelId})", subscription.ChannelName, subscription.ChannelId);
        return subscription;
    }

    public async Task<IReadOnlyList<SubscriptionVideo>> FindNewVideosAsync(ChannelSubscription subscription, CancellationToken cancellationToken)
    {
        var recent = await ReadRecentVideosAsync(subscription.ChannelUrl, cancellationToken).ConfigureAwait(false);
        var seen = subscription.SeenVideoIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return recent
            .Where(video => !string.IsNullOrWhiteSpace(video.Id) && !seen.Contains(video.Id))
            .Reverse()
            .ToArray();
    }

    public async Task<IReadOnlyList<SubscriptionVideo>> FindExistingVideosAsync(ChannelSubscription subscription, CancellationToken cancellationToken)
    {
        var videos = await ReadVideosAsync(subscription.ChannelUrl, playlistEnd: null, cancellationToken).ConfigureAwait(false);
        return videos
            .Reverse()
            .ToArray();
    }

    public async Task MarkCheckedAsync(ChannelSubscription subscription, IEnumerable<string> seenVideoIds, CancellationToken cancellationToken)
    {
        var seen = seenVideoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var index = store.Subscriptions.FindIndex(existing => SameSubscription(existing, subscription));
            if (index < 0)
            {
                return;
            }

            var stored = store.Subscriptions[index];
            stored.LastCheckedUtc = DateTimeOffset.UtcNow;
            stored.SeenVideoIds = seen
                .Concat(stored.SeenVideoIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();
            store.Subscriptions[index] = stored;
            await WriteStoreUnlockedAsync(store, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task<ChannelMetadata> ReadChannelMetadataAsync(string url, CancellationToken cancellationToken)
    {
        var ytdlp = await RequireYtdlpAsync(cancellationToken).ConfigureAwait(false);
        var json = await RunYtdlpJsonAsync(
            ytdlp,
            [
                "--dump-single-json",
                "--skip-download",
                "--no-playlist",
                "--no-warnings",
                url
            ],
            cancellationToken).ConfigureAwait(false);

        var metadata = JsonSerializer.Deserialize<YtdlpChannelMetadata>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not read channel metadata.");
        var channelId = FirstNonBlank(metadata.ChannelId, metadata.UploaderId, metadata.Id);
        var channelName = FirstNonBlank(metadata.Channel, metadata.Uploader, metadata.Title, channelId);
        var channelUrl = NormalizeChannelUrl(FirstNonBlank(metadata.ChannelUrl, metadata.UploaderUrl, url) ?? url, channelId);
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(channelName))
        {
            throw new InvalidOperationException("Could not identify the YouTube channel.");
        }

        return new ChannelMetadata(channelId, channelName, channelUrl);
    }

    private async Task<IReadOnlyList<SubscriptionVideo>> ReadRecentVideosAsync(string channelUrl, CancellationToken cancellationToken)
        => await ReadVideosAsync(channelUrl, RecentVideoLimit, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<SubscriptionVideo>> ReadVideosAsync(string channelUrl, int? playlistEnd, CancellationToken cancellationToken)
    {
        var ytdlp = await RequireYtdlpAsync(cancellationToken).ConfigureAwait(false);
        var arguments = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json"
        };
        if (playlistEnd is not null)
        {
            arguments.Add("--playlist-end");
            arguments.Add(playlistEnd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add("--no-warnings");
        arguments.Add(channelUrl);

        var json = await RunYtdlpJsonAsync(ytdlp, arguments, cancellationToken).ConfigureAwait(false);

        var playlist = JsonSerializer.Deserialize<YtdlpPlaylist>(json, JsonOptions);
        return playlist?.Entries?
            .Select(entry => new SubscriptionVideo(
                FirstNonBlank(entry.Id, ExtractVideoId(entry.Url)) ?? string.Empty,
                FirstNonBlank(entry.Title, entry.Id) ?? string.Empty,
                BuildVideoUrl(entry.Url, entry.Id)))
            .Where(video => !string.IsNullOrWhiteSpace(video.Id) && !string.IsNullOrWhiteSpace(video.Url))
            .ToArray()
            ?? [];
    }

    private async Task<string> RequireYtdlpAsync(CancellationToken cancellationToken)
        => await _ytdlpManager.EnsureAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("yt-dlp not found. Install it with: pip install yt-dlp");

    private static async Task<string> RunYtdlpJsonAsync(string ytdlpPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytdlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            return stdout;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        throw new InvalidOperationException(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "yt-dlp failed");
    }

    private async Task<SubscriptionStore> ReadStoreAsync(CancellationToken cancellationToken)
    {
        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadStoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task<SubscriptionStore> ReadStoreUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = StorePath();
        if (!File.Exists(path))
        {
            path = LegacyStorePath();
        }

        if (!File.Exists(path))
        {
            return new SubscriptionStore();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SubscriptionStore>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new SubscriptionStore();
    }

    private async Task WriteStoreUnlockedAsync(SubscriptionStore store, CancellationToken cancellationToken)
    {
        var path = StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _applicationPaths.ProgramDataPath);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private string StorePath()
        => Path.Combine(_applicationPaths.ProgramDataPath, "data", "ytdlarchive", "subscriptions.json");

    private string LegacyStorePath()
        => Path.Combine(_applicationPaths.ProgramDataPath, "plugins", "YtdlArchive", "subscriptions.json");

    private static bool IsAllowedSubscriptionUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        return AllowedSubscriptionHosts.Contains(uri.Host);
    }

    private static string NormalizeChannelUrl(string url, string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(channelId) && channelId.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.youtube.com/channel/{channelId}/videos";
        }

        var trimmed = url.Trim().TrimEnd('/');
        return trimmed.EndsWith("/videos", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/videos";
    }

    private static string? ExtractVideoId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return FirstNonBlank(QueryValue(uri, "v"), uri.Segments.LastOrDefault()?.Trim('/'));
        }

        return value.Length == 11 ? value : null;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal));
            }
        }

        return null;
    }

    private static string BuildVideoUrl(string? url, string? id)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        var videoId = FirstNonBlank(id, ExtractVideoId(url));
        return string.IsNullOrWhiteSpace(videoId) ? string.Empty : $"https://www.youtube.com/watch?v={videoId}";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool SameSubscription(ChannelSubscription left, ChannelSubscription right)
        => string.Equals(left.ChannelId, right.ChannelId, StringComparison.OrdinalIgnoreCase);

    private sealed record ChannelMetadata(string ChannelId, string ChannelName, string ChannelUrl);

    private sealed class SubscriptionStore
    {
        public List<ChannelSubscription> Subscriptions { get; set; } = [];
    }

    private sealed record YtdlpChannelMetadata(
        string? Id,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        string? Channel,
        [property: JsonPropertyName("channel_url")] string? ChannelUrl,
        [property: JsonPropertyName("uploader_id")] string? UploaderId,
        string? Uploader,
        [property: JsonPropertyName("uploader_url")] string? UploaderUrl,
        string? Title);

    private sealed record YtdlpPlaylist(List<YtdlpPlaylistEntry>? Entries);

    private sealed record YtdlpPlaylistEntry(string? Id, string? Url, string? Title);
}

public sealed class SubscriptionRequest
{
    public string? Url { get; set; }

    public string? Quality { get; set; }

    public string? AudioFormat { get; set; }

    public string? Target { get; set; }

    public int? ChapterPercent { get; set; }

    public bool DownloadExistingVideos { get; set; }
}

public sealed class ChannelSubscription
{
    public string ChannelId { get; set; } = string.Empty;

    public string ChannelName { get; set; } = string.Empty;

    public string ChannelUrl { get; set; } = string.Empty;

    public string Quality { get; set; } = "best";

    public string? AudioFormat { get; set; }

    public string Target { get; set; } = ArchiveSettings.OtherTarget;

    public int? ChapterPercent { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset LastCheckedUtc { get; set; }

    public List<string> SeenVideoIds { get; set; } = [];
}

public sealed record SubscriptionVideo(string Id, string Title, string Url);
