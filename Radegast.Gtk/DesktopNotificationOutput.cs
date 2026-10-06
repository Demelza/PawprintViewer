using System.Runtime.InteropServices;
using System.Security;

namespace Radegast.Gtk;

/// <summary>Linux desktop popups through libnotify, integrated with the GTK main loop.</summary>
internal sealed class DesktopNotificationOutput : INotificationOutput
{
    private sealed class Notice(IntPtr handle, string key, string accountId, NotificationCategory category)
    {
        public IntPtr Handle { get; } = handle;
        public string Key { get; } = key;
        public string AccountId { get; } = accountId;
        public NotificationCategory Category { get; } = category;
        public bool Closed;
        public bool Released;
    }

    private readonly Dictionary<string, Notice> _notices = new();
    private readonly Dictionary<IntPtr, Notice> _handles = new();
    private readonly Native.ClosedCallback _closed;
    private readonly bool _ready;
    private bool _disposed;
    private string? _desktopError;
    public string? Error => _desktopError;

    public DesktopNotificationOutput()
    {
        _closed = OnClosed;
        try
        {
            _ready = Native.notify_init(Program.ViewerName) != 0;
            if (!_ready) _desktopError = "Desktop notifications could not be initialized.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _desktopError = "Desktop notifications need the libnotify runtime.";
        }
    }

    public void Show(string key, string accountId, NotificationCategory category, string title, string body)
    {
        if (_disposed || !_ready) return;
        body = FormatBody(body);
        if (_notices.TryGetValue(key, out var notice) && notice.Closed) { Release(notice); notice = null; }
        if (notice == null)
        {
            // A conversation replaces its earlier popup; bound unrelated popups as well.
            if (_notices.Count >= 32) Close(_notices.Values.First());
            notice = new(Native.notify_notification_new(title, body, Program.IconPath), key, accountId, category);
            if (notice.Handle == IntPtr.Zero) return;
            _notices[key] = notice;
            _handles[notice.Handle] = notice;
            Native.g_signal_connect_data(notice.Handle, "closed", _closed, IntPtr.Zero, IntPtr.Zero, 0);
            Native.notify_notification_set_timeout(notice.Handle, 7000);
            // Notifications are visual only, even if the desktop normally plays sounds.
            var silent = Native.g_variant_ref_sink(Native.g_variant_new_boolean(1));
            Native.notify_notification_set_hint(notice.Handle, "suppress-sound", silent);
            Native.g_variant_unref(silent);
        }
        else Native.notify_notification_update(notice.Handle, title, body, Program.IconPath);
        var shown = Native.notify_notification_show(notice.Handle, out var error) != 0;
        _desktopError = shown ? null : "Desktop notifications are unavailable. " + ErrorText(error);
        if (error != IntPtr.Zero) Native.g_error_free(error);
        if (!shown) Release(notice);
    }

    internal static string FormatBody(string body)
    {
        using var label = new global::Gtk.Label();
        const string sample = "abcdefghijklmnopqrstuvwxyz0123456789";
        using var layout = label.CreatePangoLayout(sample);
        layout.GetPixelSize(out var sampleWidth, out _);
        var display = Gdk.Display.Default;
        var monitorWidth = Enumerable.Range(0, display.NMonitors)
            .Select(index => display.GetMonitor(index).Geometry.Width).DefaultIfEmpty(960).Min();
        // Xfce caps its text labels at monitor-width / 30 characters. Leave
        // room for theme differences, and keep larger-screen popups compact.
        var columns = Math.Max(1, monitorWidth / 30 - 6);
        var width = Math.Min(320, sampleWidth * columns / sample.Length);
        var preview = NotificationPreview.Fit(body, width, text =>
        {
            layout.SetText(text);
            layout.GetPixelSize(out var pixels, out _);
            return pixels;
        });
        // Measure plain text first; escaped markup must never inflate its width.
        return SecurityElement.Escape(preview) ?? string.Empty;
    }

    private void OnClosed(IntPtr handle, IntPtr data)
    {
        if (!_handles.TryGetValue(handle, out var notice)) return;
        notice.Closed = true;
        GtkDispatch.Post(() => Release(notice));
    }

    public void Clear(string? accountId = null, NotificationCategory? category = null)
    {
        foreach (var notice in _notices.Values.Where(notice =>
                     (accountId == null || notice.AccountId == accountId) &&
                     (category == null || notice.Category == category)).ToArray()) Close(notice);
    }

    private void Close(Notice notice)
    {
        if (notice.Released) return;
        Native.notify_notification_close(notice.Handle, out var error);
        if (error != IntPtr.Zero) Native.g_error_free(error);
        Release(notice);
    }

    private void Release(Notice notice)
    {
        if (notice.Released) return;
        notice.Released = true;
        if (_notices.GetValueOrDefault(notice.Key) == notice) _notices.Remove(notice.Key);
        if (_handles.GetValueOrDefault(notice.Handle) == notice) _handles.Remove(notice.Handle);
        Native.g_object_unref(notice.Handle);
    }

    private static string ErrorText(IntPtr error) => error == IntPtr.Zero ? string.Empty :
        Marshal.PtrToStringUTF8(Marshal.PtrToStructure<Native.GError>(error).Message) ?? string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        if (_ready) Native.notify_uninit();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct GError { public uint Domain; public int Code; public IntPtr Message; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ClosedCallback(IntPtr notice, IntPtr data);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern int notify_init([MarshalAs(UnmanagedType.LPUTF8Str)] string app);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern void notify_uninit();
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr notify_notification_new([MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, [MarshalAs(UnmanagedType.LPUTF8Str)] string icon);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern int notify_notification_update(IntPtr notice, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, [MarshalAs(UnmanagedType.LPUTF8Str)] string icon);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern int notify_notification_show(IntPtr notice, out IntPtr error);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern int notify_notification_close(IntPtr notice, out IntPtr error);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern void notify_notification_set_timeout(IntPtr notice, int timeout);
        [DllImport("libnotify.so.4", CallingConvention = CallingConvention.Cdecl)] public static extern void notify_notification_set_hint(IntPtr notice, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr value);
        [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern void g_object_unref(IntPtr handle);
        [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern ulong g_signal_connect_data(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ClosedCallback callback, IntPtr data, IntPtr destroy, int flags);
        [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern void g_error_free(IntPtr error);
        [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr g_variant_new_boolean(int value);
        [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr g_variant_ref_sink(IntPtr value);
        [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] public static extern void g_variant_unref(IntPtr value);
    }
}
