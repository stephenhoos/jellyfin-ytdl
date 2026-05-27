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
    public async Task AudioProvider_MapsAudioSidecarMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("Album Folder [UCmusic]");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "track.m4a");
            File.WriteAllText(mediaPath, string.Empty);
            WriteAudioSidecar(mediaPath);

            var result = await new YtdlAudioLocalProvider().GetMetadata(
                new ItemInfo(new Audio { Path = mediaPath }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Track Title", result.Item.Name);
            Assert.Equal("Source Title", result.Item.OriginalTitle);
            Assert.Equal("Album Name", result.Item.Album);
            Assert.Equal("Artist Name", result.Item.Artists.Single());
            Assert.Equal("Artist Name", result.Item.AlbumArtists.Single());
            Assert.Equal("Channel Name", result.Item.Studios.Single());
            Assert.Equal("Music", result.Item.Genres.Single());
            Assert.Equal("live", result.Item.Tags.Single());
            Assert.Equal("abc12345678", result.Item.ProviderIds["YouTube"]);
            Assert.Equal("20260520-Track Title", result.Item.ForcedSortName);
            Assert.Equal(TimeSpan.FromSeconds(125).Ticks, result.Item.RunTimeTicks);
            Assert.Contains("1,234 YouTube views", result.Item.Overview);
            Assert.Contains("https://youtu.be/abc12345678", result.Item.Overview);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AudioBookProvider_MapsCommonAudioSidecarMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("Audiobook Folder [UCmusic]");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "chapter.m4b");
            File.WriteAllText(mediaPath, string.Empty);
            WriteAudioSidecar(mediaPath);

            var result = await new YtdlAudioBookLocalProvider().GetMetadata(
                new ItemInfo(new AudioBook { Path = mediaPath }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Track Title", result.Item.Name);
            Assert.Equal("Album Name", result.Item.Album);
            Assert.Equal("Channel Name", result.Item.Studios.Single());
            Assert.Equal("abc12345678", result.Item.ProviderIds["YouTube"]);
            Assert.Contains("9 likes", result.Item.Overview);
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

    private static void WriteAudioSidecar(string mediaPath)
    {
        File.WriteAllText(
            Path.ChangeExtension(mediaPath, ".info.json"),
            """
            {
              "id": "abc12345678",
              "title": "Source Title",
              "track": "Track Title",
              "artist": "Artist Name",
              "album": "Album Name",
              "description": "Audio notes",
              "channel": "Channel Name",
              "release_date": "20260520",
              "duration": 125,
              "view_count": 1234,
              "like_count": 9,
              "webpage_url": "https://youtu.be/abc12345678",
              "categories": ["Music"],
              "tags": ["live"]
            }
            """);
    }
}
