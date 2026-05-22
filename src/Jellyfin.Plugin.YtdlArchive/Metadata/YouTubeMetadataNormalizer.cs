using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YtdlArchive.Metadata;

public static partial class YouTubeMetadataNormalizer
{
    public static YouTubeVideoMetadata NormalizeVideo(YtdlpInfoJson source)
    {
        var uploadDate = ParseUploadDate(source.UploadDate)
            ?? ParseUnixTimestamp(source.Timestamp);

        return new YouTubeVideoMetadata(
            source.Id,
            source.Title,
            source.Description,
            uploadDate,
            source.DurationSeconds is > 0 ? TimeSpan.FromSeconds(source.DurationSeconds.Value) : null,
            FirstNonBlank(source.Channel, source.Uploader),
            source.ChannelId,
            FirstNonBlank(source.WebpageUrl, BuildWatchUrl(source.Id)),
            FirstNonBlank(GetBestThumbnail(source), source.Thumbnail),
            source.Categories?.Where(IsUsefulText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            source.Tags?.Where(IsUsefulText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []);
    }

    public static YouTubeAudioMetadata NormalizeAudio(YtdlpInfoJson source, string? fallbackAlbum)
    {
        var parsed = MusicTitleParser.Parse(FirstNonBlank(source.Track, source.Title, source.FullTitle));
        var artist = FirstNonBlank(source.Artist, source.Creator, parsed.Artist, source.Channel, source.Uploader);
        var title = FirstNonBlank(source.Track, parsed.Title, source.Title, source.FullTitle);
        var album = FirstNonBlank(source.Album, parsed.Artist, fallbackAlbum, source.Channel, source.Uploader);
        var releaseDate = ParseUploadDate(source.ReleaseDate)
            ?? ParseUploadDate(source.UploadDate)
            ?? ParseUnixTimestamp(source.Timestamp);

        return new YouTubeAudioMetadata(
            source.Id,
            CleanMusicTitle(title),
            source.Title,
            artist,
            album,
            source.Description,
            releaseDate,
            source.DurationSeconds is > 0 ? TimeSpan.FromSeconds(source.DurationSeconds.Value) : null,
            FirstNonBlank(source.Channel, source.Uploader),
            source.ChannelId,
            FirstNonBlank(source.WebpageUrl, BuildWatchUrl(source.Id)),
            FirstNonBlank(GetBestThumbnail(source), source.Thumbnail),
            source.ViewCount,
            source.LikeCount,
            source.Categories?.Where(IsUsefulText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            source.Tags?.Where(IsUsefulText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []);
    }

    public static DateTimeOffset? ParseUploadDate(string? value)
    {
        if (DateTimeOffset.TryParseExact(
            value,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ParseUnixTimestamp(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : null;

    private static string? BuildWatchUrl(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : $"https://www.youtube.com/watch?v={id}";

    private static string? GetBestThumbnail(YtdlpInfoJson source)
        => source.Thumbnails?
            .Where(thumbnail => IsUsefulText(thumbnail.Url))
            .OrderBy(thumbnail => thumbnail.Width.GetValueOrDefault() * thumbnail.Height.GetValueOrDefault())
            .LastOrDefault()
            ?.Url;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(IsUsefulText);

    private static bool IsUsefulText(string? value)
        => !string.IsNullOrWhiteSpace(value);

    private static string? CleanMusicTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var cleaned = value
            .Replace("(Official Music Video)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[Official Music Video]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(Music Video)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[Music Video]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[4K HD]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[HD]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        cleaned = EditorialSuffixPattern().Replace(cleaned, string.Empty).Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? value : cleaned;
    }

    [GeneratedRegex(@"\s*[-–—]\s*(?:with\s+)?(?:intro|introduction|outro|lyrics?|audio|video|remaster(?:ed)?|live|mono|stereo)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditorialSuffixPattern();
}

public static class MusicTitleParser
{
    public static (string? Artist, string? Title) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, value);
        }

        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index >= value.Length - separator.Length)
            {
                continue;
            }

            return (value[..index].Trim(), value[(index + separator.Length)..].Trim());
        }

        return (null, value.Trim());
    }
}
