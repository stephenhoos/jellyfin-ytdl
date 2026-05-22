namespace Jellyfin.Plugin.YtdlArchive.Metadata;

public sealed record YouTubeVideoMetadata(
    string? Id,
    string? Title,
    string? Description,
    DateTimeOffset? UploadDate,
    TimeSpan? Runtime,
    string? ChannelName,
    string? ChannelId,
    string? WebUrl,
    string? ThumbnailUrl,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags);

public sealed record YouTubeAudioMetadata(
    string? Id,
    string? Title,
    string? OriginalTitle,
    string? Artist,
    string? Album,
    string? Description,
    DateTimeOffset? ReleaseDate,
    TimeSpan? Runtime,
    string? ChannelName,
    string? ChannelId,
    string? WebUrl,
    string? ThumbnailUrl,
    long? ViewCount,
    long? LikeCount,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags);
