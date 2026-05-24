using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class YtdlpManager
{
    private const string YtdlpExecutableName = "yt-dlp";
    private const string YtdlpWindowsExecutableName = "yt-dlp.exe";
    private const string YtdlpMacosAssetName = "yt-dlp_macos";
    private const string ReleaseDownloadBaseEnvironmentVariable = "YTDLP_RELEASE_DOWNLOAD_BASE";
    private const string HttpsScheme = "https";
    private const string GitHubHost = "github.com";
    private const string YtdlpReleaseDownloadPath = "yt-dlp/yt-dlp/releases/latest/download";
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILogger<YtdlpManager> _logger;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private string? _resolvedPath;

    public YtdlpManager(
        IHttpClientFactory httpClientFactory,
        IServerApplicationPaths applicationPaths,
        ILogger<YtdlpManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public string ManagedDirectory => Path.Combine(_applicationPaths.PluginsPath, "Jellyfin.Plugin.YtdlArchive", "tools");

    public string ManagedPath => Path.Combine(ManagedDirectory, ManagedExecutableName);

    private static string ManagedExecutableName
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? YtdlpWindowsExecutableName : YtdlpExecutableName;

    private static string DownloadAssetName => GetDownloadAssetName();

    public async Task<string?> EnsureAsync(CancellationToken cancellationToken)
    {
        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            var configuredPath = configuration?.YtdlpPath;
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                _resolvedPath = configuredPath;
                return _resolvedPath;
            }

            if (configuration?.AutoInstallYtdlp != false)
            {
                await EnsureManagedBinaryAsync(configuration?.AutoUpdateYtdlp != false, cancellationToken).ConfigureAwait(false);
                if (File.Exists(ManagedPath))
                {
                    _resolvedPath = ManagedPath;
                    return _resolvedPath;
                }
            }

            _resolvedPath = FindOnPath(YtdlpExecutableName)
                ?? FirstExisting(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", YtdlpExecutableName),
                    Path.Combine(Path.DirectorySeparatorChar.ToString(), "usr", "local", "bin", YtdlpExecutableName),
                    Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "homebrew", "bin", YtdlpExecutableName));
            return _resolvedPath;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var path = _resolvedPath ?? await EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(VersionCheckTimeout);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillProcess(process);
                _logger.LogWarning("Timed out reading yt-dlp version after {TimeoutSeconds} seconds", VersionCheckTimeout.TotalSeconds);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read yt-dlp version");
            return null;
        }
    }

    private async Task EnsureManagedBinaryAsync(bool update, CancellationToken cancellationToken)
    {
        if (!update && File.Exists(ManagedPath))
        {
            return;
        }

        Directory.CreateDirectory(ManagedDirectory);
        var downloadUrl = GetDownloadUrl();
        var tempPath = ManagedPath + ".download";

        try
        {
            _logger.LogInformation("Downloading yt-dlp from {Url}", downloadUrl);
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var expectedSha256 = await GetExpectedSha256Async(client, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(expectedSha256) || !await MatchesSha256Async(tempPath, expectedSha256, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Downloaded yt-dlp binary did not match the published SHA-256 checksum.");
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.Move(tempPath, ManagedPath, overwrite: true);
            _logger.LogInformation("Installed managed yt-dlp at {Path}", ManagedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install or update managed yt-dlp");
            TryDelete(tempPath);
        }
    }

    private static string GetDownloadUrl()
        => $"{ReleaseDownloadBase}/{DownloadAssetName}";

    private static string GetDownloadAssetName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return YtdlpWindowsExecutableName;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return YtdlpMacosAssetName;
        }

        return YtdlpExecutableName;
    }

    private static string ReleaseDownloadBase
        => Environment.GetEnvironmentVariable(ReleaseDownloadBaseEnvironmentVariable)?.TrimEnd('/')
            ?? BuildDefaultReleaseDownloadBase();

    private static string BuildDefaultReleaseDownloadBase()
        => new UriBuilder(HttpsScheme, GitHubHost)
        {
            Path = YtdlpReleaseDownloadPath
        }.Uri.ToString().TrimEnd('/');

    private static async Task<string?> GetExpectedSha256Async(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"{ReleaseDownloadBase}/SHA2-256SUMS",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var checksums = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var fileName = DownloadAssetName;
        foreach (var line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1], fileName, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static async Task<bool> MatchesSha256Async(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FirstExisting(params string[] paths)
        => paths.FirstOrDefault(File.Exists);

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and Kill.
        }
    }

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
            // Cleanup is best-effort; a failed update can retry on the next startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best-effort; permissions may block deleting the partial download.
        }
    }
}
