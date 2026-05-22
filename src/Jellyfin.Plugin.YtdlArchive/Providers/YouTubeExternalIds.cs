using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YouTubeVideoExternalId : IExternalId
{
    private readonly string _urlFormatString = YouTubeWatchUrlFormat;

    public string ProviderName => "YouTube";

    public string Key => Constants.YouTubeProviderKey;

    public ExternalIdMediaType? Type => null;

    public static string YouTubeWatchUrlFormat => "https://www.youtube.com/watch?v={0}";

    public string UrlFormatString => _urlFormatString;

    public bool Supports(IHasProviderIds item)
        => item is Episode or Audio or AudioBook;
}

public sealed class YouTubeChannelExternalId : IExternalId
{
    private readonly string _urlFormatString = YouTubeChannelUrlFormat;

    public string ProviderName => "YouTube";

    public string Key => Constants.YouTubeProviderKey;

    public ExternalIdMediaType? Type => null;

    public static string YouTubeChannelUrlFormat => "https://www.youtube.com/channel/{0}";

    public string UrlFormatString => _urlFormatString;

    public bool Supports(IHasProviderIds item)
        => item is Series or MusicAlbum;
}
