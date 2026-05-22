using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed class YtdlpManager
{
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
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";

    private static string DownloadAssetName
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "yt-dlp.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "yt-dlp_macos"
                : "yt-dlp";

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

            _resolvedPath = FindOnPath("yt-dlp")
                ?? FirstExisting(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "yt-dlp"),
                    "/usr/local/bin/yt-dlp",
                    "/opt/homebrew/bin/yt-dlp");
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

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
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
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";
        }

        return "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";
    }

    private static async Task<string?> GetExpectedSha256Async(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS",
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
