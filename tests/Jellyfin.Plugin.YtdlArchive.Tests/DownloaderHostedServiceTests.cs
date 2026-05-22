using System.Reflection;
using System.Text.Json;
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
    public void BuildYtdlpArguments_AddsVideoMergeOptions()
    {
        var args = InvokeStatic<IEnumerable<string>>(
            "BuildYtdlpArguments",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "720",
            null,
            "other").ToArray();

        Assert.Contains("-f", args);
        Assert.Contains("bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720][ext=mp4]/best", args);
        Assert.Contains("--merge-output-format", args);
        Assert.Contains("mp4", args);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", args[^1]);
    }

    [Fact]
    public void BuildYtdlpArguments_AddsAudioExtractionOptions()
    {
        var args = InvokeStatic<IEnumerable<string>>(
            "BuildYtdlpArguments",
            "https://youtu.be/dQw4w9WgXcQ",
            "audio",
            "m4b",
            "audiobook").ToArray();

        Assert.Contains("--extract-audio", args);
        Assert.Equal("m4a", args[Array.IndexOf(args, "--audio-format") + 1]);
        Assert.Contains("--embed-metadata", args);
    }

    [Theory]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("http://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", false)]
    public void IsAllowedDownloadUrl_RestrictsHostsByDefault(string url, bool expected)
    {
        var result = InvokeStatic<bool>("IsAllowedDownloadUrl", url);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("chrome-extension://abcdef", true)]
    [InlineData("moz-extension://abcdef", true)]
    [InlineData("http://localhost:8096", true)]
    [InlineData("https://www.youtube.com", true)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    public void IsAllowedCorsOrigin_AllowsExpectedBrowserAndMediaOrigins(string origin, bool expected)
    {
        var result = InvokeStatic<bool>("IsAllowedCorsOrigin", origin);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void JsonHelpers_ReadOnlyExpectedValueKinds()
    {
        using var document = JsonDocument.Parse("""{"name":"folder","count":20,"wrong":"20"}""");

        Assert.Equal("folder", InvokeStatic<string?>("GetString", document.RootElement, "name"));
        Assert.Null(InvokeStatic<string?>("GetString", document.RootElement, "count"));
        Assert.Equal(20, InvokeStatic<int?>("GetInt", document.RootElement, "count"));
        Assert.Null(InvokeStatic<int?>("GetInt", document.RootElement, "wrong"));
    }

    [Fact]
    public void FindNewestM4a_ReturnsNewestFileWrittenNearDownloadStart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var oldPath = Path.Combine(directory.FullName, "old.m4a");
            var newPath = Path.Combine(directory.FullName, "new.m4a");
            File.WriteAllText(oldPath, string.Empty);
            File.WriteAllText(newPath, string.Empty);
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

            var result = InvokeStatic<string?>("FindNewestM4a", directory.FullName, DateTime.UtcNow);

            Assert.Equal(newPath, result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PathHelpers_DetectSameOrChildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-root");
        var child = Path.Combine(root, "child");

        Assert.True(InvokeStatic<bool>("IsSameOrChildPath", child, root));
        Assert.True(InvokeStatic<bool>("SamePath", root + Path.DirectorySeparatorChar, root));
        Assert.Equal(root, InvokeStatic<string>("NormalizeDirectoryPath", root + Path.DirectorySeparatorChar));
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
