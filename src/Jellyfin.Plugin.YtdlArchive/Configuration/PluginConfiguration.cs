using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YtdlArchive.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool EnsureLibrariesOnStartup { get; set; } = true;

    public bool EnsureMusicLibrary { get; set; } = true;

    public bool EnsurePodcastLibrary { get; set; } = true;

    public bool EnsureAudiobookLibrary { get; set; } = true;

    public bool EnsureOtherLibrary { get; set; } = true;

    public string VideoLibraryName { get; set; } = "YT-Other";

    public string MusicLibraryName { get; set; } = "YT-Music";

    public string PodcastLibraryName { get; set; } = "YT-Podcast";

    public string AudiobookLibraryName { get; set; } = "YT-Audiobooks";

    public string OtherLibraryName { get; set; } = "YT-Other";

    public string VideoDownloadDirectory { get; set; } = string.Empty;

    public string MusicDownloadDirectory { get; set; } = string.Empty;

    public string PodcastDownloadDirectory { get; set; } = string.Empty;

    public string AudiobookDownloadDirectory { get; set; } = string.Empty;

    public string OtherDownloadDirectory { get; set; } = string.Empty;

    public bool EnableRemoteMetadata { get; set; } = true;

    public bool PreferLocalSidecars { get; set; } = true;

    public bool AutoInstallYtdlp { get; set; } = true;

    public bool AutoUpdateYtdlp { get; set; } = true;

    public string BrowserApiToken { get; set; } = string.Empty;

    public bool AllowNonYouTubeDownloads { get; set; }

    public int MaxConcurrentDownloads { get; set; } = 2;

    public string YtdlpPath { get; set; } = string.Empty;

    public string CacheDirectory { get; set; } = string.Empty;

    public int CacheExpirationDays { get; set; } = 30;

    public string CookieFilePath { get; set; } = string.Empty;

    public bool DownloadMissingMetadataIntoCache { get; set; } = true;

    public bool DownloadMissingThumbnailsIntoCache { get; set; } = true;

    public bool EnableVerboseLogging { get; set; }

    public TagImportPolicy TagImportPolicy { get; set; } = TagImportPolicy.All;

    public PeopleMappingPolicy PeopleMappingPolicy { get; set; } = PeopleMappingPolicy.ChannelAsStudio;

    public ShortsHandlingPolicy ShortsHandlingPolicy { get; set; } = ShortsHandlingPolicy.Ignore;
}

public enum TagImportPolicy
{
    None,
    Conservative,
    All
}

public enum PeopleMappingPolicy
{
    None,
    ChannelAsDirector,
    ChannelAsStudio
}

public enum ShortsHandlingPolicy
{
    Include,
    Ignore,
    Mark
}
