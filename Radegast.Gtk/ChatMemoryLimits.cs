namespace Radegast.Gtk;

internal static class ChatMemoryLimits
{
    public const int Messages = 2000;
    public const int Characters = 1_000_000;

    public static string LimitMessage(string text)
    {
        if (text.Length <= Characters) return text;
        var end = Characters - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }
}
