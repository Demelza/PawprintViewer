using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Rezzed objects near one account, with touch, sit and stand actions.</summary>
internal sealed class ObjectsPanel : Box
{
    private readonly AccountSession _session;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.None };
    private readonly Dictionary<UUID, ObjectRow> _rows = new();
    private readonly HashSet<UUID> _touching = new();
    private readonly Dictionary<UUID, (DateTime Sent, int Attempts)> _nameRequests = new();
    private readonly Button _stand = new("Stand");
    private readonly Label _status = new("Objects within 50 m") { Xalign = 0 };
    private readonly Label _feedback = new() { Xalign = 0, Wrap = true };
    private readonly CancellationTokenSource _actions = new();
    private bool _displayed;
    private bool _active;
    private bool _busy;
    private bool _refreshQueued;
    private bool _disposed;

    public ObjectsPanel(AccountSession session) : base(Orientation.Vertical, 6)
    {
        _session = session;
        BorderWidth = 8;
        Vexpand = true;
        var toolbar = new Box(Orientation.Horizontal, 6);
        toolbar.PackStart(new Label("Objects within 50 m") { Xalign = 0 }, true, true, 0);
        _stand.Clicked += (_, _) => Stand();
        toolbar.PackStart(_stand, false, false, 0);
        var refresh = new Button("Refresh");
        refresh.Clicked += (_, _) => { _nameRequests.Clear(); Refresh(); };
        toolbar.PackStart(refresh, false, false, 0);
        PackStart(toolbar, false, false, 0);
        var scroll = new ScrolledWindow { Vexpand = true };
        scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        scroll.Add(_list);
        PackStart(scroll, true, true, 0);
        PackStart(_status, false, false, 0);
        PackStart(_feedback, false, false, 0);
        _list.SortFunc = (a, b) =>
        {
            var first = ((ObjectRow)a).Item;
            var second = ((ObjectRow)b).Item;
            var distance = first.Distance.CompareTo(second.Distance);
            return distance != 0 ? distance : StringComparer.CurrentCultureIgnoreCase.Compare(first.Name, second.Name);
        };
        UpdateStand();
    }

    public void SetDisplayed(bool displayed)
    {
        if (_disposed) return;
        _displayed = displayed;
        if (!displayed) return;
        if (!_active)
        {
            _active = true;
            _session.StateChanged += OnStateChanged;
            _session.Client.Objects.ObjectProperties += OnObjectProperties;
            _session.Client.Objects.AvatarSitChanged += OnSeatChanged;
            GLib.Timeout.Add(1000, () =>
            {
                if (_disposed) return false;
                if (_displayed) Refresh();
                return true;
            });
        }
        Refresh();
    }

    private void OnStateChanged(AccountSession session) => ScheduleRefresh();

    private void OnObjectProperties(object? sender, ObjectPropertiesEventArgs e)
    {
        var id = e.Properties.ObjectID;
        GtkDispatch.Post(() => { if (_rows.ContainsKey(id)) ScheduleRefresh(); });
    }

    private void OnSeatChanged(object? sender, AvatarSitChangedEventArgs e)
    {
        if (e.Avatar.ID == _session.Client.Self.AgentID) GtkDispatch.Post(ScheduleRefresh);
    }

    private void ScheduleRefresh()
    {
        if (_disposed || !_displayed || _refreshQueued) return;
        _refreshQueued = true;
        GLib.Timeout.Add(180, () =>
        {
            _refreshQueued = false;
            if (_displayed && !_disposed) Refresh();
            return false;
        });
    }

    private void Refresh()
    {
        if (_disposed || !_displayed) return;
        UpdateStand();
        _feedback.Text = _session.RedactText(_feedback.Text);
        var items = _session.GetNearbyObjects();
        var ids = items.Select(item => item.Id).ToHashSet();
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var row = _rows[id];
            _list.Remove(row);
            row.Dispose();
            _rows.Remove(id);
            _nameRequests.Remove(id);
        }
        foreach (var item in items)
        {
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                row = new ObjectRow(item, Touch, Sit);
                _rows.Add(item.Id, row);
                _list.Add(row);
            }
            row.Update(item, _touching.Contains(item.Id) ? "Touch is being sent." : _session.ObjectTouchError(item),
                _busy ? "Another sit request is pending." : _session.ObjectSitError(item), _session.RedactText(item.Name));
        }
        _list.InvalidateSort();
        _list.ShowAll();
        _status.Text = !_session.IsConnected ? "Connect to see nearby objects." : items.Count == 0
            ? "No rezzed objects found within 50 m."
            : $"{items.Count} object{(items.Count == 1 ? "" : "s")} within 50 m · nearest first";
        RequestNames(items);
    }

    private void RequestNames(IReadOnlyList<NearbyObject> items)
    {
        var now = DateTime.UtcNow;
        foreach (var group in items.Where(item => !item.HasName).GroupBy(item => item.Simulator))
        {
            var request = new List<uint>();
            foreach (var item in group)
            {
                _nameRequests.TryGetValue(item.Id, out var previous);
                if (previous.Attempts >= 3 || now - previous.Sent < TimeSpan.FromSeconds(10)) continue;
                _nameRequests[item.Id] = (now, previous.Attempts + 1);
                request.Add(item.LocalId);
                if (request.Count == 50) break;
            }
            if (request.Count > 0) _session.Client.Objects.SelectObjects(group.Key, request.ToArray(), true);
        }
    }

    private void UpdateStand()
    {
        var error = _busy ? "A sit request is pending." : _session.StandError;
        _stand.Sensitive = error == null;
        _stand.TooltipText = error ?? "Stand up from the current seat";
    }

    private async void Sit(NearbyObject item)
    {
        if (_disposed || _busy) return;
        _busy = true;
        _feedback.Text = $"Requesting a seat on {_session.RedactText(item.Name)}…";
        Refresh();
        try
        {
            await _session.SitOnObjectAsync(item, _actions.Token).ConfigureAwait(false);
            GtkDispatch.Post(() => { if (!_disposed) _feedback.Text = $"Sitting on {_session.RedactText(item.Name)}."; });
        }
        catch (OperationCanceledException)
        {
            GtkDispatch.Post(() => { if (!_disposed) _feedback.Text = "Sit request canceled."; });
        }
        catch (Exception ex)
        {
            GtkDispatch.Post(() => { if (!_disposed) _feedback.Text = $"Sit failed: {ex.Message}"; });
        }
        finally
        {
            GtkDispatch.Post(() => { if (!_disposed) { _busy = false; Refresh(); } });
        }
    }

    private async void Touch(NearbyObject item)
    {
        if (_disposed || !_touching.Add(item.Id)) return;
        Refresh();
        try
        {
            await _session.TouchObjectAsync(item, _actions.Token).ConfigureAwait(false);
            GtkDispatch.Post(() => { if (!_disposed) _feedback.Text = $"Touch sent to {_session.RedactText(item.Name)}."; });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GtkDispatch.Post(() => { if (!_disposed) _feedback.Text = $"Touch failed: {ex.Message}"; });
        }
        finally
        {
            GtkDispatch.Post(() => { if (!_disposed) { _touching.Remove(item.Id); Refresh(); } });
        }
    }

    private void Stand()
    {
        try { _session.StandUp(); _feedback.Text = "Stand requested."; }
        catch (Exception ex) { _feedback.Text = $"Stand failed: {ex.Message}"; }
        Refresh();
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _actions.Cancel();
        _actions.Dispose();
        _session.StateChanged -= OnStateChanged;
        _session.Client.Objects.ObjectProperties -= OnObjectProperties;
        _session.Client.Objects.AvatarSitChanged -= OnSeatChanged;
    }

    private sealed class ObjectRow : ListBoxRow
    {
        private readonly Label _name = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
        private readonly Label _distance = new() { Xalign = 1 };
        private readonly Button _touch = new("Touch");
        private readonly Button _sit = new("Sit");
        public NearbyObject Item { get; private set; }

        public ObjectRow(NearbyObject item, Action<NearbyObject> touch, Action<NearbyObject> sit)
        {
            Item = item;
            var content = new Box(Orientation.Horizontal, 10) { Margin = 4 };
            _touch.Clicked += (_, _) => touch(Item);
            _sit.Clicked += (_, _) => sit(Item);
            content.PackStart(_touch, false, false, 0);
            content.PackStart(_sit, false, false, 0);
            content.PackStart(_name, true, true, 0);
            content.PackStart(_distance, false, false, 0);
            Add(content);
        }

        public void Update(NearbyObject item, string? touchError, string? sitError, string name)
        {
            Item = item;
            _name.Text = name;
            _name.TooltipText = name;
            _distance.Text = $"{item.Distance:0.0} m";
            _touch.Sensitive = touchError == null;
            _touch.TooltipText = touchError ?? $"Touch {name}";
            _sit.Sensitive = sitError == null;
            _sit.TooltipText = sitError ?? $"Sit on {name}";
        }
    }
}
