using Gtk;
using LibreMetaverse;
using System.Globalization;

namespace Radegast.Gtk;

internal sealed class MapPanel : Box
{
    private readonly AccountSession _session;
    private readonly Box _controls = new(Orientation.Vertical, 8) { Margin = 8 };
    private readonly Entry _region = new() { WidthChars = 12, PlaceholderText = "Region name" };
    private readonly Button _search = new() { Image = new Image("system-search-symbolic", IconSize.Button), TooltipText = "Find region" };
    private readonly Entry _x = Coordinate("128");
    private readonly Entry _y = Coordinate("128");
    private readonly Entry _z = Coordinate("20");
    private readonly Button _teleport = new("Teleport");
    private readonly Label _status = new() { Xalign = 0, Wrap = true, MaxWidthChars = 26 };
    private readonly MapCanvas _map = new();
    private readonly Stack _pages = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _lookup;
    private GridRegion? _destination;
    private bool _displayed, _disposed, _busy, _updating;
    private ulong? _avatarRegion;
    private int _mapHeight = 128;

    public MapPanel(AccountSession session) : base(Orientation.Horizontal, 0)
    {
        _session = session;
        Vexpand = true;
        _controls.PackStart(new Label("Region") { Xalign = 0 }, false, false, 0);
        var searchRow = new Box(Orientation.Horizontal, 6);
        searchRow.PackStart(_region, true, true, 0);
        searchRow.PackStart(_search, false, false, 0);
        _controls.PackStart(searchRow, false, false, 0);
        var coordinates = new Box(Orientation.Horizontal, 4);
        foreach (var (name, field) in new[] { ("X", _x), ("Y", _y), ("Z", _z) })
        {
            coordinates.PackStart(new Label(name), false, false, 0);
            coordinates.PackStart(field, true, true, 0);
            field.Changed += (_, _) => UpdateTarget();
        }
        _controls.PackStart(coordinates, false, false, 0);
        _controls.PackStart(_teleport, false, false, 0);
        _controls.PackStart(_status, false, false, 0);
        _controls.PackEnd(new Label("Drag to pan · Scroll to zoom\nGreen: avatar · Gold: destination")
            { Xalign = 0, Wrap = true, MaxWidthChars = 26 }, false, false, 0);
        var content = new Box(Orientation.Horizontal, 0);
        content.PackStart(_controls, true, true, 0);
        content.PackEnd(_map, false, true, 0);
        // Allocate the square explicitly: GTK's ordinary horizontal box would stretch it.
        content.SizeAllocated += (_, args) => AllocateMap(args.Allocation);
        _pages.AddNamed(content, "map");
        _pages.AddNamed(new Label("The map is hidden by an RLV restriction."), "restricted");
        PackStart(_pages, true, true, 0);
        _region.Changed += (_, _) =>
        {
            if (_updating) return;
            CancelLookup();
            _destination = null;
            _map.SetTarget(null, null);
            UpdateButtons();
        };
        _search.Clicked += (_, _) => Search();
        _region.Activated += (_, _) => Search();
        _teleport.Clicked += (_, _) => Teleport();
        _session.StateChanged += OnStateChanged;
        _session.Rlv.Changed += UpdateRestrictions;
        GLib.Timeout.Add(1000, () =>
        {
            if (_disposed) return false;
            if (_displayed) UpdateAvatar();
            return true;
        });
        UpdateRestrictions();
    }

    private static Entry Coordinate(string value) => new()
    {
        Text = value, WidthChars = 4, MaxWidthChars = 5, InputPurpose = InputPurpose.Number
    };

    protected override SizeRequestMode OnGetRequestMode() => SizeRequestMode.WidthForHeight;

    protected override void OnGetPreferredWidth(out int minimumWidth, out int naturalWidth) =>
        OnGetPreferredWidthForHeight(_mapHeight, out minimumWidth, out naturalWidth);

