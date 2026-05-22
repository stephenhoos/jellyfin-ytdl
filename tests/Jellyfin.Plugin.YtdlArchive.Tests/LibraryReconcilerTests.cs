using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;
using MediaBrowser.Common.Configuration;
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

            var cleaned = File.ReadAllText(optionsPath);
            Assert.DoesNotContain("resource fork", cleaned, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<EnableInternetProviders>false</EnableInternetProviders>", cleaned);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(LibraryReconciler).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }

    private static void InvokeInstance(object instance, string name, params object?[] args)
    {
        var method = typeof(LibraryReconciler).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{name} was not found.");
        method.Invoke(instance, args);
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
}
