using Jellyfin.Plugin.YtdlArchive.Metadata;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class YouTubeMetadataNormalizerTests
{
    [Fact]
    public void NormalizeVideo_MapsYtdlpFieldsToNeutralMetadata()
    {
        var result = YouTubeMetadataNormalizer.NormalizeVideo(new YtdlpInfoJson
        {
            Id = "F3Qixy-r_rQ",
            Title = "A video",
            Description = "Description",
            UploadDate = "20240501",
            DurationSeconds = 90,
            Uploader = "Uploader",
            Channel = "Channel",
            ChannelId = "UCYO_jab_esuFRV4b17AJtAw",
            Categories = ["Education", "Education", ""],
            Tags = ["math", "Math"],
            Thumbnails =
            [
                new YtdlpThumbnail { Url = "small.jpg", Width = 100, Height = 100 },
                new YtdlpThumbnail { Url = "large.jpg", Width = 400, Height = 400 }
            ]
        });

        Assert.Equal("A video", result.Title);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), result.UploadDate);
        Assert.Equal(TimeSpan.FromSeconds(90), result.Runtime);
        Assert.Equal("Channel", result.ChannelName);
        Assert.Equal("https://www.youtube.com/watch?v=F3Qixy-r_rQ", result.WebUrl);
        Assert.Equal("large.jpg", result.ThumbnailUrl);
        Assert.Equal(["Education"], result.Categories);
        Assert.Equal(["math"], result.Tags);
    }

    [Fact]
    public void NormalizeAudio_UsesTitleArtistBeforeUploaderForMusicVideo()
    {
        var result = YouTubeMetadataNormalizer.NormalizeAudio(new YtdlpInfoJson
        {
            Id = "N4bFqW_eu2I",
            Title = "The Animals - House Of The Rising Sun (Music Video) [4K HD]",
            Uploader = "Timeless Music",
            Channel = "Timeless Music",
            UploadDate = "20210301"
        }, "Timeless_Music");

        Assert.Equal("House Of The Rising Sun", result.Title);
        Assert.Equal("The Animals", result.Artist);
        Assert.Equal("The Animals", result.Album);
    }

    [Fact]
    public void NormalizeAudio_RemovesEditorialSuffixesFromParsedTitle()
    {
        var result = YouTubeMetadataNormalizer.NormalizeAudio(new YtdlpInfoJson
        {
            Id = "nz_-KNNl-no",
            Title = "Tom Lehrer - Pollution - with intro",
            Uploader = "The Tom Lehrer Wisdom Channel",
            Channel = "The Tom Lehrer Wisdom Channel",
            UploadDate = "20090824"
        }, "The_Tom_Lehrer_Wisdom_Channel");

        Assert.Equal("Pollution", result.Title);
        Assert.Equal("Tom Lehrer", result.Artist);
        Assert.Equal("Tom Lehrer", result.Album);
    }
}
