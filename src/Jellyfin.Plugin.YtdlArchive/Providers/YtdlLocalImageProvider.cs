using Jellyfin.Plugin.YtdlArchive.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlLocalImageProvider : ILocalImageProvider, IHasOrder
{
    private readonly IFileSystem _fileSystem;

    public YtdlLocalImageProvider(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public string Name => Constants.PluginName;

    public int Order => 1;

    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        var imagePath = item switch
        {
            Episode when !string.IsNullOrWhiteSpace(item.Path)
                => SidecarLocator.FindThumbnailPath(item.Path, File.Exists),
            Audio when !string.IsNullOrWhiteSpace(item.Path)
                => SidecarLocator.FindThumbnailPath(item.Path, File.Exists),
            AudioBook when !string.IsNullOrWhiteSpace(item.Path)
                => SidecarLocator.FindThumbnailPath(item.Path, File.Exists),
            Series when !string.IsNullOrWhiteSpace(item.Path)
                => FindFirstSeriesThumbnail(item.Path),
            MusicAlbum when !string.IsNullOrWhiteSpace(item.Path)
                => FindFirstSeriesThumbnail(item.Path),
            _ => null
        };

        if (imagePath is null)
        {
            yield break;
        }

        yield return new LocalImageInfo
        {
            FileInfo = _fileSystem.GetFileSystemInfo(imagePath)
        };
    }

    public bool Supports(BaseItem item)
        => item is Episode or Series or Audio or AudioBook or MusicAlbum;

    private static string? FindFirstSeriesThumbnail(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
