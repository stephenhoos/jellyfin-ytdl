using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.YtdlArchive.Metadata;

public sealed class YtdlpInfoJson
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("fulltitle")]
    public string? FullTitle { get; init; }

    [JsonPropertyName("track")]
    public string? Track { get; init; }

    [JsonPropertyName("artist")]
    public string? Artist { get; init; }

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("creator")]
    public string? Creator { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("release_year")]
    public int? ReleaseYear { get; init; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    [JsonPropertyName("duration")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("uploader")]
    public string? Uploader { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("webpage_url")]
    public string? WebpageUrl { get; init; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; init; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; init; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<string>? Categories { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("thumbnails")]
    public IReadOnlyList<YtdlpThumbnail>? Thumbnails { get; init; }
}

public sealed class YtdlpThumbnail
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }
}
