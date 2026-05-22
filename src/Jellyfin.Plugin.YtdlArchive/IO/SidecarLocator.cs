namespace Jellyfin.Plugin.YtdlArchive.IO;

public static class SidecarLocator
{
    private static readonly string[] ThumbnailExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static string GetInfoJsonPath(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(directory, $"{name}.info.json");
    }

    public static string? FindThumbnailPath(string mediaPath, Func<string, bool> exists)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mediaPath);

        foreach (var extension in ThumbnailExtensions)
        {
            var candidate = Path.Combine(directory, $"{name}{extension}");
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
