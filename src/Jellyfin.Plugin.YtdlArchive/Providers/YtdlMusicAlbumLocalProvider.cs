using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlMusicAlbumLocalProvider : ILocalMetadataProvider<MusicAlbum>
{
    public string Name => Constants.PluginName;

    public async Task<MetadataResult<MusicAlbum>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadFirstAudioInDirectoryAsync(info.Path, cancellationToken);
        if (metadata is null)
        {
            return new MetadataResult<MusicAlbum>();
        }

        var folderName = StripBracketedId(Path.GetFileName(info.Path));
        var channelName = metadata.ChannelName ?? folderName;
        var albumName = FirstNonBlank(metadata.Album, folderName, channelName);
        var artistName = FirstNonBlank(metadata.Artist, channelName, albumName);
        var item = new MusicAlbum
        {
            Name = albumName,
            AlbumArtists = NonBlank(artistName),
            Artists = NonBlank(artistName),
            Overview = metadata.Description,
            PremiereDate = metadata.ReleaseDate?.UtcDateTime,
            ProductionYear = metadata.ReleaseDate?.Year,
            Genres = metadata.Categories.ToArray(),
            Tags = metadata.Tags.ToArray(),
            Studios = string.IsNullOrWhiteSpace(channelName) ? [] : [channelName]
        };

        if (!string.IsNullOrWhiteSpace(metadata.ChannelId))
        {
            item.ProviderIds[Constants.YouTubeProviderKey] = metadata.ChannelId;
        }

        return new MetadataResult<MusicAlbum>
        {
            HasMetadata = true,
            Item = item
        };
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string[] NonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string StripBracketedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bracket = value.LastIndexOf('[');
        return bracket > 0 ? value[..bracket].Trim() : value;
    }
}
