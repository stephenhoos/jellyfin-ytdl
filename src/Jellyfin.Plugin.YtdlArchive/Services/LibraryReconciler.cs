using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtdlArchive.Services;

public sealed partial class LibraryReconciler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<LibraryReconciler> _logger;

    public LibraryReconciler(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        ILogger<LibraryReconciler> logger)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public async Task EnsureConfiguredLibrariesAsync(CancellationToken cancellationToken)
    {
        ScrubAppleDoubleFiles();
        Directory.CreateDirectory(ArchiveSettings.MusicDownloadDirectory);
        Directory.CreateDirectory(ArchiveSettings.PodcastDownloadDirectory);
        Directory.CreateDirectory(ArchiveSettings.AudiobookDownloadDirectory);
        Directory.CreateDirectory(ArchiveSettings.OtherDownloadDirectory);

        if (!ArchiveSettings.EnsureLibrariesOnStartup)
        {
            _logger.LogInformation("YtdlArchive library bootstrap is disabled");
            return;
        }

        if (ArchiveSettings.EnsureMusicLibrary)
        {
            await EnsureLibraryAsync(
                ArchiveSettings.MusicLibraryName,
                ArchiveSettings.MusicDownloadDirectory,
                CollectionTypeOptions.music,
                KnownManagedPaths(ArchiveSettings.DefaultMusicDownloadDirectory),
                ArchiveSettings.EnableRemoteMetadata).ConfigureAwait(false);
        }

        if (ArchiveSettings.EnsurePodcastLibrary)
        {
            await EnsureLibraryAsync(
                ArchiveSettings.PodcastLibraryName,
                ArchiveSettings.PodcastDownloadDirectory,
                CollectionTypeOptions.music,
                KnownManagedPaths(ArchiveSettings.DefaultPodcastDownloadDirectory),
                ArchiveSettings.EnableRemoteMetadata).ConfigureAwait(false);
        }

        if (ArchiveSettings.EnsureAudiobookLibrary)
        {
            await EnsureLibraryAsync(
                ArchiveSettings.AudiobookLibraryName,
                ArchiveSettings.AudiobookDownloadDirectory,
                CollectionTypeOptions.books,
                KnownManagedPaths(ArchiveSettings.DefaultAudiobookDownloadDirectory),
                ArchiveSettings.EnableRemoteMetadata).ConfigureAwait(false);
        }

        if (ArchiveSettings.EnsureOtherLibrary)
        {
            await EnsureLibraryAsync(
                ArchiveSettings.OtherLibraryName,
                ArchiveSettings.OtherDownloadDirectory,
                CollectionTypeOptions.tvshows,
                KnownManagedPaths(ArchiveSettings.DefaultOtherDownloadDirectory),
                false).ConfigureAwait(false);
        }
    }

    public async Task RefreshLibraryAsync(CancellationToken cancellationToken)
        => await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);

    private async Task EnsureLibraryAsync(
        string libraryName,
        string path,
        CollectionTypeOptions collectionType,
        HashSet<string> replaceablePaths,
        bool enableInternetProviders)
    {
        var desiredPath = Path.GetFullPath(path);
        NormalizeLibraryOptions(libraryName, enableInternetProviders);
        var existing = _libraryManager
            .GetVirtualFolders()
            .FirstOrDefault(folder => string.Equals(folder.Name, libraryName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var options = CreateLibraryOptions(desiredPath);
            await _libraryManager.AddVirtualFolder(libraryName, collectionType, options, false).ConfigureAwait(false);
            _logger.LogInformation("YtdlArchive created Jellyfin library {LibraryName} at {Path}", libraryName, desiredPath);
            return;
        }

        var locations = existing.Locations
            .Select(NormalizePathOrEmpty)
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .ToArray();
        var hasDesiredPath = locations.Any(location => SamePath(location, desiredPath));

        if (!hasDesiredPath)
        {
            _libraryManager.AddMediaPath(libraryName, new MediaPathInfo(desiredPath));
            _logger.LogInformation("YtdlArchive added {Path} to Jellyfin library {LibraryName}", desiredPath, libraryName);
        }

        foreach (var location in locations)
        {
            if (SamePath(location, desiredPath))
            {
                continue;
            }

            if (locations.Length == 1 || replaceablePaths.Contains(location))
            {
                _libraryManager.RemoveMediaPath(libraryName, location);
                _logger.LogInformation("YtdlArchive removed old managed path {Path} from Jellyfin library {LibraryName}", location, libraryName);
            }
        }
    }

    private static LibraryOptions CreateLibraryOptions(string path)
        => new()
        {
            Enabled = true,
            EnableRealtimeMonitor = true,
            PreferNonstandardArtistsTag = true,
            PathInfos = new[] { new MediaPathInfo(path) },
            MetadataSavers = Array.Empty<string>(),
            DisabledLocalMetadataReaders = Array.Empty<string>(),
            LocalMetadataReaderOrder = Array.Empty<string>(),
            DisabledSubtitleFetchers = Array.Empty<string>(),
            SubtitleFetcherOrder = Array.Empty<string>(),
            DisabledMediaSegmentProviders = Array.Empty<string>(),
            MediaSegmentProviderOrder = Array.Empty<string>(),
            SubtitleDownloadLanguages = Array.Empty<string>(),
            DisabledLyricFetchers = Array.Empty<string>(),
            LyricFetcherOrder = Array.Empty<string>(),
            TypeOptions = Array.Empty<TypeOptions>()
        };

    private static HashSet<string> KnownManagedPaths(params string[] defaultPaths)
        => defaultPaths
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "YouTube Music"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "YouTube"))
            .Select(NormalizePathOrEmpty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim());

    private static string NormalizePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return NormalizePath(path);
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            NormalizePath(left).TrimEnd(Path.DirectorySeparatorChar),
            NormalizePath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void ScrubAppleDoubleFiles()
    {
        foreach (var rootPath in CandidateRootPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(rootPath, "._*", SearchOption.AllDirectories))
            {
                TryDelete(path);
            }
        }
    }

    private void NormalizeLibraryOptions(string libraryName, bool enableInternetProviders)
    {
        foreach (var rootPath in CandidateRootPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var optionsPath = Path.Combine(rootPath, "default", libraryName, "options.xml");
            if (!File.Exists(optionsPath))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(optionsPath);
                var cleaned = AppleDoubleMediaPathRegex().Replace(text, string.Empty);
                var enabledValue = enableInternetProviders ? "true" : "false";
                cleaned = EnableInternetProvidersRegex().IsMatch(cleaned)
                    ? EnableInternetProvidersRegex().Replace(
                        cleaned,
                        $"<EnableInternetProviders>{enabledValue}</EnableInternetProviders>",
                        1)
                    : cleaned.Replace(
                        "<EnableRealtimeMonitor>",
                        $"<EnableInternetProviders>{enabledValue}</EnableInternetProviders>{Environment.NewLine}  <EnableRealtimeMonitor>",
                        StringComparison.Ordinal);

                if (!string.Equals(text, cleaned, StringComparison.Ordinal))
                {
                    File.WriteAllText(optionsPath, cleaned);
                    _logger.LogInformation("YtdlArchive normalized managed library options in {OptionsPath}", optionsPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "YtdlArchive could not normalize managed library options in {OptionsPath}", optionsPath);
            }
        }
    }

    private IEnumerable<string> CandidateRootPaths()
    {
        yield return Path.Combine(_applicationPaths.ProgramDataPath, "root");
        yield return Path.Combine(_applicationPaths.DataPath, "..", "root");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // AppleDouble cleanup is best-effort; Jellyfin can continue if a file is locked.
        }
        catch (UnauthorizedAccessException)
        {
            // AppleDouble cleanup is best-effort; permissions may block deleting some files.
        }
    }

    [GeneratedRegex(@"\s*<MediaPathInfo>(?:(?!</MediaPathInfo>).)*(?:This resource fork intentionally left blank|&#x0;)(?:(?!</MediaPathInfo>).)*</MediaPathInfo>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AppleDoubleMediaPathRegex();

    [GeneratedRegex(@"<EnableInternetProviders>.*?</EnableInternetProviders>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex EnableInternetProvidersRegex();
}
