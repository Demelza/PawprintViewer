using Gdk;
using Gtk;
using Radegast.Gtk;
using System.Runtime.InteropServices;

/// <summary>Requires a GTK display; emits native events through GtkSharp's callbacks.</summary>
internal static class NativeMapChecks
{
    public static int RunScroll()
    {
        Application.Init();
        var window = new global::Gtk.Window("Pawprint Viewer map scroll check");
        var map = new MapCanvas();
        map.SetActive(true, null); // No grid login or tile downloads are needed.
        map.CenterOn(256128, 257064);
        window.SetDefaultSize(512, 512);
        window.Add(map);
        window.ShowAll();
        var result = 0;
        GLib.Idle.Add(() =>
        {
            try
            {
                var initialScale = map.Viewport.Scale;
                Scroll(map, ScrollDirection.Smooth, 0, -0.5, 1);
                Scroll(map, ScrollDirection.Smooth, 0, 0.5, -1);
                Check(Math.Abs(map.Viewport.Scale - initialScale) < 0.000001, "Opposite scrolls changed the original zoom");
                Scroll(map, ScrollDirection.Up, 0, 0, 1);
                Scroll(map, ScrollDirection.Down, 0, 0, -1);
                Scroll(map, ScrollDirection.Smooth, 0, 0, 0);
                Scroll(map, ScrollDirection.Smooth, 1, 0, 0);
                Gestures(map);
                Check(!map.HasTooltip && map.TooltipText == null, "The map still has hover text");
                map.SetPeople(new[] { new MapAvatarMarker(256100, 257080), new MapAvatarMarker(256140, 257070, 3) });
                using (var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, map.AllocatedWidth, map.AllocatedHeight))
                using (var context = new Cairo.Context(surface)) map.Draw(context);
                map.SetActive(false, null);
                Scroll(map, ScrollDirection.Smooth, 0, -1, 0);
                Console.WriteLine("PASS native GTK map scrolling, click selection, drag separation, avatar drawing and inactive map");
            }
            catch (Exception ex)
            {
                result = 1;
                Console.Error.WriteLine($"FAIL native GTK map scrolling: {ex}");
            }
            finally
            {
                map.Stop();
                window.Dispose();
                Application.Quit();
            }
            return false;
        });
        Application.Run();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return result;
    }

    private static void Scroll(MapCanvas map, ScrollDirection direction, double dx, double dy, int zoomDirection)
    {
        var x = map.AllocatedWidth * 0.25;
        var y = map.AllocatedHeight * 0.4;
        var before = map.Viewport.ToWorld(x, y, map.AllocatedWidth, map.AllocatedHeight);
        var scale = map.Viewport.Scale;
        var evnt = (EventScroll)EventHelper.New(EventType.Scroll);
        try
        {
            evnt.Direction = direction;
            evnt.DeltaX = dx; evnt.DeltaY = dy;
            evnt.X = x; evnt.Y = y;
            // Exercise the native signal and Gtk.Widget.ScrollEvent_cb,
            // rather than calling the viewport math or protected handler directly.
            g_signal_emit_by_name(map.Handle, "scroll-event", evnt.Handle, out _);
        }
        finally { EventHelper.Free(evnt); }
        Check(Math.Sign(map.Viewport.Scale - scale) == zoomDirection, $"Incorrect zoom for {direction}, delta {dy}");
        var after = map.Viewport.ToWorld(x, y, map.AllocatedWidth, map.AllocatedHeight);
        Check(Math.Abs(before.X - after.X) < 0.001 && Math.Abs(before.Y - after.Y) < 0.001,
            "Scroll moved the world point under the cursor");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Gestures(MapCanvas map)
    {
        var selections = new List<(double X, double Y)>();
        void Selected(double x, double y) => selections.Add((x, y));
        map.PointSelected += Selected;
        try
        {
            var expected = map.Viewport.ToWorld(100, 120, map.AllocatedWidth, map.AllocatedHeight);
            Button(map, EventType.ButtonPress, 100, 120);
            Check(selections.Count == 0, "Mouse-down selected a destination before a drag could start");
            Button(map, EventType.ButtonRelease, 100, 120);
            Check(selections.Count == 1 && selections[0] == expected, "A click did not select the world point under the pointer");
            var center = map.Viewport.CenterX;
            Button(map, EventType.ButtonPress, 100, 120);
            var motion = (EventMotion)EventHelper.New(EventType.MotionNotify);
            try
            {
                motion.State = ModifierType.Button1Mask;
                motion.X = 160; motion.Y = 120;
                g_signal_emit_by_name(map.Handle, "motion-notify-event", motion.Handle, out _);
                motion.X = 100;
                g_signal_emit_by_name(map.Handle, "motion-notify-event", motion.Handle, out _);
            }
            finally { EventHelper.Free(motion); }
            Button(map, EventType.ButtonRelease, 100, 120);
            Check(selections.Count == 1 && Math.Abs(map.Viewport.CenterX - center) < 0.001,
                "Dragging back to the starting point selected a destination or failed to pan");
            Button(map, EventType.ButtonPress, 100, 120);
            Button(map, EventType.ButtonRelease, 200, 120);
            Check(selections.Count == 1, "A release outside the drag threshold selected a destination");
        }
        finally { map.PointSelected -= Selected; }
    }

    private static void Button(MapCanvas map, EventType type, double x, double y)
    {
        var evnt = (EventButton)EventHelper.New(type);
        try
        {
            evnt.Button = 1; evnt.X = x; evnt.Y = y;
            g_signal_emit_by_name(map.Handle, type == EventType.ButtonPress ? "button-press-event" : "button-release-event", evnt.Handle, out _);
        }
        finally { EventHelper.Free(evnt); }
    }

    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_signal_emit_by_name(IntPtr instance, string name, IntPtr evnt, out int handled);
}
