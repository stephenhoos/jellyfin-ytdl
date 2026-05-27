using Jellyfin.Plugin.YtdlArchive.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YtdlArchive.Providers;

public sealed class YtdlAudioBookLocalProvider : ILocalMetadataProvider<AudioBook>
{
    public string Name => Constants.PluginName;

    public async Task<MetadataResult<AudioBook>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var metadata = await YtdlpSidecarReader.ReadAudioForMediaAsync(info.Path, cancellationToken);
        if (metadata is null)
        {
            return new MetadataResult<AudioBook>();
        }

        var item = new AudioBook
        {
            Album = metadata.Album
        };

        YtdlAudioMetadataMapper.ApplyCommonMetadata(item, metadata);
        return YtdlAudioMetadataMapper.CreateResult(item);
    }
}
