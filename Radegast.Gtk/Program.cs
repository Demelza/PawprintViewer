using Gtk;

namespace Radegast.Gtk;

internal static class Program
{
    internal const string ViewerName = "Pawprint Viewer";
    internal const string ViewerVersion = ViewerName + " 0.1";

    private static void Main()
    {
        GLib.Global.ApplicationName = ViewerName;
        GLib.Global.ProgramName = "pawprint-viewer";
        Application.Init();
        var window = new MainWindow();
        window.ShowAll();
        Application.Run();
    }
}
