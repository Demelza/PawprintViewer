using Gtk;

namespace Radegast.Gtk;

internal static class Program
{
    internal const string ViewerName = "Pawprint Viewer";
    internal const string ViewerVersion = ViewerName + " 0.1";
    internal static string IconPath => Path.Combine(AppContext.BaseDirectory, "PawprintViewer.png");

    internal static void SetApplicationIcon()
    {
        using var icon = new Gdk.Pixbuf(IconPath);
        // Small sizes serve window decorations/taskbars and stay within X11's
        // icon-property limit; the supplied 512px image is too large on some servers.
        var sizes = new[] { 16, 32, 48, 64, 128, 256 };
        var icons = sizes.Select(size => icon.ScaleSimple(size, size, Gdk.InterpType.Bilinear)).ToArray();
        try { Window.DefaultIconList = icons; }
        finally { foreach (var image in icons) image.Dispose(); }
    }

    private static void Main()
    {
        GLib.Global.ApplicationName = ViewerName;
        GLib.Global.ProgramName = "pawprint-viewer";
        Application.Init();
        SetApplicationIcon();
        var window = new MainWindow();
        window.ShowAll();
        Application.Run();
    }
}
