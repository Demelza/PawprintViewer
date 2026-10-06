using Gtk;

namespace Radegast.Gtk;

/// <summary>Show viewer-owned windows when the viewer has the desktop's focus.</summary>
internal sealed class ChildWindowPresenter : IDisposable
{
    private readonly Window _owner;
    private readonly HashSet<Window> _windows = new();
    private readonly List<Window> _pending = new();
    private bool _checkingForeground;
    private bool _disposed;
    internal bool HasFocus => ViewerHasFocus();

    public ChildWindowPresenter(Window owner)
    {
        _owner = owner;
        owner.Destroyed += OnOwnerDestroyed;
    }

    public void Show(Window window, bool showContents = true)
    {
        if (_disposed) return;
        if (_windows.Add(window)) window.Destroyed += OnChildDestroyed;
        if (showContents) window.Child?.ShowAll();
        // Mapping a transient window can also request focus from the window
        // manager. Only explicitly present it while this viewer is active.
        window.FocusOnMap = false;
        if (ViewerHasFocus())
        {
            _pending.Remove(window);
            window.Show();
            if (ViewerHasFocus()) window.Present();
            return;
        }

        if (!_pending.Contains(window)) _pending.Add(window);
        if (_checkingForeground) return;
        _checkingForeground = true;
        // Poll only while windows are pending. This also detects returning to
        // an existing modal child rather than directly to the main window.
        GLib.Timeout.Add(150, () =>
        {
            if (_disposed || _pending.Count == 0)
            {
                _checkingForeground = false;
                return false;
            }
            if (!ViewerHasFocus()) return true;
            Window? lastShown = null;
            foreach (var pending in _pending.ToArray())
            {
                if (!ViewerHasFocus()) break;
                _pending.Remove(pending);
                pending.Show();
                lastShown = pending;
            }
            if (lastShown != null && ViewerHasFocus()) lastShown.Present();
            _checkingForeground = _pending.Count > 0;
            return _checkingForeground;
        });
    }

    private bool ViewerHasFocus()
    {
        if (_disposed) return false;
        foreach (var window in Window.ListToplevels())
        {
            if (!window.IsActive) continue;
            for (Window? parent = window; parent != null; parent = parent.TransientFor)
                if (parent.Handle == _owner.Handle) return true;
        }
        return false;
    }

    private void OnChildDestroyed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        _pending.Remove(window);
        _windows.Remove(window);
        window.Destroyed -= OnChildDestroyed;
    }

    private void OnOwnerDestroyed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Destroyed -= OnOwnerDestroyed;
        foreach (var window in _windows) window.Destroyed -= OnChildDestroyed;
        _windows.Clear();
        _pending.Clear();
    }
}
