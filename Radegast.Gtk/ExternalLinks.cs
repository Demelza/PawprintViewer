using System.Diagnostics;

namespace Radegast.Gtk;

internal static class ExternalLinks
{
    public static void Open(string url)
    {
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
