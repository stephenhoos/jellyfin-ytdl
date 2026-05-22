using Jellyfin.Plugin.YtdlArchive.Configuration;

namespace Jellyfin.Plugin.YtdlArchive.Services;

internal static class ArchiveSettings
{
    public const string DefaultMusicLibraryName = "YT-Music";
    public const string DefaultPodcastLibraryName = "YT-Podcast";
    public const string DefaultAudiobookLibraryName = "YT-Audiobooks";
    public const string DefaultOtherLibraryName = "YT-Other";
    public const string MusicTarget = "music";
    public const string PodcastTarget = "podcast";
    public const string AudiobookTarget = "audiobook";
    public const string OtherTarget = "other";
    public const string VideoTarget = "video";
    public const string BookTarget = "book";

    public static string DefaultMusicDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Music",
        DefaultMusicLibraryName);

    public static string DefaultPodcastDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Music",
        DefaultPodcastLibraryName);

    public static string DefaultAudiobookDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Music",
        DefaultAudiobookLibraryName);

    public static string DefaultOtherDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        DefaultOtherLibraryName);

    public static string VideoLibraryName => FirstConfigured(
        Environment.GetEnvironmentVariable("JELLYFIN_LIBRARY_NAME"),
        Configuration.VideoLibraryName,
        OtherLibraryName);

    public static string MusicLibraryName => FirstConfigured(
        Environment.GetEnvironmentVariable("JELLYFIN_MUSIC_LIBRARY_NAME"),
        Configuration.MusicLibraryName,
        DefaultMusicLibraryName);

    public static string PodcastLibraryName => FirstConfigured(
        Environment.GetEnvironmentVariable("JELLYFIN_PODCAST_LIBRARY_NAME"),
        Configuration.PodcastLibraryName,
        DefaultPodcastLibraryName);

    public static string AudiobookLibraryName => FirstConfigured(
        Environment.GetEnvironmentVariable("JELLYFIN_AUDIOBOOK_LIBRARY_NAME"),
        Configuration.AudiobookLibraryName,
        DefaultAudiobookLibraryName);

    public static string OtherLibraryName => FirstConfigured(
        Environment.GetEnvironmentVariable("JELLYFIN_OTHER_LIBRARY_NAME"),
        Configuration.OtherLibraryName,
        DefaultOtherLibraryName);

    public static string VideoDownloadDirectory => Path.GetFullPath(FirstConfigured(
        Environment.GetEnvironmentVariable("YTDL_DOWNLOAD_DIR"),
        Configuration.VideoDownloadDirectory,
        OtherDownloadDirectory));

    public static string MusicDownloadDirectory => Path.GetFullPath(FirstConfigured(
        Environment.GetEnvironmentVariable("YTDL_MUSIC_DOWNLOAD_DIR"),
        Configuration.MusicDownloadDirectory,
        DefaultMusicDownloadDirectory));

    public static string PodcastDownloadDirectory => Path.GetFullPath(FirstConfigured(
        Environment.GetEnvironmentVariable("YTDL_PODCAST_DOWNLOAD_DIR"),
        Configuration.PodcastDownloadDirectory,
        DefaultPodcastDownloadDirectory));

    public static string AudiobookDownloadDirectory => Path.GetFullPath(FirstConfigured(
        Environment.GetEnvironmentVariable("YTDL_AUDIOBOOK_DOWNLOAD_DIR"),
        Configuration.AudiobookDownloadDirectory,
        DefaultAudiobookDownloadDirectory));

    public static string OtherDownloadDirectory => Path.GetFullPath(FirstConfigured(
        Environment.GetEnvironmentVariable("YTDL_OTHER_DOWNLOAD_DIR"),
        Configuration.OtherDownloadDirectory,
        DefaultOtherDownloadDirectory));

    public static bool EnsureLibrariesOnStartup => Configuration.EnsureLibrariesOnStartup;

    public static bool EnsureMusicLibrary => Configuration.EnsureMusicLibrary;

    public static bool EnsurePodcastLibrary => Configuration.EnsurePodcastLibrary;

    public static bool EnsureAudiobookLibrary => Configuration.EnsureAudiobookLibrary;

    public static bool EnsureOtherLibrary => Configuration.EnsureOtherLibrary;

    public static bool EnableRemoteMetadata => Configuration.EnableRemoteMetadata;

    public static string DownloadDirectoryForTarget(string target)
        => NormalizeTarget(target) switch
        {
            MusicTarget => MusicDownloadDirectory,
            PodcastTarget => PodcastDownloadDirectory,
            AudiobookTarget => AudiobookDownloadDirectory,
            OtherTarget => OtherDownloadDirectory,
            _ => VideoDownloadDirectory
        };

    public static string NormalizeTarget(string? target)
        => string.IsNullOrWhiteSpace(target)
            ? OtherTarget
            : target.Trim().ToLowerInvariant() switch
            {
                VideoTarget => OtherTarget,
                MusicTarget => MusicTarget,
                PodcastTarget => PodcastTarget,
                AudiobookTarget => AudiobookTarget,
                BookTarget => AudiobookTarget,
                OtherTarget => OtherTarget,
                _ => string.Empty
            };

    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string FirstConfigured(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))!;
}
