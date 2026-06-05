using System.Reflection;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.YtdlArchive;
using Jellyfin.Plugin.YtdlArchive.Configuration;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class DownloaderHostedServiceTests
{
    private static readonly object PluginLock = new();

    public DownloaderHostedServiceTests()
    {
        EnsurePluginToken(Path.GetTempPath());
    }

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
    public void IsRoute_MatchesMethodCaseInsensitivelyAndPathExactly()
    {
        Assert.True(InvokeStatic<bool>("IsRoute", "get", "/ping", "GET", "/ping"));
        Assert.False(InvokeStatic<bool>("IsRoute", "GET", "/Ping", "GET", "/ping"));
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
    public void FindFfmpeg_UsesConfiguredExecutableWhenItExists()
    {
        var directory = Directory.CreateTempSubdirectory();
        var original = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG_PATH");
        try
        {
            var ffmpeg = Path.Combine(directory.FullName, "ffmpeg");
            File.WriteAllText(ffmpeg, string.Empty);
            Environment.SetEnvironmentVariable("JELLYFIN_FFMPEG_PATH", ffmpeg);

            var result = InvokeStatic<string>("FindFfmpeg");

            Assert.Equal(ffmpeg, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JELLYFIN_FFMPEG_PATH", original);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_FinalizesM4bWhenAudioBookDownloadSucceeds()
    {
        var directory = Directory.CreateTempSubdirectory();
        var original = Environment.GetEnvironmentVariable("YTDL_AUDIOBOOK_DOWNLOAD_DIR");
        try
        {
            Environment.SetEnvironmentVariable("YTDL_AUDIOBOOK_DOWNLOAD_DIR", directory.FullName);
            var sourcePath = Path.Combine(directory.FullName, "book.m4a");
            File.WriteAllText(sourcePath, string.Empty);
            var ytdlp = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "yt-dlp.cmd" : "yt-dlp");
            File.WriteAllText(
                ytdlp,
                OperatingSystem.IsWindows()
                    ? "@echo off\r\necho Audio Book\r\n"
                    : "#!/bin/sh\necho Audio Book\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(ytdlp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var service = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager: null!,
                libraryReconciler: null!,
                subscriptions: null!);
            typeof(DownloaderHostedService)
                .GetField("_ytdlpPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, ytdlp);

            await InvokeInstance<Task>(
                service,
                "RunDownloadAsync",
                "https://youtu.be/dQw4w9WgXcQ",
                "audio",
                "m4b",
                "audiobook",
                null,
                CancellationToken.None);

            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(Path.ChangeExtension(sourcePath, ".m4b")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTDL_AUDIOBOOK_DOWNLOAD_DIR", original);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StopAsync_IgnoresDisposedListener()
    {
        var service = new DownloaderHostedService(
            NullLogger<DownloaderHostedService>.Instance,
            ytdlpManager: null!,
            libraryReconciler: null!,
            subscriptions: null!);
        var listener = new HttpListener();
        listener.Close();
        typeof(DownloaderHostedService)
            .GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, listener);

        await service.StopAsync(CancellationToken.None);

        Assert.True(true);
    }

    [Theory]
    [InlineData("GET", "/ping", null)]
    [InlineData("GET", "/save-types", null)]
    [InlineData("GET", "/status", null)]
    [InlineData("POST", "/download", """{"url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","quality":"audio","audioFormat":"flac"}""")]
    [InlineData("POST", "/directories", """{"parent":"","name":""}""")]
    [InlineData("POST", "/extension/config", "{}")]
    [InlineData("GET", "/extension/zip", null)]
    [InlineData("POST", "/libraries/reconcile", "{}")]
    public async Task HandleAsync_RoutesKnownApiRequests(string method, string path, string? body)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            EnsurePluginToken(directory.FullName);
            var service = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                new YtdlpManager(
                    new StaticHttpClientFactory(),
                    ServerPathsProxy.Create(directory.FullName),
                    NullLogger<YtdlpManager>.Instance),
                libraryReconciler: null!,
                subscriptions: null!);
            typeof(DownloaderHostedService)
                .GetField("_ytdlpPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "yt-dlp");

            var (statusCode, responseBody) = await SendToHandlerAsync(service, method, path, body);

            Assert.NotEqual(404, statusCode);
            Assert.NotEqual(string.Empty, responseBody);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ListenerPrefixes_UsesLocalhostWhenLanAccessIsDisabled()
    {
        EnsurePluginToken(Path.GetTempPath());
        Plugin.Instance!.Configuration.EnableLanBrowserAccess = false;

        var prefixes = InvokeStatic<IEnumerable<string>>("ListenerPrefixes").ToArray();

        Assert.Equal(["http://localhost:9876/"], prefixes);
    }

    [Fact]
    public void EffectiveAdvertisedServerUrl_UsesConfiguredUrlWithoutTrailingSlashes()
    {
        EnsurePluginToken(Path.GetTempPath());
        Plugin.Instance!.Configuration.EnableLanBrowserAccess = false;
        Plugin.Instance.Configuration.AdvertisedServerUrl = " http://192.168.1.50:9876/// ";

        var result = InvokeStatic<string>("EffectiveAdvertisedServerUrl");

        Assert.Equal("http://192.168.1.50:9876", result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("chrome-extension://abcdef", true)]
    [InlineData("moz-extension://abcdef", true)]
    [InlineData("https://example.com", false)]
    [InlineData("not a url", false)]
    public void IsBrowserTokenPairingOrigin_AllowsOnlyExtensionOrEmptyOrigins(string? origin, bool expected)
    {
        var result = InvokeStatic<bool>("IsBrowserTokenPairingOrigin", origin);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("chrome-extension://abcdef", 200)]
    [InlineData("https://example.com", 403)]
    public async Task SendBrowserTokenAsync_RequiresBrowserExtensionOrigin(string origin, int expectedStatus)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            EnsurePluginToken(directory.FullName);
            var service = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager: null!,
                libraryReconciler: null!,
                subscriptions: null!);

            var (statusCode, body) = await SendToHandlerAsync(service, "GET", "/browser-token", null, origin);

            Assert.Equal(expectedStatus, statusCode);
            Assert.NotEqual(string.Empty, body);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_SubscriptionRoutesListAndCreateSubscriptions()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            EnsurePluginToken(directory.FullName);
            var ytdlpManager = await CreateYtdlpManagerAsync(directory.FullName, """
                #!/bin/sh
                case "$*" in
                  *--skip-download*) printf '%s\n' '{"channel_id":"UC1234567890123456789012","channel":"Test Channel","channel_url":"https://www.youtube.com/channel/UC1234567890123456789012"}' ;;
                  *) printf '%s\n' '{"entries":[{"id":"video-new-1","title":"Newest"}]}' ;;
                esac
                """);
            var subscriptions = new ChannelSubscriptionManager(
                ytdlpManager,
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<ChannelSubscriptionManager>.Instance);
            var service = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager,
                libraryReconciler: null!,
                subscriptions);

            var (postStatus, postBody) = await SendToHandlerAsync(
                service,
                "POST",
                "/subscriptions",
                """{"url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","quality":"audio","audioFormat":"mp3","target":"music","downloadExistingVideos":true}""");
            var (getStatus, getBody) = await SendToHandlerAsync(service, "GET", "/subscriptions", null);

            Assert.Equal(200, postStatus);
            Assert.Contains("\"subscribed\":true", postBody, StringComparison.Ordinal);
            Assert.Contains("\"queuedExisting\":[", postBody, StringComparison.Ordinal);
            Assert.Contains("\"queued\":true", postBody, StringComparison.Ordinal);
            Assert.Equal(200, getStatus);
            Assert.Contains("Test Channel", getBody, StringComparison.Ordinal);
            Assert.Contains("subscriptions.json", getBody, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("null", "Invalid subscription request")]
    [InlineData("""{"url":"https://example.com/watch?v=dQw4w9WgXcQ","target":"other"}""", "Only YouTube URLs")]
    public async Task HandleAsync_SubscriptionRouteReportsBadRequests(string body, string expected)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            EnsurePluginToken(directory.FullName);
            var ytdlpManager = new YtdlpManager(
                new StaticHttpClientFactory(),
                ServerPathsProxy.Create(directory.FullName),
                NullLogger<YtdlpManager>.Instance);
            var service = new DownloaderHostedService(
                NullLogger<DownloaderHostedService>.Instance,
                ytdlpManager,
                libraryReconciler: null!,
                new ChannelSubscriptionManager(
                    ytdlpManager,
                    ApplicationPathsProxy.Create(directory.FullName),
                    NullLogger<ChannelSubscriptionManager>.Instance));

            var (status, responseBody) = await SendToHandlerAsync(service, "POST", "/subscriptions", body);

            Assert.Equal(400, status);
            Assert.Contains(expected, responseBody, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadDuration_ReturnsZeroForMalformedSidecar()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "bad.m4a");
            File.WriteAllText(mediaPath, string.Empty);
            File.WriteAllText(Path.ChangeExtension(mediaPath, ".info.json"), "{");

            var result = InvokeStatic<TimeSpan>("ReadDuration", mediaPath);

            Assert.Equal(TimeSpan.Zero, result);
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

    private static T InvokeInstance<T>(object instance, string name, params object?[] args)
    {
        var method = typeof(DownloaderHostedService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(instance, args)!;
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

    private static void EnsurePluginToken(string root)
    {
        lock (PluginLock)
        {
            if (Plugin.Instance is null)
            {
                _ = new Plugin(ApplicationPathsProxy.Create(root), XmlSerializerProxy.Create());
            }

            var plugin = Plugin.Instance!;
            if (plugin.Configuration is null)
            {
                typeof(BasePlugin<PluginConfiguration>)
                    .GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(plugin, new PluginConfiguration());
            }

            plugin.Configuration!.BrowserApiToken = "test-token";
        }
    }

    private static async Task<(int StatusCode, string Body)> SendToHandlerAsync(
        DownloaderHostedService service,
        string method,
        string path,
        string? body,
        string? origin = null)
    {
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 50000);
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var contextTask = listener.GetContextAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://localhost:{port}{path}");
        request.Headers.Add("X-YtdlArchive-Token", "test-token");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body);
        }

        var responseTask = client.SendAsync(request);
        var context = await contextTask;
        await InvokeInstance<Task>(service, "HandleAsync", context, CancellationToken.None);
        var response = await responseTask;
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
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

    public class XmlSerializerProxy : DispatchProxy
    {
        public static IXmlSerializer Create()
            => Create<IXmlSerializer, XmlSerializerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
