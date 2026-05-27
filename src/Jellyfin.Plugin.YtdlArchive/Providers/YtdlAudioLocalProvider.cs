using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlAudioLocalProvider : ILocalMetadataProvider<Audio>
{
    public string Name => Constants.PluginName;

    public async Task<MetadataResult<Audio>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadAudioForMediaAsync(info.Path, cancellationToken);
        if (metadata is null)
        {
            return new MetadataResult<Audio>();
        }

        var item = new Audio
        {
            Album = metadata.Album,
            Artists = YtdlAudioMetadataMapper.NonBlank(metadata.Artist),
            AlbumArtists = YtdlAudioMetadataMapper.NonBlank(metadata.Artist)
        };

        YtdlAudioMetadataMapper.ApplyCommonMetadata(item, metadata);
        return YtdlAudioMetadataMapper.CreateResult(item);
    }
}
