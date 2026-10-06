using System.Globalization;

namespace Radegast.Gtk;

internal static class NotificationPreview
{
    /// <summary>Fit plain text to one line, preserving complete Unicode text elements.</summary>
    public static string Fit(string text, int width, Func<string, int> measure)
    {
        text = string.Join(" ", text.Replace("\0", string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0 || measure(text) <= width) return text;
        const string ellipsis = "…";
        if (measure(ellipsis) > width) return string.Empty;
        var elements = StringInfo.ParseCombiningCharacters(text);
        var low = 0;
        var high = elements.Length;
        string Candidate(int count) => text[..(count == elements.Length ? text.Length : elements[count])].TrimEnd() + ellipsis;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (measure(Candidate(middle)) <= width) low = middle;
            else high = middle - 1;
        }
        return Candidate(low);
    }
}
