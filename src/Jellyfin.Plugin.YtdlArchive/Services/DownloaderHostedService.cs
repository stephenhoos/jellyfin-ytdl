using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YtdlArchive.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class DownloaderHostedService : BackgroundService
{
    private const int Port = 9876;
    private static readonly TimeSpan ListenerRestartDelay = TimeSpan.FromSeconds(5);
    private const string Format = "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best";
    private const string AudioQuality = "audio";
    private const string M4aFormat = "m4a";
    private const string M4bFormat = "m4b";
    private const string Mp3Format = "mp3";
    private const string OpusFormat = "opus";
    private const string ApplicationJsonContentType = "application/json";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> QualityFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["best"] = Format,
        ["1080"] = "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best",
        ["720"] = "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720][ext=mp4]/best",
        ["480"] = "bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/best[height<=480][ext=mp4]/best",
        [AudioQuality] = "bestaudio/best"
    };

    private static readonly HashSet<string> SupportedAudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        Mp3Format,
        M4aFormat,
        M4bFormat,
        OpusFormat
    };

    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be"
    };

    private static readonly SaveType[] SaveTypes =
    {
        new("Best to Other", "best", "★", ArchiveSettings.OtherTarget),
        new("1080p to Other", "1080", "HD", ArchiveSettings.OtherTarget),
        new("720p to Other", "720", "HD", ArchiveSettings.OtherTarget),
        new("480p to Other", "480", "SD", ArchiveSettings.OtherTarget),
        new("MP3 to Music", AudioQuality, "♫", ArchiveSettings.MusicTarget, Mp3Format),
        new("M4A to Music", AudioQuality, "♫", ArchiveSettings.MusicTarget, M4aFormat),
        new("Opus to Music", AudioQuality, "♫", ArchiveSettings.MusicTarget, OpusFormat),
        new("MP3 to Podcast", AudioQuality, "◉", ArchiveSettings.PodcastTarget, Mp3Format),
        new("M4A to Podcast", AudioQuality, "◉", ArchiveSettings.PodcastTarget, M4aFormat),
        new("M4B Audiobook", AudioQuality, "▣", ArchiveSettings.AudiobookTarget, M4bFormat),
        new("M4B Audiobook 10% chapters", AudioQuality, "▣", ArchiveSettings.AudiobookTarget, M4bFormat, 10),
        new("M4B Audiobook 20% chapters", AudioQuality, "▣", ArchiveSettings.AudiobookTarget, M4bFormat, 20),
        new("M4A to Audiobooks", AudioQuality, "▣", ArchiveSettings.AudiobookTarget, M4aFormat)
    };

    private readonly ILogger<DownloaderHostedService> _logger;
    private readonly YtdlpManager _ytdlpManager;
    private readonly LibraryReconciler _libraryReconciler;
    private readonly ConcurrentDictionary<string, DownloadStatus> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _libraryScanLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock;
    private string? _ytdlpPath;
    private string? _ytdlpVersion;
    private HttpListener? _listener;

    public DownloaderHostedService(
        ILogger<DownloaderHostedService> logger,
        YtdlpManager ytdlpManager,
        LibraryReconciler libraryReconciler)
    {
        _logger = logger;
        _ytdlpManager = ytdlpManager;
        _libraryReconciler = libraryReconciler;
        _downloadLock = new SemaphoreSlim(Math.Clamp(Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 2, 1, 6));
    }

    public static string DownloadDirectory => ArchiveSettings.VideoDownloadDirectory;

    public static string MusicDownloadDirectory => ArchiveSettings.MusicDownloadDirectory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureBrowserApiToken();
        await TryWriteBrowserExtensionConfigFileAsync(stoppingToken).ConfigureAwait(false);
        Directory.CreateDirectory(DownloadDirectory);
        Directory.CreateDirectory(MusicDownloadDirectory);
        _ytdlpPath = await _ytdlpManager.EnsureAsync(stoppingToken).ConfigureAwait(false);
        _ytdlpVersion = await _ytdlpManager.GetVersionAsync(stoppingToken).ConfigureAwait(false);
        if (_ytdlpPath is null)
        {
            _logger.LogWarning("YtdlArchive downloader server started without yt-dlp available");
        }
        else
        {
            _logger.LogInformation("YtdlArchive using yt-dlp {Version} at {Path}", _ytdlpVersion ?? "unknown version", _ytdlpPath);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "YtdlArchive downloader server stopped unexpectedly and will restart");
            }
            finally
            {
                CloseListener();
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ListenerRestartDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunListenerAsync(CancellationToken stoppingToken)
    {
        _listener = new HttpListener();
        var prefixes = ListenerPrefixes().ToArray();
        foreach (var prefix in prefixes)
        {
            _listener.Prefixes.Add(prefix);
        }

        _listener.Start();
        _logger.LogInformation("YtdlArchive downloader server listening on {Prefixes}", string.Join(", ", prefixes));

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, stoppingToken), CancellationToken.None);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            CloseListener();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "YtdlArchive listener was already disposed during shutdown");
        }

        return base.StopAsync(cancellationToken);
    }

    private void CloseListener()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        listener?.Stop();
        listener?.Close();
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        AddCorsHeaders(context.Request, context.Response);

        if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(context.Response, 200, new { }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath;
        if (IsRoute(method, path, "GET", "/browser-token"))
        {
            await SendBrowserTokenAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(context.Request))
        {
            await SendJsonAsync(context.Response, 401, new { error = "Missing or invalid YtdlArchive browser API token" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (IsRoute(method, path, "GET", "/ping"))
            {
                await SendJsonAsync(context.Response, 200, new
                {
                    ok = true,
                    embedded = true,
                    serverUrl = EffectiveAdvertisedServerUrl(),
                    lanBrowserAccess = Plugin.Instance?.Configuration.EnableLanBrowserAccess == true,
                    ytdlp = _ytdlpPath ?? "not found",
                    ytdlpVersion = _ytdlpVersion,
                    managedYtdlp = _ytdlpPath == _ytdlpManager.ManagedPath,
                    downloadDir = DownloadDirectory,
                    musicDownloadDir = MusicDownloadDirectory,
                    podcastDownloadDir = ArchiveSettings.PodcastDownloadDirectory,
                    audiobookDownloadDir = ArchiveSettings.AudiobookDownloadDirectory,
                    otherDownloadDir = ArchiveSettings.OtherDownloadDirectory,
                    jellyfin = new
                    {
                        enabled = true,
                        musicLibraryName = ArchiveSettings.MusicLibraryName,
                        musicLibraryType = "music",
                        podcastLibraryName = ArchiveSettings.PodcastLibraryName,
                        podcastLibraryType = "music",
                        audiobookLibraryName = ArchiveSettings.AudiobookLibraryName,
                        audiobookLibraryType = "books",
                        otherLibraryName = ArchiveSettings.OtherLibraryName,
                        otherLibraryType = "tvshows"
                    }
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "GET", "/save-types"))
            {
                await SendJsonAsync(context.Response, 200, new
                {
                    saveTypes = SaveTypes,
                    defaults = new
                    {
                        music = ArchiveSettings.MusicLibraryName,
                        podcast = ArchiveSettings.PodcastLibraryName,
                        audiobook = ArchiveSettings.AudiobookLibraryName,
                        other = ArchiveSettings.OtherLibraryName
                    }
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "GET", "/status"))
            {
                await SendJsonAsync(context.Response, 200, _active, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "POST", "/download"))
            {
                await QueueDownloadAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "POST", "/directories"))
            {
                await CreateDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "POST", "/extension/config"))
            {
                await WriteBrowserExtensionConfigAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRoute(method, path, "POST", "/libraries/reconcile"))
            {
                await ReconcileLibrariesAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendJsonAsync(context.Response, 404, new { error = "Not found" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YtdlArchive downloader request failed");
            if (context.Response.OutputStream.CanWrite)
            {
                await SendJsonAsync(context.Response, 500, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CreateDirectoryAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = body.RootElement;
        var parent = GetString(root, "parent")?.Trim();
        var name = GetString(root, "name")?.Trim();

        if (string.IsNullOrWhiteSpace(parent))
        {
            await SendJsonAsync(context.Response, 400, new { error = "Missing parent directory" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            await SendJsonAsync(context.Response, 400, new { error = "Missing folder name" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || name is "." or "..")
        {
            await SendJsonAsync(context.Response, 400, new { error = "Invalid folder name" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var parentFullPath = Path.GetFullPath(parent);
        if (!Directory.Exists(parentFullPath))
        {
            await SendJsonAsync(context.Response, 404, new { error = "Parent directory does not exist" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedDirectoryParent(parentFullPath))
        {
            await SendJsonAsync(context.Response, 403, new { error = "Folders can only be created inside configured YtdlArchive library roots or their parent media folder" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var newDirectory = Path.GetFullPath(Path.Combine(parentFullPath, name));
        var parentWithSeparator = parentFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!newDirectory.StartsWith(parentWithSeparator, StringComparison.Ordinal))
        {
            await SendJsonAsync(context.Response, 400, new { error = "Folder must be created inside the selected directory" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(newDirectory);
        await SendJsonAsync(context.Response, 200, new { path = newDirectory }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileLibrariesAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        await ReconcileAndRefreshLibraryAsync(cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(context.Response, 200, new
        {
            ok = true,
            musicDownloadDir = ArchiveSettings.MusicDownloadDirectory,
            podcastDownloadDir = ArchiveSettings.PodcastDownloadDirectory,
            audiobookDownloadDir = ArchiveSettings.AudiobookDownloadDirectory,
            otherDownloadDir = ArchiveSettings.OtherDownloadDirectory
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueDownloadAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = body.RootElement;
        var url = GetString(root, "url")?.Trim();
        var quality = GetString(root, "quality") ?? "best";
        var audioFormat = GetString(root, "audioFormat");
        var target = ArchiveSettings.NormalizeTarget(GetString(root, "target"));
        var chapterPercent = GetInt(root, "chapterPercent");

        if (string.IsNullOrWhiteSpace(url))
        {
            await SendJsonAsync(context.Response, 400, new { error = "Missing url" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedDownloadUrl(url))
        {
            await SendJsonAsync(context.Response, 400, new { error = "Only YouTube URLs are allowed by default" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_ytdlpPath is null)
        {
            await SendJsonAsync(context.Response, 500, new { error = "yt-dlp not found. Install it with: pip install yt-dlp" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!QualityFormats.ContainsKey(quality))
        {
            await SendJsonAsync(context.Response, 400, new { error = $"Unsupported quality: {quality}" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (quality.Equals(AudioQuality, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(audioFormat)
            && !SupportedAudioFormats.Contains(audioFormat))
        {
            await SendJsonAsync(context.Response, 400, new { error = $"Unsupported audio format: {audioFormat}" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (target.Length == 0)
        {
            await SendJsonAsync(context.Response, 400, new { error = "Unsupported target" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (chapterPercent is not null && chapterPercent is not (10 or 20))
        {
            await SendJsonAsync(context.Response, 400, new { error = "chapterPercent must be 10 or 20" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_active.TryGetValue(url, out var existing) && existing.Status is "downloading" or "queued")
        {
            await SendJsonAsync(context.Response, 200, new { queued = false, reason = "already downloading" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _active[url] = new DownloadStatus("queued", string.Empty, null, target);
        _ = Task.Run(() => RunDownloadAsync(url, quality, audioFormat, target, chapterPercent, CancellationToken.None), CancellationToken.None);

        await SendJsonAsync(context.Response, 200, new
        {
            queued = true,
            saveTo = ArchiveDirectoryForTarget(target),
            target
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunDownloadAsync(string url, string quality, string? audioFormat, string target, int? chapterPercent, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _active[url] = new DownloadStatus("downloading", string.Empty, null, target);
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytdlpPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var arg in BuildYtdlpArguments(url, quality, audioFormat, target))
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start yt-dlp.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var title = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? url;

            if (process.ExitCode == 0)
            {
                if (quality.Equals(AudioQuality, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(audioFormat, M4bFormat, StringComparison.OrdinalIgnoreCase))
                {
                    await FinalizeM4bAsync(target, startedUtc, chapterPercent, cancellationToken).ConfigureAwait(false);
                }

                _active[url] = new DownloadStatus("done", title, null, target);
                _logger.LogInformation("YtdlArchive downloaded {Title} to {Target}", title, target);
                await ReconcileAndRefreshLibraryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var error = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "Unknown error";
                _active[url] = new DownloadStatus("error", title, error, target);
                _logger.LogWarning("YtdlArchive download failed: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _active[url] = new DownloadStatus("error", string.Empty, ex.Message, target);
            _logger.LogError(ex, "YtdlArchive download failed");
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private static IEnumerable<string> BuildYtdlpArguments(string url, string quality, string? audioFormat, string target)
    {
        var archiveDirectory = ArchiveDirectoryForTarget(target);
        var outputTemplate = Path.Combine(
            archiveDirectory,
            "%(channel,uploader|Unknown_Channel).200B [%(channel_id|unknown-channel)s]",
            "%(upload_date>%Y-%m-%d,release_date>%Y-%m-%d|NA)s - %(title).200B [%(id)s].%(ext)s");

        yield return "-f";
        yield return QualityFormats[quality];
        yield return "--output";
        yield return outputTemplate;
        yield return "--no-playlist";
        yield return "--write-info-json";
        yield return "--write-thumbnail";
        yield return "--no-mtime";
        yield return "--restrict-filenames";
        yield return "--print";
        yield return "before_dl:%(title)s";

        if (quality.Equals(AudioQuality, StringComparison.OrdinalIgnoreCase))
        {
            yield return "--extract-audio";
            yield return "--audio-format";
            yield return ResolveAudioFormat(audioFormat);
            yield return "--audio-quality";
            yield return "0";
            yield return "--embed-metadata";
        }
        else
        {
            yield return "--merge-output-format";
            yield return "mp4";
        }

        yield return url;
    }

    private static string ResolveAudioFormat(string? audioFormat)
    {
        if (string.Equals(audioFormat, M4bFormat, StringComparison.OrdinalIgnoreCase))
        {
            return M4aFormat;
        }

        return string.IsNullOrWhiteSpace(audioFormat) ? Mp3Format : audioFormat;
    }

    private static string ArchiveDirectoryForTarget(string target)
        => ArchiveSettings.DownloadDirectoryForTarget(target);

    private async Task FinalizeM4bAsync(string target, DateTime startedUtc, int? chapterPercent, CancellationToken cancellationToken)
    {
        var sourcePath = FindNewestM4a(ArchiveDirectoryForTarget(target), startedUtc);
        if (sourcePath is null)
        {
            _logger.LogWarning("YtdlArchive could not find the extracted m4a to finalize as m4b");
            return;
        }

        var destinationPath = Path.ChangeExtension(sourcePath, ".m4b");
        if (chapterPercent is null)
        {
            File.Move(sourcePath, destinationPath, true);
            return;
        }

        var metadataPath = Path.Combine(Path.GetTempPath(), $"ytdlarchive-{Guid.NewGuid():N}.ffmetadata");
        var tempPath = Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(sourcePath)}.tmp.m4b");
        try
        {
            await File.WriteAllTextAsync(metadataPath, BuildChapterMetadata(sourcePath, chapterPercent.Value), cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = FindFfmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(metadataPath);
            startInfo.ArgumentList.Add("-map_metadata");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-codec");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add(tempPath);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffmpeg.");
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                File.Move(tempPath, destinationPath, true);
                File.Delete(sourcePath);
            }
            else
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                _logger.LogWarning("YtdlArchive could not add m4b chapters: {Error}", stderr);
                File.Move(sourcePath, destinationPath, true);
            }
        }
        finally
        {
            TryDelete(metadataPath);
            TryDelete(tempPath);
        }
    }

    private static string? FindNewestM4a(string archiveDirectory, DateTime startedUtc)
    {
        if (!Directory.Exists(archiveDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(archiveDirectory, "*.m4a", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= startedUtc.AddMinutes(-2))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private static string BuildChapterMetadata(string mediaPath, int chapterPercent)
    {
        var duration = ReadDuration(mediaPath);
        var title = Path.GetFileNameWithoutExtension(mediaPath);
        var lines = new List<string>
        {
            ";FFMETADATA1",
            $"title={EscapeMetadata(title)}"
        };

        if (duration <= TimeSpan.Zero)
        {
            return string.Join('\n', lines);
        }

        var chapterCount = 100 / chapterPercent;
        var durationMs = (long)duration.TotalMilliseconds;
        for (var index = 0; index < chapterCount; index++)
        {
            var start = durationMs * index / chapterCount;
            var end = index == chapterCount - 1 ? durationMs : durationMs * (index + 1) / chapterCount;
            lines.Add("[CHAPTER]");
            lines.Add("TIMEBASE=1/1000");
            lines.Add($"START={start}");
            lines.Add($"END={end}");
            lines.Add($"title={chapterPercent * index}%");
        }

        return string.Join('\n', lines);
    }

    private static TimeSpan ReadDuration(string mediaPath)
    {
        var infoPath = Path.ChangeExtension(mediaPath, ".info.json");
        if (!File.Exists(infoPath))
        {
            return TimeSpan.Zero;
        }

        try
        {
            using var stream = File.OpenRead(infoPath);
            var info = JsonSerializer.Deserialize<YtdlpInfoJson>(stream, WebJsonOptions);
            return info?.DurationSeconds is > 0 ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : TimeSpan.Zero;
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
        catch (JsonException)
        {
            return TimeSpan.Zero;
        }
        catch (UnauthorizedAccessException)
        {
            return TimeSpan.Zero;
        }
    }

    private static string FindFfmpeg()
    {
        var jellyfinFfmpeg = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG_PATH")
            ?? Path.Combine(Path.DirectorySeparatorChar.ToString(), "Applications", "Jellyfin.app", "Contents", "MacOS", "ffmpeg");
        return File.Exists(jellyfinFfmpeg) ? jellyfinFfmpeg : "ffmpeg";
    }

    private static string EscapeMetadata(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("\n", "\\\n", StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp file should not fail the download.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; file permissions may prevent deleting temporary output.
        }
    }

    private async Task ReconcileAndRefreshLibraryAsync(CancellationToken cancellationToken)
    {
        await _libraryScanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _libraryReconciler.EnsureConfiguredLibrariesAsync(cancellationToken).ConfigureAwait(false);
            await _libraryReconciler.RefreshLibraryAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("YtdlArchive queued Jellyfin library refresh");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YtdlArchive could not queue a library refresh");
        }
        finally
        {
            _libraryScanLock.Release();
        }
    }

    private static IEnumerable<string> ListenerPrefixes()
    {
        yield return BuildHttpServerUrl("localhost") + "/";

        if (Plugin.Instance?.Configuration.EnableLanBrowserAccess != true)
        {
            yield break;
        }

        foreach (var address in LanAddresses())
        {
            yield return BuildHttpServerUrl(address.ToString()) + "/";
        }
    }

    private static IEnumerable<IPAddress> LanAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private static string EffectiveAdvertisedServerUrl()
    {
        var configuration = Plugin.Instance?.Configuration;
        var configuredUrl = configuration?.AdvertisedServerUrl?.Trim();
        if (IsValidHttpServerUrl(configuredUrl))
        {
            return TrimTrailingSlash(configuredUrl!);
        }

        if (configuration?.EnableLanBrowserAccess == true)
        {
            var address = LanAddresses().FirstOrDefault();
            if (address is not null)
            {
                return BuildHttpServerUrl(address.ToString());
            }
        }

        return BuildHttpServerUrl("localhost");
    }

    private static string BuildHttpServerUrl(string host)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttp, host, Port).Uri;
        return TrimTrailingSlash(uri.AbsoluteUri);
    }

    private static bool IsValidHttpServerUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host);

    private static string TrimTrailingSlash(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] == '/')
        {
            end--;
        }

        return value[..end];
    }

    private static async Task WriteBrowserExtensionConfigAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var result = await WriteBrowserExtensionConfigFileAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            await SendJsonAsync(context.Response, 503, new { error = "Browser API token is not available" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendJsonAsync(context.Response, 200, new
        {
            ok = true,
            serverUrl = result.Config.ServerUrl,
            extensionDirectory = result.ExtensionDirectory,
            configPath = result.ConfigPath
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteBrowserExtensionConfigFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await WriteBrowserExtensionConfigFileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The API can still run if the bundled extension folder is not writable.
        }
    }

    private static async Task<BrowserExtensionConfigWriteResult?> WriteBrowserExtensionConfigFileAsync(CancellationToken cancellationToken)
    {
        EnsureBrowserApiToken();
        var token = Plugin.Instance?.Configuration.BrowserApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var extensionDirectory = BrowserExtensionDirectory();
        Directory.CreateDirectory(extensionDirectory);
        var configPath = Path.Combine(extensionDirectory, "config.json");
        var extensionConfig = new BrowserExtensionConfig(EffectiveAdvertisedServerUrl(), token);
        await using (var stream = File.Create(configPath))
        {
            await JsonSerializer.SerializeAsync(stream, extensionConfig, WebJsonOptions, cancellationToken).ConfigureAwait(false);
        }

        return new BrowserExtensionConfigWriteResult(extensionConfig, extensionDirectory, configPath);
    }

    private static string BrowserExtensionDirectory()
    {
        var assemblyLocation = typeof(DownloaderHostedService).Assembly.Location;
        var pluginDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        return Path.Combine(pluginDirectory, "chrome-extension");
    }

    private static void EnsureBrowserApiToken()
    {
        var plugin = Plugin.Instance;
        var configuration = plugin?.Configuration;
        if (plugin is null || configuration is null || !string.IsNullOrWhiteSpace(configuration.BrowserApiToken))
        {
            return;
        }

        configuration.BrowserApiToken = CreateBrowserApiToken();
        plugin.SaveConfiguration(configuration);
    }

    private static string CreateBrowserApiToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task SendBrowserTokenAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context.Request))
        {
            await SendJsonAsync(context.Response, 403, new { error = "Browser token pairing is only available on the Jellyfin server computer" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsBrowserTokenPairingOrigin(context.Request.Headers["Origin"]))
        {
            await SendJsonAsync(context.Response, 403, new { error = "Browser token pairing is only available to browser extensions" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureBrowserApiToken();
        var token = Plugin.Instance?.Configuration.BrowserApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            await SendJsonAsync(context.Response, 503, new { error = "Browser API token is not available" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendJsonAsync(context.Response, 200, new { apiToken = token }, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLocalRequest(HttpListenerRequest request)
    {
        var remoteAddress = request.RemoteEndPoint?.Address;
        if (remoteAddress is null)
        {
            return true;
        }

        return IPAddress.IsLoopback(remoteAddress);
    }

    private static bool IsAuthorized(HttpListenerRequest request)
    {
        var expected = Plugin.Instance?.Configuration.BrowserApiToken;
        var supplied = request.Headers["X-YtdlArchive-Token"];
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        if (Plugin.Instance?.Configuration.AllowNonYouTubeDownloads == true)
        {
            return true;
        }

        return AllowedDownloadHosts.Contains(uri.Host);
    }

    private static bool IsAllowedDirectoryParent(string parentFullPath)
    {
        foreach (var root in ConfiguredArchiveRoots())
        {
            if (IsSameOrChildPath(parentFullPath, root))
            {
                return true;
            }

            var rootParent = Directory.GetParent(root)?.FullName;
            if (!string.IsNullOrWhiteSpace(rootParent) && SamePath(parentFullPath, rootParent))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ConfiguredArchiveRoots()
    {
        yield return ArchiveSettings.MusicDownloadDirectory;
        yield return ArchiveSettings.PodcastDownloadDirectory;
        yield return ArchiveSettings.AudiobookDownloadDirectory;
        yield return ArchiveSettings.OtherDownloadDirectory;
    }

    private static bool IsSameOrChildPath(string path, string possibleParent)
    {
        if (SamePath(path, possibleParent))
        {
            return true;
        }

        var normalizedParent = NormalizeDirectoryPath(possibleParent);
        return NormalizeDirectoryPath(path).StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            NormalizeDirectoryPath(left),
            NormalizeDirectoryPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeDirectoryPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void AddCorsHeaders(HttpListenerRequest request, HttpListenerResponse response)
    {
        var origin = request.Headers["Origin"];
        if (IsAllowedCorsOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin!;
            response.Headers["Vary"] = "Origin";
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-YtdlArchive-Token";
    }

    private static bool IsRoute(string actualMethod, string? actualPath, string expectedMethod, string expectedPath)
        => actualMethod.Equals(expectedMethod, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actualPath, expectedPath, StringComparison.Ordinal);

    private static bool IsAllowedCorsOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (origin is "null")
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "chrome-extension" or "moz-extension"
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))
            || ((uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && AllowedDownloadHosts.Contains(uri.Host));
    }

    private static bool IsBrowserTokenPairingOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "chrome-extension" or "moz-extension";
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WebJsonOptions);
        response.StatusCode = statusCode;
        response.ContentType = ApplicationJsonContentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private sealed record DownloadStatus(string Status, string Title, string? Error, string Target);

    private sealed record SaveType(string Label, string Quality, string Icon, string Target, string? AudioFormat = null, int? ChapterPercent = null);

    private sealed record BrowserExtensionConfig(string ServerUrl, string ApiToken);

    private sealed record BrowserExtensionConfigWriteResult(BrowserExtensionConfig Config, string ExtensionDirectory, string ConfigPath);
}
