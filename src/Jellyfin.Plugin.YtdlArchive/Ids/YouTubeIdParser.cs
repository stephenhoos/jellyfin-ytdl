using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YtdlArchive.Ids;

public static partial class YouTubeIdParser
{
    public static string? FindVideoId(string? value)
        => FindLastBracketedMatch(value, VideoIdRegex());

    public static string? FindChannelId(string? value)
        => FindLastBracketedMatch(value, ChannelIdRegex());

    public static string? FindPlaylistId(string? value)
        => FindLastBracketedMatch(value, PlaylistIdRegex());

    public static bool IsValidVideoId(string? value)
        => !string.IsNullOrWhiteSpace(value) && VideoIdOnlyRegex().IsMatch(value);

    public static bool IsValidChannelId(string? value)
        => !string.IsNullOrWhiteSpace(value) && ChannelIdOnlyRegex().IsMatch(value);

    public static bool IsValidPlaylistId(string? value)
        => !string.IsNullOrWhiteSpace(value) && PlaylistIdOnlyRegex().IsMatch(value);

    private static string? FindLastBracketedMatch(string? value, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var matches = regex.Matches(value);
        return matches.Count == 0 ? null : matches[^1].Groups["id"].Value;
    }

    [GeneratedRegex(@"(?<=\[)(?<id>[A-Za-z0-9_-]{11})(?=\])")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdOnlyRegex();

    [GeneratedRegex(@"(?<=\[)(?<id>UC[A-Za-z0-9_-]{22})(?=\])")]
    private static partial Regex ChannelIdRegex();

    [GeneratedRegex(@"^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelIdOnlyRegex();

    [GeneratedRegex(@"(?<=\[)(?<id>(?:PL|UU|OLAK)[A-Za-z0-9_-]{10,})(?=\])")]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"^(?:PL|UU|OLAK)[A-Za-z0-9_-]{10,}$")]
    private static partial Regex PlaylistIdOnlyRegex();
}
