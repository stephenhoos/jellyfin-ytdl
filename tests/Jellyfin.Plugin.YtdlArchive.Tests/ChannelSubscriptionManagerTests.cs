using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class ChannelSubscriptionManagerTests
{
    [Fact]
    public async Task SubscribeAsync_SavesChannelWithRecentVideosMarkedSeen()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var ytdlp = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "yt-dlp.cmd" : "yt-dlp");
            await File.WriteAllTextAsync(
                ytdlp,
                OperatingSystem.IsWindows()
                    ? """
                      @echo off
                      echo %* | find "--skip-download" >nul
                      if %errorlevel%==0 (
                        echo {"channel_id":"UC1234567890123456789012","channel":"Test Channel","channel_url":"https://www.youtube.com/channel/UC1234567890123456789012"}
                      ) else (
                        echo {"entries":[{"id":"video-new-1","title":"Newest"},{"id":"video-new-2","title":"Second"}]}
                      )
                      """
                    : """
                      #!/bin/sh
                      case "$*" in
                        *--skip-download*) printf '%s\n' '{"channel_id":"UC1234567890123456789012","channel":"Test Channel","channel_url":"https://www.youtube.com/channel/UC1234567890123456789012"}' ;;
                        *) printf '%s\n' '{"entries":[{"id":"video-new-1","title":"Newest"},{"id":"video-new-2","title":"Second"}]}' ;;
                      esac
                      """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(ytdlp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var ytdlpManager = new YtdlpManager(
                new StaticHttpClientFactory(),
                ServerPathsProxy.Create(directory.FullName),
                NullLogger<YtdlpManager>.Instance);
            typeof(YtdlpManager)
                .GetField("_resolvedPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(ytdlpManager, ytdlp);

            var manager = new ChannelSubscriptionManager(
                ytdlpManager,
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<ChannelSubscriptionManager>.Instance);

            var subscription = await manager.SubscribeAsync(
                new SubscriptionRequest
                {
                    Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    Quality = "audio",
                    AudioFormat = "mp3",
                    Target = "music"
                },
                CancellationToken.None);
            var stored = await manager.ListAsync(CancellationToken.None);

            Assert.Equal("UC1234567890123456789012", subscription.ChannelId);
            Assert.Equal("Test Channel", subscription.ChannelName);
            Assert.Equal("music", subscription.Target);
            Assert.Equal(["video-new-1", "video-new-2"], subscription.SeenVideoIds);
            Assert.Single(stored);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubscribeAsync_RejectsNonYoutubeUrls()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var manager = new ChannelSubscriptionManager(
                new YtdlpManager(
                    new StaticHttpClientFactory(),
                    ServerPathsProxy.Create(directory.FullName),
                    NullLogger<YtdlpManager>.Instance),
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<ChannelSubscriptionManager>.Instance);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => manager.SubscribeAsync(
                new SubscriptionRequest
                {
                    Url = "https://example.com/watch?v=dQw4w9WgXcQ",
                    Target = "other"
                },
                CancellationToken.None));

            Assert.Contains("Only YouTube URLs", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PollingService_QueuesNewVideosAndMarksThemSeen()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var ytdlpManager = await CreateYtdlpManagerAsync(directory.FullName, """
                #!/bin/sh
                case "$*" in
                  *--flat-playlist*) printf '%s\n' '{"entries":[{"id":"old-video-1","title":"Old"},{"id":"new-video-1","title":"New"}]}' ;;
                  *) printf '%s\n' 'Downloaded subscribed video' ;;
                esac
                """);
            var manager = new ChannelSubscriptionManager(
                ytdlpManager,
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<ChannelSubscriptionManager>.Instance);
            await WriteSubscriptionStoreAsync(directory.FullName, new ChannelSubscription
            {
                ChannelId = "UC1234567890123456789012",
                ChannelName = "Test Channel",
                ChannelUrl = "https://www.youtube.com/channel/UC1234567890123456789012/videos",
                Quality = "best",
                Target = "other",
                SeenVideoIds = ["old-video-1"]
            });
            var downloader = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager,
                libraryReconciler: null!,
                subscriptions: manager);
            var polling = new SubscriptionPollingHostedService(
                manager,
                downloader,
                NullLogger<SubscriptionPollingHostedService>.Instance);

            await InvokePollingCheckAsync(polling);
            await Task.Delay(100);

            var subscription = Assert.Single(await manager.ListAsync(CancellationToken.None));
            Assert.Contains("new-video-1", subscription.SeenVideoIds);
            Assert.Contains("old-video-1", subscription.SeenVideoIds);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PollingService_DoesNotMarkVideosSeenWhenQueueFails()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var ytdlpManager = await CreateYtdlpManagerAsync(directory.FullName, """
                #!/bin/sh
                printf '%s\n' '{"entries":[{"id":"new-video-2","title":"New"}]}'
                """);
            var manager = new ChannelSubscriptionManager(
                ytdlpManager,
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<ChannelSubscriptionManager>.Instance);
            await WriteSubscriptionStoreAsync(directory.FullName, new ChannelSubscription
            {
                ChannelId = "UC1234567890123456789012",
                ChannelName = "Test Channel",
                ChannelUrl = "https://www.youtube.com/channel/UC1234567890123456789012/videos",
                Quality = "4k",
                Target = "other"
            });
            var downloader = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager,
                libraryReconciler: null!,
                subscriptions: manager);
            var polling = new SubscriptionPollingHostedService(
                manager,
                downloader,
                NullLogger<SubscriptionPollingHostedService>.Instance);

            await InvokePollingCheckAsync(polling);

            var subscription = Assert.Single(await manager.ListAsync(CancellationToken.None));
            Assert.DoesNotContain("new-video-2", subscription.SeenVideoIds);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<YtdlpManager> CreateYtdlpManagerAsync(string root, string script)
    {
        var ytdlp = Path.Combine(root, OperatingSystem.IsWindows() ? "yt-dlp.cmd" : "yt-dlp");
        await File.WriteAllTextAsync(
            ytdlp,
            OperatingSystem.IsWindows()
                ? script.Replace("#!/bin/sh", "@echo off", StringComparison.Ordinal)
                : script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ytdlp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var ytdlpManager = new YtdlpManager(
            new StaticHttpClientFactory(),
            ServerPathsProxy.Create(root),
            NullLogger<YtdlpManager>.Instance);
        typeof(YtdlpManager)
            .GetField("_resolvedPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(ytdlpManager, ytdlp);
        return ytdlpManager;
    }

    private static async Task WriteSubscriptionStoreAsync(string root, ChannelSubscription subscription)
    {
        var directory = Path.Combine(root, "data", "ytdlarchive");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "subscriptions.json"),
            $$"""
              {
                "subscriptions": [
                  {
                    "channelId": "{{subscription.ChannelId}}",
                    "channelName": "{{subscription.ChannelName}}",
                    "channelUrl": "{{subscription.ChannelUrl}}",
                    "quality": "{{subscription.Quality}}",
                    "target": "{{subscription.Target}}",
                    "seenVideoIds": {{JsonSerializer.Serialize(subscription.SeenVideoIds)}}
                  }
                ]
              }
              """);
    }

    private static Task InvokePollingCheckAsync(SubscriptionPollingHostedService polling)
    {
        var method = typeof(SubscriptionPollingHostedService).GetMethod("CheckSubscriptionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CheckSubscriptionsAsync was not found.");
        return (Task)method.Invoke(polling, [CancellationToken.None])!;
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new();
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

    public class ApplicationPathsProxy : DispatchProxy
    {
        private string _root = string.Empty;

        public static IApplicationPaths Create(string root)
        {
            var proxy = Create<IApplicationPaths, ApplicationPathsProxy>();
            ((ApplicationPathsProxy)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType == typeof(string) ? _root : null;
    }

}
