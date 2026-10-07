using Gtk;

namespace Radegast.Gtk;

/// <summary>Release removed GTK subtrees, including child signal handlers and toggle references.</summary>
internal static class GtkWidgetLifetime
{
    public static void Remove(Container parent, Widget child)
    {
        parent.Remove(child);
        Dispose(child);
    }

    public static void Clear(Container parent)
    {
        foreach (var child in parent.Children) Remove(parent, child);
    }

    public static void Dispose(Widget widget)
    {
        if (widget.Handle == IntPtr.Zero) return;
        if (widget is Container container) Clear(container);
        widget.Dispose();
    }
}
