using System.Reflection;
using Jellyfin.Plugin.YtdlArchive.Services;

namespace Jellyfin.Plugin.YtdlArchive.Tests;

public sealed class ArchiveSettingsTests
{
    private static readonly Type ArchiveSettingsType = typeof(YtdlpManager).Assembly.GetType("Jellyfin.Plugin.YtdlArchive.Services.ArchiveSettings")
        ?? throw new InvalidOperationException("ArchiveSettings type was not found.");

    [Theory]
    [InlineData(null, "other")]
    [InlineData("", "other")]
    [InlineData(" video ", "other")]
    [InlineData("MUSIC", "music")]
    [InlineData("podcast", "podcast")]
    [InlineData("book", "audiobook")]
    [InlineData("audiobook", "audiobook")]
    [InlineData("unknown", "")]
    public void NormalizeTarget_MapsBrowserTargets(string? target, string expected)
    {
        var result = InvokeStatic<string>("NormalizeTarget", target);

        Assert.Equal(expected, result);
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = ArchiveSettingsType.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)method.Invoke(null, args)!;
    }
}
