using System.Text.Json;
using Jellyfin.Plugin.YtdlArchive.IO;

namespace Jellyfin.Plugin.YtdlArchive.Metadata;

public static class YtdlpSidecarReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<YouTubeVideoMetadata?> ReadVideoForMediaAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var sidecarPath = SidecarLocator.GetInfoJsonPath(mediaPath);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(sidecarPath);
        var info = await JsonSerializer.DeserializeAsync<YtdlpInfoJson>(stream, Options, cancellationToken);
        return info is null ? null : YouTubeMetadataNormalizer.NormalizeVideo(info);
    }

    public static async Task<YouTubeAudioMetadata?> ReadAudioForMediaAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var sidecarPath = SidecarLocator.GetInfoJsonPath(mediaPath);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(sidecarPath);
        var info = await JsonSerializer.DeserializeAsync<YtdlpInfoJson>(stream, Options, cancellationToken);
        return info is null
            ? null
            : YouTubeMetadataNormalizer.NormalizeAudio(info, StripBracketedId(Path.GetFileName(Path.GetDirectoryName(mediaPath))));
    }

    public static async Task<YouTubeVideoMetadata?> ReadFirstVideoInDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            return null;
        }

        var sidecarPath = Directory.EnumerateFiles(directoryPath, "*.info.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sidecarPath is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(sidecarPath);
        var info = await JsonSerializer.DeserializeAsync<YtdlpInfoJson>(stream, Options, cancellationToken);
        return info is null ? null : YouTubeMetadataNormalizer.NormalizeVideo(info);
    }

    public static async Task<YouTubeAudioMetadata?> ReadFirstAudioInDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            return null;
        }

        var sidecarPath = Directory.EnumerateFiles(directoryPath, "*.info.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sidecarPath is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(sidecarPath);
        var info = await JsonSerializer.DeserializeAsync<YtdlpInfoJson>(stream, Options, cancellationToken);
        return info is null
            ? null
            : YouTubeMetadataNormalizer.NormalizeAudio(info, StripBracketedId(Path.GetFileName(directoryPath)));
    }

    private static string StripBracketedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bracket = value.LastIndexOf("[", StringComparison.Ordinal);
        return bracket > 0 ? value[..bracket].Trim() : value;
    }
}
