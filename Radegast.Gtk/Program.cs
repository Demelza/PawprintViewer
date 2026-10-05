using Gtk;

namespace Radegast.Gtk;

internal static class Program
{
    private static void Main()
    {
        Application.Init();
        var window = new MainWindow();
        window.ShowAll();
        Application.Run();
    }
}
