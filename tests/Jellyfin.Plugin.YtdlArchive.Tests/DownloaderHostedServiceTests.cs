using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class DownloaderHostedServiceTests
{
    [Theory]
    [InlineData(null, "mp3")]
    [InlineData("", "mp3")]
    [InlineData("m4b", "m4a")]
    [InlineData("opus", "opus")]
    public void ResolveAudioFormat_NormalizesRequestedFormat(string? requested, string expected)
    {
        var result = InvokeStatic<string>("ResolveAudioFormat", requested);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildChapterMetadata_AddsPercentChaptersFromSidecarDuration()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "A=B;C#D.m4a");
            File.WriteAllText(mediaPath, string.Empty);
            File.WriteAllText(
                Path.ChangeExtension(mediaPath, ".info.json"),
                """{"duration":100}""");

            var metadata = InvokeStatic<string>("BuildChapterMetadata", mediaPath, 20);

            Assert.Contains(";FFMETADATA1", metadata);
            Assert.Contains("title=A\\=B\\;C\\#D", metadata);
            Assert.Contains("START=0", metadata);
            Assert.Contains("END=20000", metadata);
            Assert.Contains("title=80%", metadata);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildChapterMetadata_WithoutDurationOnlyWritesTitle()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "No Duration.m4a");
            File.WriteAllText(mediaPath, string.Empty);

            var metadata = InvokeStatic<string>("BuildChapterMetadata", mediaPath, 10);

            Assert.Contains("title=No Duration", metadata);
            Assert.DoesNotContain("[CHAPTER]", metadata);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(DownloaderHostedService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }
}
