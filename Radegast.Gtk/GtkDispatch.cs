using GLib;

namespace Radegast.Gtk;

internal static class GtkDispatch
{
    public static void Post(Action action)
    {
        Idle.Add(() =>
        {
            action();
            return false;
        });
    }
}