    protected override void OnSizeAllocated(Gdk.Rectangle allocation)
    {
        base.OnSizeAllocated(allocation);
        if (_mapHeight == allocation.Height) return;
        _mapHeight = allocation.Height;
        // Gtk.Window queries width without height when setting its minimum size.
        // Include the allocated height so its fixed side rails can retain 15% each.
        QueueResize();
    }

    protected override void OnGetPreferredWidthForHeight(int height, out int minimumWidth, out int naturalWidth)
    {
        _controls.GetPreferredWidth(out var controlsWidth, out _);
        minimumWidth = naturalWidth = controlsWidth + Math.Max(128, height);
    }

    private void AllocateMap(Gdk.Rectangle allocation)
    {
        if (_disposed) return;
        _controls.GetPreferredWidth(out var minimum, out _);
        var size = Math.Max(1, Math.Min(allocation.Height, allocation.Width - minimum));
        _controls.SizeAllocate(new Gdk.Rectangle(allocation.X, allocation.Y, allocation.Width - size, allocation.Height));
        _map.SizeAllocate(new Gdk.Rectangle(allocation.X + allocation.Width - size, allocation.Y, size, size));
    }

    public void SetDisplayed(bool displayed)
    {
        if (_disposed || _displayed == displayed) return;
        _displayed = displayed;
        if (!displayed) CancelLookup();
        UpdateRestrictions();
        if (displayed) CenterOnAvatar();
    }

    private void OnStateChanged(AccountSession session)
    {
        if (_disposed) return;
        UpdateRestrictions();
        if (_displayed) UpdateAvatar();
    }

    private void UpdateRestrictions()
    {
        if (_disposed) return;
        var allowed = _session.CanViewWorldMap;
        _pages.VisibleChildName = allowed ? "map" : "restricted";
        _map.SetActive(_displayed && _session.IsConnected && allowed, _session.MapTileServer);
        if (!allowed || !_session.IsConnected)
        {
            CancelLookup();
            _destination = null;
            _avatarRegion = null;
            _updating = true;
            _region.Text = string.Empty;
            _x.Text = _y.Text = _z.Text = "0";
            _updating = false;
            _map.SetAvatar(null, null);
            _map.SetTarget(null, null);
            _status.Text = allowed ? "Connect to use the map." : string.Empty;
        }
        else if (_displayed && _avatarRegion == null) CenterOnAvatar();
        UpdateButtons();
    }

    private void CenterOnAvatar()
    {
        if (!_session.IsConnected || !_session.CanViewWorldMap || _session.Client.Network.CurrentSim is not { } sim) return;
        CancelLookup();
        var position = _session.Client.Self.SimPosition;
        Utils.LongToUInts(sim.Handle, out var originX, out var originY);
        _destination = new GridRegion { Name = sim.Name, RegionHandle = sim.Handle, Access = sim.Access };
        _avatarRegion = sim.Handle;
        _updating = true;
        _region.Text = sim.Name;
        _x.Text = position.X.ToString("0", CultureInfo.InvariantCulture);
        _y.Text = position.Y.ToString("0", CultureInfo.InvariantCulture);
        _z.Text = position.Z.ToString("0", CultureInfo.InvariantCulture);
        _updating = false;
        _map.SetAvatar((double)originX + position.X, (double)originY + position.Y);
        _map.CenterOn((double)originX + position.X, (double)originY + position.Y);
        _status.Text = string.Empty;
        UpdateTarget();
    }

    private void UpdateAvatar()
    {
        if (_disposed || !_session.IsConnected || !_session.CanViewWorldMap ||
            _session.Client.Network.CurrentSim is not { } sim) return;
        if (_avatarRegion != sim.Handle) { CenterOnAvatar(); return; }
        var position = _session.Client.Self.SimPosition;
        Utils.LongToUInts(sim.Handle, out var x, out var y);
        _map.SetAvatar((double)x + position.X, (double)y + position.Y);
    }

