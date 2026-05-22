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

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(YtdlpManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }
}
