using Jellyfin.Plugin.YtdlArchive.Providers;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class ProviderCoverageTests
{
    [Fact]
    public void YouTubeExternalIds_SupportExpectedJellyfinItemTypes()
    {
        var videoId = new YouTubeVideoExternalId();
        var channelId = new YouTubeChannelExternalId();

        Assert.Equal("https://www.youtube.com/watch?v={0}", videoId.UrlFormatString);
        Assert.True(videoId.Supports(new Episode()));
        Assert.True(videoId.Supports(new Audio()));
        Assert.True(videoId.Supports(new AudioBook()));
        Assert.False(videoId.Supports(new Series()));

        Assert.Equal("https://www.youtube.com/channel/{0}", channelId.UrlFormatString);
        Assert.True(channelId.Supports(new Series()));
        Assert.True(channelId.Supports(new MusicAlbum()));
        Assert.False(channelId.Supports(new Episode()));
    }

    [Fact]
    public async Task MusicAlbumProvider_UsesFirstAudioSidecarInDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("Artist Name [UCmusic]");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "track.info.json"),
                """
                {
                  "id": "abc12345678",
                  "title": "Artist Name - Track Title",
                  "description": "Album notes",
                  "channel": "Artist Channel",
                  "channel_id": "UCmusic",
                  "upload_date": "20260520",
                  "categories": ["Music"],
                  "tags": ["live"]
                }
                """);

            var result = await new YtdlMusicAlbumLocalProvider().GetMetadata(
                new ItemInfo(new MusicAlbum { Path = directory.FullName }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Artist Name", result.Item.AlbumArtists.Single());
            Assert.Equal("UCmusic", result.Item.ProviderIds["YouTube"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AudioProvider_MapsAudioSidecarMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("Album Folder [UCmusic]");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "track.m4a");
            File.WriteAllText(mediaPath, string.Empty);
            WriteAudioSidecar(mediaPath);

            var result = await new YtdlAudioLocalProvider().GetMetadata(
                new ItemInfo(new Audio { Path = mediaPath }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Track Title", result.Item.Name);
            Assert.Equal("Source Title", result.Item.OriginalTitle);
            Assert.Equal("Album Name", result.Item.Album);
            Assert.Equal("Artist Name", result.Item.Artists.Single());
            Assert.Equal("Artist Name", result.Item.AlbumArtists.Single());
            Assert.Equal("Channel Name", result.Item.Studios.Single());
            Assert.Equal("Music", result.Item.Genres.Single());
            Assert.Equal("live", result.Item.Tags.Single());
            Assert.Equal("abc12345678", result.Item.ProviderIds["YouTube"]);
            Assert.Equal("20260520-Track Title", result.Item.ForcedSortName);
            Assert.Equal(TimeSpan.FromSeconds(125).Ticks, result.Item.RunTimeTicks);
            Assert.Contains("1,234 YouTube views", result.Item.Overview);
            Assert.Contains("https://youtu.be/abc12345678", result.Item.Overview);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AudioBookProvider_MapsCommonAudioSidecarMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("Audiobook Folder [UCmusic]");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "chapter.m4b");
            File.WriteAllText(mediaPath, string.Empty);
            WriteAudioSidecar(mediaPath);

            var result = await new YtdlAudioBookLocalProvider().GetMetadata(
                new ItemInfo(new AudioBook { Path = mediaPath }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Track Title", result.Item.Name);
            Assert.Equal("Album Name", result.Item.Album);
            Assert.Equal("Channel Name", result.Item.Studios.Single());
            Assert.Equal("abc12345678", result.Item.ProviderIds["YouTube"]);
            Assert.Contains("9 likes", result.Item.Overview);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EpisodeProvider_MapsVideoSidecarMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("Channel Name [UCepisode]");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "episode.mp4");
            File.WriteAllText(mediaPath, string.Empty);
            File.WriteAllText(
                Path.ChangeExtension(mediaPath, ".info.json"),
                """
                {
                  "id": "dQw4w9WgXcQ",
                  "title": "Episode Title",
                  "description": "Episode notes",
                  "upload_date": "20260521",
                  "duration": 90
                }
                """);

            var result = await new YtdlEpisodeLocalProvider().GetMetadata(
                new ItemInfo(new Episode { Path = mediaPath }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Episode Title", result.Item.Name);
            Assert.Equal("Episode notes", result.Item.Overview);
            Assert.Equal(2026, result.Item.ProductionYear);
            Assert.Equal("20260521-Episode Title", result.Item.ForcedSortName);
            Assert.Equal(TimeSpan.FromSeconds(90).Ticks, result.Item.RunTimeTicks);
            Assert.Equal("dQw4w9WgXcQ", result.Item.ProviderIds["YouTube"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EpisodeProvider_ReturnsEmptyResultWithoutSidecar()
    {
        var result = await new YtdlEpisodeLocalProvider().GetMetadata(
            new ItemInfo(new Episode { Path = Path.Combine(Path.GetTempPath(), "missing.mp4") }),
            directoryService: null!,
            CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public void LocalImageProvider_FindsMediaAndDirectoryThumbnails()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "episode.mp4");
            var thumbnailPath = Path.Combine(directory.FullName, "episode.webp");
            var albumThumbnailPath = Path.Combine(directory.FullName, "cover.jpg");
            File.WriteAllText(mediaPath, string.Empty);
            File.WriteAllText(thumbnailPath, string.Empty);
            File.WriteAllText(albumThumbnailPath, string.Empty);

            var provider = new YtdlLocalImageProvider(new RecordingFileSystem());

            var episodeImage = provider.GetImages(new Episode { Path = mediaPath }, directoryService: null!).Single();
            var albumImage = provider.GetImages(new MusicAlbum { Path = directory.FullName }, directoryService: null!).Single();

            Assert.Equal(thumbnailPath, episodeImage.FileInfo.FullName);
            Assert.Equal(albumThumbnailPath, albumImage.FileInfo.FullName);
            Assert.True(provider.Supports(new AudioBook()));
            Assert.False(provider.Supports(new Folder()));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LocalImageProvider_ReturnsNoImagesForUnsupportedOrMissingPaths()
    {
        var provider = new YtdlLocalImageProvider(new RecordingFileSystem());

        Assert.Empty(provider.GetImages(new Episode { Path = Path.Combine(Path.GetTempPath(), "no-thumb.mp4") }, directoryService: null!));
        Assert.Empty(provider.GetImages(new Folder { Path = Path.GetTempPath() }, directoryService: null!));
    }

    [Fact]
    public void ServiceRegistrator_AddsExpectedServices()
    {
        var services = new ServiceCollection();

        new ServiceRegistrator().RegisterServices(services, applicationHost: null!);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(YtdlpManager));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(LibraryReconciler));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(LibraryBootstrapHostedService));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(DownloaderHostedService));
    }

    [Fact]
    public async Task LibraryBootstrapHostedService_StopAsyncCompletes()
    {
        var service = new LibraryBootstrapHostedService(
            NullLogger<LibraryBootstrapHostedService>.Instance,
            libraryReconciler: null!);

        await service.StopAsync(CancellationToken.None);

        Assert.IsAssignableFrom<IHostedService>(service);
    }

    [Fact]
    public async Task SeriesProvider_FallsBackToFolderChannelId()
    {
        var directory = Directory.CreateTempSubdirectory("Channel Name [UCYO_jab_esuFRV4b17AJtAw]");
        try
        {
            var result = await new YtdlSeriesLocalProvider().GetMetadata(
                new ItemInfo(new Series { Path = directory.FullName }),
                directoryService: null!,
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Channel Name", result.Item.Name);
            Assert.Equal("UCYO_jab_esuFRV4b17AJtAw", result.Item.ProviderIds["YouTube"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void WriteAudioSidecar(string mediaPath)
    {
        File.WriteAllText(
            Path.ChangeExtension(mediaPath, ".info.json"),
            """
            {
              "id": "abc12345678",
              "title": "Source Title",
              "track": "Track Title",
              "artist": "Artist Name",
              "album": "Album Name",
              "description": "Audio notes",
              "channel": "Channel Name",
              "release_date": "20260520",
              "duration": 125,
              "view_count": 1234,
              "like_count": 9,
              "webpage_url": "https://youtu.be/abc12345678",
              "categories": ["Music"],
              "tags": ["live"]
            }
            """);
    }

    private sealed class RecordingFileSystem : IFileSystem
    {
        public FileSystemMetadata GetFileSystemInfo(string path)
            => new()
            {
                Exists = File.Exists(path) || Directory.Exists(path),
                FullName = path,
                Name = Path.GetFileName(path),
                Extension = Path.GetExtension(path),
                IsDirectory = Directory.Exists(path)
            };

        public bool AreEqual(string path1, string path2) => string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        public bool ContainsSubPath(string parentPath, string path) => path.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
        public void CreateShortcut(string shortcutPath, string target) => throw new NotSupportedException();
        public void DeleteFile(string path) => File.Delete(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
        public DateTime GetCreationTimeUtc(FileSystemMetadata info) => info.CreationTimeUtc;
        public DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(path);
        public IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false) => Directory.EnumerateDirectories(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        public FileSystemMetadata GetDirectoryInfo(string path) => GetFileSystemInfo(path);
        public IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false) => GetDirectoryPaths(path, recursive).Select(GetFileSystemInfo);
        public IEnumerable<FileSystemMetadata> GetDrives() => [];
        public FileSystemMetadata GetFileInfo(string path) => GetFileSystemInfo(path);
        public string GetFileNameWithoutExtension(FileSystemMetadata info) => Path.GetFileNameWithoutExtension(info.FullName);
        public IEnumerable<string> GetFilePaths(string path, bool recursive = false) => Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        public IEnumerable<string> GetFilePaths(string path, string[]? extensions, bool enableCaseSensitiveExtensions, bool recursive) => GetFilePaths(path, recursive).Where(file => extensions?.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) != false);
        public IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false) => GetFilePaths(path, recursive).Select(GetFileSystemInfo);
        public IEnumerable<FileSystemMetadata> GetFiles(string path, string pattern, bool recursive = false) => Directory.EnumerateFiles(path, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Select(GetFileSystemInfo);
        public IEnumerable<FileSystemMetadata> GetFiles(string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive) => GetFilePaths(path, recursive).Where(file => extensions?.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) != false).Select(GetFileSystemInfo);
        public IEnumerable<FileSystemMetadata> GetFiles(string path, string pattern, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive) => GetFiles(path, pattern, recursive).Where(file => extensions?.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) != false);
        public IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false) => GetFileSystemEntryPaths(path, recursive).Select(GetFileSystemInfo);
        public IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false) => Directory.EnumerateFileSystemEntries(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        public DateTime GetLastWriteTimeUtc(FileSystemMetadata info) => info.LastWriteTimeUtc;
        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
        public string GetValidFilename(string filename) => filename;
        public bool IsPathFile(string path) => File.Exists(path);
        public bool IsShortcut(string filename) => false;
        public string MakeAbsolutePath(string folderPath, string filePath) => Path.Combine(folderPath, filePath);
        public void MoveDirectory(string source, string target) => Directory.Move(source, target);
        public string ResolveShortcut(string filename) => filename;
        public void SetAttributes(string path, bool isHidden, bool readOnly) { }
        public void SetHidden(string path, bool isHidden) { }
        public void SwapFiles(string file1, string file2) => throw new NotSupportedException();
    }
}
