using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class LibraryReconcilerTests
{
    [Fact]
    public void NormalizePathOrEmpty_RejectsBlankAndInvalidPaths()
    {
        Assert.Equal(string.Empty, InvokeStatic<string>("NormalizePathOrEmpty", " "));
        Assert.Equal(string.Empty, InvokeStatic<string>("NormalizePathOrEmpty", "bad\0path"));
    }

    [Fact]
    public void SamePath_IgnoresTrailingDirectorySeparators()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var left = directory.FullName + Path.DirectorySeparatorChar;
            var result = InvokeStatic<bool>("SamePath", left, directory.FullName);

            Assert.True(result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void KnownManagedPaths_IncludesProvidedDefaultsAndLegacyLocations()
    {
        var defaultPath = Path.Combine(Path.GetTempPath(), "YT-Music");

        var result = InvokeStatic<HashSet<string>>("KnownManagedPaths", new object[] { new[] { defaultPath } });

        Assert.Contains(Path.GetFullPath(defaultPath), result);
        Assert.Contains(result, path => path.EndsWith(Path.Combine("Music", "YouTube Music"), StringComparison.Ordinal));
        Assert.Contains(result, path => path.EndsWith(Path.Combine("Downloads", "YouTube"), StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeLibraryOptions_RemovesAppleDoublePathAndSetsInternetProviderFlag()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var optionsDirectory = Path.Combine(root.FullName, "root", "default", "YT-Music");
            Directory.CreateDirectory(optionsDirectory);
            var optionsPath = Path.Combine(optionsDirectory, "options.xml");
            File.WriteAllText(
                optionsPath,
                """
                <LibraryOptions>
                  <MediaPathInfo><Path>._bad</Path><Comment>This resource fork intentionally left blank</Comment></MediaPathInfo>
                  <EnableInternetProviders>true</EnableInternetProviders>
                  <EnableRealtimeMonitor>true</EnableRealtimeMonitor>
                </LibraryOptions>
                """);

            var reconciler = new LibraryReconciler(
                libraryManager: null!,
                ApplicationPathsProxy.Create(root.FullName),
                NullLogger<LibraryReconciler>.Instance);

            InvokeInstance(reconciler, "NormalizeLibraryOptions", "YT-Music", false);
            var candidateRoots = InvokeInstance<IEnumerable<string>>(reconciler, "CandidateRootPaths").ToArray();

            var cleaned = File.ReadAllText(optionsPath);
            Assert.Contains(Path.Combine(root.FullName, "root"), candidateRoots);
            Assert.Contains(Path.Combine(root.FullName, "data", "..", "root"), candidateRoots);
            Assert.DoesNotContain("resource fork", cleaned, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<EnableInternetProviders>false</EnableInternetProviders>", cleaned);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnsureConfiguredLibrariesAsync_CreatesConfiguredLibraryDefinitions()
    {
        var directory = Directory.CreateTempSubdirectory();
        var originals = new Dictionary<string, string?>
        {
            ["YTDL_MUSIC_DOWNLOAD_DIR"] = Environment.GetEnvironmentVariable("YTDL_MUSIC_DOWNLOAD_DIR"),
            ["YTDL_PODCAST_DOWNLOAD_DIR"] = Environment.GetEnvironmentVariable("YTDL_PODCAST_DOWNLOAD_DIR"),
            ["YTDL_AUDIOBOOK_DOWNLOAD_DIR"] = Environment.GetEnvironmentVariable("YTDL_AUDIOBOOK_DOWNLOAD_DIR"),
            ["YTDL_OTHER_DOWNLOAD_DIR"] = Environment.GetEnvironmentVariable("YTDL_OTHER_DOWNLOAD_DIR")
        };

        try
        {
            foreach (var name in originals.Keys)
            {
                Environment.SetEnvironmentVariable(name, Path.Combine(directory.FullName, name.ToLowerInvariant()));
            }

            var libraryManager = LibraryManagerProxy.Create();
            var reconciler = new LibraryReconciler(
                libraryManager,
                ApplicationPathsProxy.Create(directory.FullName),
                NullLogger<LibraryReconciler>.Instance);

            await reconciler.EnsureConfiguredLibrariesAsync(CancellationToken.None);

            Assert.Equal(4, ((LibraryManagerProxy)(object)libraryManager).AddedLibraries.Count);
            Assert.All(originals.Keys, name => Assert.True(Directory.Exists(Environment.GetEnvironmentVariable(name))));
        }
        finally
        {
            foreach (var (name, value) in originals)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryDelete_IgnoresDirectoryPaths()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            InvokeStatic<object?>("TryDelete", directory.FullName);

            Assert.True(Directory.Exists(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(LibraryReconciler).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }

    private static T InvokeInstance<T>(object instance, string name, params object?[] args)
    {
        var method = typeof(LibraryReconciler).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(instance, args)!;
    }

    private static void InvokeInstance(object instance, string name, params object?[] args)
    {
        InvokeInstance<object?>(instance, name, args);
    }

    public class ApplicationPathsProxy : DispatchProxy
    {
        private string _root = string.Empty;

        public static IApplicationPaths Create(string root)
        {
            var proxy = Create<IApplicationPaths, ApplicationPathsProxy>();
            ((ApplicationPathsProxy)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_ProgramDataPath" => _root,
                "get_DataPath" => Path.Combine(_root, "data"),
                _ when targetMethod?.ReturnType == typeof(string) => _root,
                _ when targetMethod?.ReturnType == typeof(bool) => false,
                _ => null
            };
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public List<string> AddedLibraries { get; } = [];

        public static ILibraryManager Create()
            => Create<ILibraryManager, LibraryManagerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetVirtualFolders")
            {
                var returnType = targetMethod.ReturnType;
                var elementType = returnType.IsArray
                    ? returnType.GetElementType()!
                    : returnType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                var array = Array.CreateInstance(elementType, 0);
                if (returnType.IsAssignableFrom(array.GetType()))
                {
                    return array;
                }

                return Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
            }

            if (targetMethod?.Name == "AddVirtualFolder")
            {
                AddedLibraries.Add((string)args![0]!);
                return Task.CompletedTask;
            }

            if (targetMethod?.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (targetMethod?.ReturnType == typeof(void))
            {
                return null;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
