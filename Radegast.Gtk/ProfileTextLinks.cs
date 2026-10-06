using LibreMetaverse;
using System.Text.RegularExpressions;

namespace Radegast.Gtk;

internal enum ProfileLinkAction { Web, AvatarProfile, PayResident }

internal sealed record ProfileTextLink(string Url, ProfileLinkAction Action, UUID AvatarId = default)
{
    public static ProfileTextLink Avatar(UUID id) => new("", ProfileLinkAction.AvatarProfile, id);

    public bool IsLocation => Action == ProfileLinkAction.Web &&
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("maps.secondlife.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/secondlife/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Second Life's [URL label] profile markup, plus ordinary avatar profile SLURLs.</summary>
internal static class ProfileTextLinks
{
    private static readonly Regex Bracketed = new(
        @"\[(?<url>[^\s\[\]]+)[ \t]+(?<label>[^\]\r\n]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Agent = new(
        @"^secondlife:///?app/agent/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(?<action>about|pay)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<ChatTextSpan> Parse(string text)
    {
        var spans = new List<ChatTextSpan>();
        var offset = 0;
        foreach (Match match in Bracketed.Matches(text))
        {
            var label = match.Groups["label"].Value.Trim();
            var link = ParseTarget(match.Groups["url"].Value);
            if (label.Length == 0 || link == null) continue;
            spans.AddRange(AvatarProfileLinks.Parse(text[offset..match.Index]));
            spans.Add(new(label, Link: link));
            offset = match.Index + match.Length;
        }
        spans.AddRange(AvatarProfileLinks.Parse(text[offset..]));
        return spans;
    }

    private static ProfileTextLink? ParseTarget(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0)
            return new(url, ProfileLinkAction.Web);
        var agent = Agent.Match(url);
        if (!agent.Success || !UUID.TryParse(agent.Groups["id"].Value, out var id) || id == UUID.Zero) return null;
        return new(url, agent.Groups["action"].Value.Equals("pay", StringComparison.OrdinalIgnoreCase)
            ? ProfileLinkAction.PayResident : ProfileLinkAction.AvatarProfile, id);
    }
}