    private void UpdateTarget()
    {
        if (_disposed || _updating) return;
        if (_destination is { } destination && _session.CanViewWorldMap && TryReadPosition(out var position))
        {
            Utils.LongToUInts(destination.RegionHandle, out var x, out var y);
            _map.SetTarget((double)x + position.X, (double)y + position.Y);
        }
        else _map.SetTarget(null, null);
        UpdateButtons();
    }

    private bool TryReadPosition(out Vector3 position)
    {
        position = default;
        if (!float.TryParse(_x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(_y.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(_z.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        position = new(x, y, z);
        return true;
    }

    private void UpdateButtons()
    {
        var enabled = !_busy && _session.IsConnected && _session.CanViewWorldMap;
        _region.Sensitive = _x.Sensitive = _y.Sensitive = _z.Sensitive = enabled;
        _search.Sensitive = enabled && !string.IsNullOrWhiteSpace(_region.Text);
        var error = !TryReadPosition(out var position) ? "Enter valid X, Y and Z numbers." :
            _destination is { } region ? _session.MapTeleportError(region, position) : null;
        _teleport.Sensitive = enabled && !string.IsNullOrWhiteSpace(_region.Text) && error == null;
        _teleport.TooltipText = error ?? "Teleport to the entered region and local X, Y, Z coordinates";
    }

    private async void Search()
    {
        if (_disposed || _busy || !_search.Sensitive) return;
        CancelLookup();
        var lookup = _lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var name = _region.Text.Trim();
        _status.Text = "Finding region…";
        try
        {
            var region = await _session.FindMapRegionAsync(name, lookup.Token).ConfigureAwait(false);
            GtkDispatch.Post(() =>
            {
                if (_disposed || lookup.IsCancellationRequested || _lookup != lookup || !_displayed ||
                    !_session.IsConnected || !_session.CanViewWorldMap) return;
                _destination = region;
                _updating = true;
                _region.Text = region.Name;
                _updating = false;
                Utils.LongToUInts(region.RegionHandle, out var x, out var y);
                _map.CenterOn(x + 128, y + 128);
                _status.Text = string.Empty;
                UpdateTarget();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GtkDispatch.Post(() =>
            {
                if (!_disposed && !lookup.IsCancellationRequested && _lookup == lookup)
                    _status.Text = _session.RedactText(ex.Message);
            });
        }
    }

    private async void Teleport()
    {
        if (_disposed || _busy || !_teleport.Sensitive) return;
        if (!TryReadPosition(out var position)) return;
        var name = _region.Text.Trim();
        var destination = _destination;
        CancelLookup();
        _busy = true;
        _status.Text = "Teleporting…";
        UpdateButtons();
        try
        {
            var region = destination ?? await _session.FindMapRegionAsync(name, _lifetime.Token).ConfigureAwait(false);
            await _session.TeleportMapAsync(region, position, _lifetime.Token).ConfigureAwait(false);
            GtkDispatch.Post(() =>
            {
                if (_disposed) return;
                if (_displayed) CenterOnAvatar();
                _status.Text = _session.CanViewWorldMap ? "Teleport complete." : string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
            GtkDispatch.Post(() => { if (!_disposed && _session.CanViewWorldMap) _status.Text = "Teleport canceled."; });
        }
        catch (Exception ex)
        {
            GtkDispatch.Post(() => { if (!_disposed) _status.Text = _session.RedactText($"Teleport failed: {ex.Message}"); });
        }
        finally
        {
            GtkDispatch.Post(() => { if (!_disposed) { _busy = false; UpdateButtons(); } });
        }
    }

    private void CancelLookup()
    {
        _lookup?.Cancel();
        _lookup?.Dispose();
        _lookup = null;
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StateChanged -= OnStateChanged;
        _session.Rlv.Changed -= UpdateRestrictions;
        CancelLookup();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _map.Stop();
    }
}
