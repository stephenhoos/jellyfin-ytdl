using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;

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
    public void ManagedExecutableName_UsesCurrentPlatformExecutableName()
    {
        var result = InvokeStatic<string>("get_ManagedExecutableName");

        Assert.Equal(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp", result);
    }

    [Fact]
    public void GetDownloadAssetName_UsesCurrentPlatformAssetName()
    {
        var result = InvokeStatic<string>("GetDownloadAssetName");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("yt-dlp.exe", result);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("yt-dlp_macos", result);
        }
        else
        {
            Assert.Equal("yt-dlp", result);
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
    public async Task GetExpectedSha256Async_ReadsMatchingChecksumLine()
    {
        var original = Environment.GetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE");
        try
        {
            Environment.SetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE", "https://downloads.example/yt-dlp");
            var assetName = InvokeStatic<string>("GetDownloadAssetName");
            using var client = new HttpClient(new StaticResponseHandler($"abc123  other-file\nfeedface  {assetName}\n"));

            var result = await InvokeStatic<Task<string?>>("GetExpectedSha256Async", client, CancellationToken.None);

            Assert.Equal("feedface", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTDLP_RELEASE_DOWNLOAD_BASE", original);
        }
    }

    [Fact]
    public async Task EnsureAsync_FallsBackToPathSearchWhenManagedInstallFails()
    {
        var directory = Directory.CreateTempSubdirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var executable = Path.Combine(directory.FullName, "yt-dlp");
            File.WriteAllText(executable, string.Empty);
            Environment.SetEnvironmentVariable("PATH", directory.FullName);

            var manager = new YtdlpManager(
                new StaticHttpClientFactory(),
                ServerPathsProxy.Create(directory.FullName),
                NullLogger<YtdlpManager>.Instance);

            var result = await manager.EnsureAsync(CancellationToken.None);

            Assert.Equal(executable, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
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

    [Fact]
    public void TryDelete_IgnoresDirectoryPaths()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            InvokeStatic<object?>("TryDelete", directory.FullName);

            Assert.True(Directory.Exists(directory.FullName));
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

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticResponseHandler(string.Empty));
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }

    public class ServerPathsProxy : DispatchProxy
    {
        private string _root = string.Empty;

        public static IServerApplicationPaths Create(string root)
        {
            var proxy = Create<IServerApplicationPaths, ServerPathsProxy>();
            ((ServerPathsProxy)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType == typeof(string) ? _root : null;
    }
}
