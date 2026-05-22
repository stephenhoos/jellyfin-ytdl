using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class YtdlpManagerTests
{
    [Fact]
    public void BuildDefaultReleaseDownloadBase_UsesOfficialYtdlpReleaseEndpoint()
    {
        var result = InvokeStatic<string>("BuildDefaultReleaseDownloadBase");

        Assert.Equal("https://github.com/yt-dlp/yt-dlp/releases/latest/download", result);
    }

    [Fact]
    public void GetDownloadUrl_UsesConfiguredReleaseBaseWithoutTrailingSlash()
    {
        var original = Environment.GetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE");
        try
        {
            Environment.SetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE", "https://downloads.example/yt-dlp/");

            var result = InvokeStatic<string>("GetDownloadUrl");

            Assert.Contains("https://downloads.example/yt-dlp/", result, StringComparison.Ordinal);
            Assert.DoesNotContain("//yt-dlp", result, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE", original);
        }
    }

    [Fact]
    public async Task MatchesSha256Async_ComparesDownloadedBinaryHash()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "yt-dlp");
            await File.WriteAllTextAsync(path, "binary");
            var expected = "9a3a45d01531a20e89ac6ae10b0b0beb0492acd7216a368aa062d1a5fecaf9cd";

            var result = await InvokeStatic<Task<bool>>("MatchesSha256Async", path, expected, CancellationToken.None);

            Assert.True(result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindOnPath_ReturnsFirstExecutableMatch()
    {
        var directory = Directory.CreateTempSubdirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var executable = Path.Combine(directory.FullName, "yt-dlp");
            File.WriteAllText(executable, "#!/bin/sh\n");
            Environment.SetEnvironmentVariable("PATH", directory.FullName);

            var result = InvokeStatic<string?>("FindOnPath", "yt-dlp");

            Assert.Equal(executable, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FirstExisting_ReturnsFirstPathThatExists()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var missing = Path.Combine(directory.FullName, "missing");
            var existing = Path.Combine(directory.FullName, "existing");
            File.WriteAllText(existing, string.Empty);

            var result = InvokeStatic<string?>("FirstExisting", new object[] { new[] { missing, existing } });

            Assert.Equal(existing, result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryDelete_RemovesTemporaryDownloadWhenPresent()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "partial.download");
            File.WriteAllText(path, string.Empty);

            InvokeStatic<object?>("TryDelete", path);

            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(YtdlpManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }
}
