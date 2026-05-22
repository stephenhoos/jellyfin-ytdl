using Jellyfin.Plugin.YtdlArchive.Ids;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class YouTubeIdParserTests
{
    [Theory]
    [InlineData("Some Video [F3Qixy-r_rQ].mp4", "F3Qixy-r_rQ")]
    [InlineData("prefix [aaaaaaaaaaa] middle [bbbbbbbbbbb].mkv", "bbbbbbbbbbb")]
    [InlineData("No id here.mkv", null)]
    public void FindVideoId_ExtractsLastBracketedVideoId(string value, string? expected)
        => Assert.Equal(expected, YouTubeIdParser.FindVideoId(value));

    [Fact]
    public void FindChannelId_RequiresUcPrefix()
        => Assert.Equal(
            "UCYO_jab_esuFRV4b17AJtAw",
            YouTubeIdParser.FindChannelId("Channel [UCYO_jab_esuFRV4b17AJtAw]"));

    [Theory]
    [InlineData("F3Qixy-r_rQ", true)]
    [InlineData("too-short", false)]
    [InlineData("UCYO_jab_esuFRV4b17AJtAw", false)]
    public void IsValidVideoId_ValidatesStrictly(string value, bool expected)
        => Assert.Equal(expected, YouTubeIdParser.IsValidVideoId(value));
}
