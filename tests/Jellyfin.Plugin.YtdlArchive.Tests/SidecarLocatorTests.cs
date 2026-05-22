using Jellyfin.Plugin.YtdlArchive.IO;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class SidecarLocatorTests
{
    [Fact]
    public void GetInfoJsonPath_UsesMediaBaseName()
    {
        var path = SidecarLocator.GetInfoJsonPath("/media/YouTube/Video [abc12345678].mp4");

        Assert.Equal("/media/YouTube/Video [abc12345678].info.json", path);
    }

    [Fact]
    public void FindThumbnailPath_ReturnsFirstMatchingSupportedExtension()
    {
        var existing = new HashSet<string> { "/media/Video [abc12345678].webp" };

        var path = SidecarLocator.FindThumbnailPath("/media/Video [abc12345678].mp4", existing.Contains);

        Assert.Equal("/media/Video [abc12345678].webp", path);
    }
}
