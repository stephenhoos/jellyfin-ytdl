using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YouTubeVideoExternalId : IExternalId
{
    public string ProviderName => "YouTube";

    public string Key => Constants.YouTubeProviderKey;

    public ExternalIdMediaType? Type => null;

    public static string YouTubeWatchUrlFormat => "https://www.youtube.com/watch?v={0}";

    [SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Jellyfin IExternalId requires an instance UrlFormatString property.")]
    public string UrlFormatString => YouTubeWatchUrlFormat;

    public bool Supports(IHasProviderIds item)
        => item is Episode or Audio or AudioBook;
}

public sealed class YouTubeChannelExternalId : IExternalId
{
    public string ProviderName => "YouTube";

    public string Key => Constants.YouTubeProviderKey;

    public ExternalIdMediaType? Type => null;

    public static string YouTubeChannelUrlFormat => "https://www.youtube.com/channel/{0}";

    [SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Jellyfin IExternalId requires an instance UrlFormatString property.")]
    public string UrlFormatString => YouTubeChannelUrlFormat;

    public bool Supports(IHasProviderIds item)
        => item is Series or MusicAlbum;
}
