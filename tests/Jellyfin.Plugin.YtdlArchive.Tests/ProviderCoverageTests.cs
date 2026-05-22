using Jellyfin.Plugin.YtdlArchive.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class ProviderCoverageTests
{
    [Fact]
    public void YouTubeExternalIds_SupportExpectedJellyfinItemTypes()
    {
        var videoId = new YouTubeVideoExternalId();
        var channelId = new YouTubeChannelExternalId();

        Assert.Equal("https://www.youtube.com/watch?v={0}", videoId.UrlFormatString);
        Assert.True(videoId.Supports(new Episode()));
        Assert.True(videoId.Supports(new Audio()));
        Assert.True(videoId.Supports(new AudioBook()));
        Assert.False(videoId.Supports(new Series()));

        Assert.Equal("https://www.youtube.com/channel/{0}", channelId.UrlFormatString);
        Assert.True(channelId.Supports(new Series()));
        Assert.True(channelId.Supports(new MusicAlbum()));
        Assert.False(channelId.Supports(new Episode()));
    }

    [Fact]
    public async Task MusicAlbumProvider_UsesFirstAudioSidecarInDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("Artist Name [UCmusic]");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "track.info.json"),
                """
                {
                  "id": "abc12345678",
                  "title": "Artist Name - Track Title",
                  "description": "Album notes",
                  "channel": "Artist Channel",
                  "channel_id": "UCmusic",
                  "upload_date": "20260520",
                  "categories": ["Music"],
                  "tags": ["live"]
                }
                """);

            var result = await new YtdlMusicAlbumLocalProvider().GetMetadata(
                new ItemInfo(new MusicAlbum { Path = directory.FullName }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Artist Name", result.Item.AlbumArtists.Single());
            Assert.Equal("UCmusic", result.Item.ProviderIds["YouTube"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SeriesProvider_FallsBackToFolderChannelId()
    {
        var directory = Directory.CreateTempSubdirectory("Channel Name [UCYO_jab_esuFRV4b17AJtAw]");
        try
        {
            var result = await new YtdlSeriesLocalProvider().GetMetadata(
                new ItemInfo(new Series { Path = directory.FullName }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Channel Name", result.Item.Name);
            Assert.Equal("UCYO_jab_esuFRV4b17AJtAw", result.Item.ProviderIds["YouTube"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
