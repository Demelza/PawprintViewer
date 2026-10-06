using LibreMetaverse;
using System.Text.RegularExpressions;

namespace Radegast.Gtk;

internal sealed record ChatTextSpan(string Text, UUID AvatarId = default, ProfileTextLink? Link = null);

/// <summary>Recognizes profile SLURLs while retaining surrounding text and punctuation.</summary>
internal static class AvatarProfileLinks
{
    private static readonly Regex Profile = new(
        @"(?<![\w:/])secondlife:///?app/agent/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/about(?=$|[\s)\]}>,;.!'""<])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<ChatTextSpan> Parse(string text)
    {
        var spans = new List<ChatTextSpan>();
        var offset = 0;
        foreach (Match match in Profile.Matches(text))
        {
            if (!UUID.TryParse(match.Groups["id"].Value, out var id) || id == UUID.Zero) continue;
            if (match.Index > offset) spans.Add(new(text[offset..match.Index]));
            spans.Add(new(match.Value, id));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) spans.Add(new(text[offset..]));
        return spans;
    }

    public static string HideNames(string text, Func<UUID, bool> canShowName) =>
        string.Concat(Parse(text).Select(span => span.AvatarId != UUID.Zero && !canShowName(span.AvatarId)
            ? "Resident" : span.Text));
}
