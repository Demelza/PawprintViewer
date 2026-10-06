using Cairo;
using Gdk;
using Gtk;

namespace Radegast.Gtk;

internal sealed record MapRegionLabel(double X, double Y, string Name, int? AvatarCount, uint SizeX = 256, uint SizeY = 256)
{
    public string Text => AvatarCount is { } count ? $"{Name} - {count}" : Name;
}

/// <summary>A bounded tile cache and a CPU-drawn, north-up world map.</summary>
internal sealed class MapCanvas : DrawingArea
{
    private sealed record TileImage(Pixbuf? Image, DateTime RetryAfter)
    {
        public long Used { get; set; }
    }

    private const int CacheLimit = 64;
    private readonly Dictionary<MapTile, TileImage> _images = new();
    private readonly HashSet<MapTile> _loading = new();
    private CancellationTokenSource _downloads = new();
    private Uri? _server;
    private bool _active;
    private bool _disposed;
    private bool _queued;
    private bool _dragging;
    private bool _moved;
    private double _pressX, _pressY, _dragX, _dragY;
    private long _generation, _used;
    private (double X, double Y)? _avatar, _target;
    private IReadOnlyList<MapAvatarMarker> _people = Array.Empty<MapAvatarMarker>();
    private IReadOnlyList<MapRegionLabel> _regions = Array.Empty<MapRegionLabel>();

    public WorldMapViewport Viewport { get; } = new();
    public event Action<double, double>? PointSelected;
    public event System.Action? ViewportChanged;

    public MapCanvas()
    {
        WidthRequest = HeightRequest = 128;
        Vexpand = true;
        CanFocus = true;
        AddEvents((int)(EventMask.ButtonPressMask | EventMask.ButtonReleaseMask |
            EventMask.PointerMotionMask | EventMask.ScrollMask | EventMask.SmoothScrollMask));
        SizeAllocated += (_, _) => { ScheduleTiles(); ViewportChanged?.Invoke(); };
    }

    public void SetActive(bool active, Uri? server)
    {
        if (_disposed) return;
        if (!Equals(server, _server))
        {
            CancelDownloads();
            foreach (var tile in _images.Values) tile.Image?.Dispose();
            _images.Clear();
            _server = server;
            _dragging = false;
        }
        if (_active && !active) CancelDownloads();
        if (_active != active) _dragging = false;
        _active = active;
        if (active) ScheduleTiles();
        QueueDraw();
    }

    public void CenterOn(double x, double y)
    {
        Viewport.Center(x, y);
        ScheduleTiles();
        QueueDraw();
        ViewportChanged?.Invoke();
    }

    public void SetAvatar(double? x, double? y)
    {
        _avatar = x.HasValue && y.HasValue ? (x.Value, y.Value) : null;
        QueueDraw();
    }

    public void SetTarget(double? x, double? y)
    {
        _target = x.HasValue && y.HasValue ? (x.Value, y.Value) : null;
        QueueDraw();
    }

    public void SetPeople(IReadOnlyList<MapAvatarMarker> people)
    {
        _people = people;
        QueueDraw();
    }

    public void SetRegions(IReadOnlyList<MapRegionLabel> regions)
    {
        _regions = regions;
        QueueDraw();
    }

    protected override bool OnButtonPressEvent(EventButton evnt)
    {
        if (!_active || evnt.Button != 1) return base.OnButtonPressEvent(evnt);
        GrabFocus();
        _dragging = true;
        _moved = false;
        _pressX = evnt.X;
        _pressY = evnt.Y;
        _dragX = evnt.X;
        _dragY = evnt.Y;
        return true;
    }

    protected override bool OnButtonReleaseEvent(EventButton evnt)
    {
        if (evnt.Button != 1) return base.OnButtonReleaseEvent(evnt);
        var clicked = _active && _dragging && !_moved &&
            !global::Gtk.Drag.CheckThreshold(this, (int)_pressX, (int)_pressY, (int)evnt.X, (int)evnt.Y);
        _dragging = false;
        if (clicked && evnt.X >= 0 && evnt.X < AllocatedWidth && evnt.Y >= 0 && evnt.Y < AllocatedHeight)
        {
            var point = Viewport.ToWorld(evnt.X, evnt.Y, AllocatedWidth, AllocatedHeight);
            PointSelected?.Invoke(point.X, point.Y);
        }
        return true;
    }

    protected override bool OnMotionNotifyEvent(EventMotion evnt)
    {
        if (!_active || !_dragging) return base.OnMotionNotifyEvent(evnt);
        if ((evnt.State & ModifierType.Button1Mask) == 0) { _dragging = false; return false; }
        if (!_moved)
        {
            if (!global::Gtk.Drag.CheckThreshold(this, (int)_pressX, (int)_pressY, (int)evnt.X, (int)evnt.Y)) return true;
            _moved = true;
        }
        Viewport.Pan(evnt.X - _dragX, evnt.Y - _dragY);
        _dragX = evnt.X;
        _dragY = evnt.Y;
        ScheduleTiles();
        QueueDraw();
        ViewportChanged?.Invoke();
        return true;
    }

    protected override bool OnScrollEvent(EventScroll evnt)
    {
        if (!_active) return base.OnScrollEvent(evnt);
        var steps = evnt.Direction switch { ScrollDirection.Up => 1d, ScrollDirection.Down => -1d, _ => 0d };
        if (evnt.Direction == ScrollDirection.Smooth) steps = -evnt.DeltaY;
        if (steps == 0) return false;
        if (_dragging) _moved = true;
        Viewport.Zoom(steps, evnt.X, evnt.Y, AllocatedWidth, AllocatedHeight);
        ScheduleTiles();
        QueueDraw();
        ViewportChanged?.Invoke();
        return true;
    }

    protected override bool OnDrawn(Context context)
    {
        var width = AllocatedWidth;
        var height = AllocatedHeight;
        context.SetSourceRGB(0.12, 0.22, 0.29);
        context.Paint();
        if (!_active) return true;
        foreach (var tile in Viewport.VisibleTiles(width, height))
        {
            var (x, y) = Viewport.ToScreen(tile.X * 256d, tile.Y * 256d + tile.Meters, width, height);
            var size = tile.Meters * Viewport.Scale;
            if (_images.TryGetValue(tile, out var entry) && entry.Image is { } image)
            {
                entry.Used = ++_used;
                context.Save();
                context.Rectangle(x, y, size, size);
                context.Clip();
                context.Translate(x, y);
                context.Scale(size / image.Width, size / image.Height);
                Gdk.CairoHelper.SetSourcePixbuf(context, image, 0, 0);
                context.Paint();
                context.Restore();
            }
        }
        DrawGrid(context, width, height);
        foreach (var region in _regions) DrawRegionLabel(context, region, width, height);
        foreach (var person in _people) DrawPerson(context, person, width, height);
        if (_target is { } target) DrawMarker(context, target, width, height, false);
        if (_avatar is { } avatar) DrawMarker(context, avatar, width, height, true);
        // Labels use Pango and therefore the system font, including its scale setting.
        Caption(context, "N ↑", 8, 8);
        var meters = Math.Pow(10, Math.Floor(Math.Log10(100 / Viewport.Scale)));
        if (meters * Viewport.Scale < 40) meters *= 2;
        context.SetSourceRGB(1, 1, 1);
        context.LineWidth = 2;
        var scaleX = width - 12 - meters * Viewport.Scale;
        context.MoveTo(scaleX, 32);
        context.LineTo(width - 12, 32);
        context.Stroke();
        Caption(context, $"{meters:0} m", scaleX, 8);
        if (_server == null) Caption(context, "This grid did not provide a map tile service.", 8, 38);
        else if (_images.Count == 0) Caption(context, "Loading map…", 8, 38);
        return true;
    }

    private void DrawGrid(Context context, double width, double height)
    {
        var spacing = 256 * Viewport.Scale;
        if (spacing < 48) return;
        var bottomLeft = Viewport.ToWorld(0, height, width, height);
        var topRight = Viewport.ToWorld(width, 0, width, height);
        context.SetSourceRGBA(1, 1, 1, 0.16);
        context.LineWidth = 1;
        for (var x = Math.Ceiling(bottomLeft.X / 256) * 256; x <= topRight.X; x += 256)
        {
            var px = Viewport.ToScreen(x, 0, width, height).X;
            context.MoveTo(px, 0); context.LineTo(px, height);
        }
        for (var y = Math.Ceiling(bottomLeft.Y / 256) * 256; y <= topRight.Y; y += 256)
        {
            var py = Viewport.ToScreen(0, y, width, height).Y;
            context.MoveTo(0, py); context.LineTo(width, py);
        }
        context.Stroke();
    }

    private void DrawMarker(Context context, (double X, double Y) point, int width, int height, bool avatar)
    {
        var (x, y) = Viewport.ToScreen(point.X, point.Y, width, height);
        if (x < -10 || x > width + 10 || y < -10 || y > height + 10) return;
        context.SetSourceRGB(0, 0, 0);
        context.Arc(x, y, avatar ? 6 : 8, 0, Math.PI * 2);
        context.Fill();
        context.SetSourceRGB(avatar ? 0.3 : 1, avatar ? 1 : 0.75, avatar ? 0.4 : 0.15);
        context.Arc(x, y, avatar ? 4 : 6, 0, Math.PI * 2);
        if (avatar) context.Fill(); else { context.LineWidth = 2; context.Stroke(); }
    }

    private void DrawRegionLabel(Context context, MapRegionLabel region, int width, int height)
    {
        if (region.SizeX * Viewport.Scale < 64) return;
        var (left, bottom) = Viewport.ToScreen(region.X, region.Y, width, height);
        var (right, top) = Viewport.ToScreen(region.X + region.SizeX, region.Y + region.SizeY, width, height);
        // Keep partially visible regions' labels inside their visible portion.
        left = Math.Max(0, left);
        bottom = Math.Min(height, bottom);
        right = Math.Min(width, right);
        top = Math.Max(0, top);
        if (right - left < 64 || bottom <= top) return;
        using var layout = CreatePangoLayout(region.Text);
        layout.Width = (int)((right - left - 12) * Pango.Scale.PangoScale);
        layout.Ellipsize = Pango.EllipsizeMode.Middle; // Preserve the count after long region names.
        layout.SingleParagraphMode = true;
        layout.GetPixelSize(out var textWidth, out var textHeight);
        if (bottom - top < textHeight + 8) return;
        var x = left + 6;
        var y = bottom - textHeight - 6;
        context.SetSourceRGBA(0, 0, 0, 0.65);
        context.Rectangle(x - 2, y - 2, textWidth + 4, textHeight + 4);
        context.Fill();
        context.MoveTo(x, y);
        context.SetSourceRGB(1, 1, 1);
        Pango.CairoHelper.ShowLayout(context, layout);
    }

    private void DrawPerson(Context context, MapAvatarMarker person, int width, int height)
    {
        var (x, y) = Viewport.ToScreen(person.X, person.Y, width, height);
        var radius = person.Count > 1 ? 5 : 3;
        if (x < -6 || x > width + 6 || y < -6 || y > height + 6) return;
        context.SetSourceRGB(0, 0, 0);
        context.Arc(x, y, radius + 1, 0, Math.PI * 2);
        context.Fill();
        context.SetSourceRGB(0.3, 0.65, 1);
        context.Arc(x, y, radius, 0, Math.PI * 2);
        context.Fill();
    }

    private void Caption(Context context, string text, double x, double y)
    {
        using var layout = CreatePangoLayout(text);
        layout.Width = (int)(Math.Max(1, AllocatedWidth - x - 8) * Pango.Scale.PangoScale);
        layout.Ellipsize = Pango.EllipsizeMode.End;
        context.MoveTo(x + 1, y + 1);
        context.SetSourceRGB(0, 0, 0);
        Pango.CairoHelper.ShowLayout(context, layout);
        context.MoveTo(x, y);
        context.SetSourceRGB(1, 1, 1);
        Pango.CairoHelper.ShowLayout(context, layout);
    }

    private void ScheduleTiles()
    {
        if (_disposed || !_active || _server == null || _queued) return;
        _queued = true;
        GLib.Timeout.Add(100, () =>
        {
            _queued = false;
            if (!_disposed && _active) LoadVisibleTiles();
            return false;
        });
    }

    private void LoadVisibleTiles()
    {
        if (_disposed || !_active || _server == null) return;
        foreach (var tile in Viewport.VisibleTiles(AllocatedWidth, AllocatedHeight))
        {
            if (_loading.Count >= 4) break;
            if (_loading.Contains(tile)) continue;
            if (_images.TryGetValue(tile, out var cached))
            {
                if (cached.Image != null || cached.RetryAfter > DateTime.UtcNow) continue;
                _images.Remove(tile);
            }
            _loading.Add(tile);
            _ = FetchTileAsync(tile, _server, _generation, _downloads.Token);
        }
    }

    private async Task FetchTileAsync(MapTile tile, Uri server, long generation, CancellationToken token)
    {
        byte[]? bytes = null;
        Exception? error = null;
        try { bytes = await WorldMapTileSource.DownloadAsync(server, tile, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { error = ex; }
        GtkDispatch.Post(() =>
        {
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            _loading.Remove(tile);
            Pixbuf? image = null;
            try
            {
                if (bytes != null)
                {
                    image = new Pixbuf(bytes);
                    if (image.Width != 256 || image.Height != 256)
                        throw new IOException("Map tile must be 256 × 256 pixels.");
                }
            }
            catch (Exception ex) { image?.Dispose(); image = null; error = ex; }
            _images[tile] = new(image, DateTime.UtcNow.AddSeconds(error == null ? 600 : 30)) { Used = ++_used };
            var visible = Viewport.VisibleTiles(AllocatedWidth, AllocatedHeight).ToHashSet();
            // Keep every visible tile on large displays; evicting one would request it
            // again on the next pass. Off-screen history remains bounded.
            while (_images.Count > Math.Max(CacheLimit, visible.Count))
            {
                var victim = _images.OrderBy(pair => visible.Contains(pair.Key)).ThenBy(pair => pair.Value.Used).First();
                victim.Value.Image?.Dispose();
                _images.Remove(victim.Key);
            }
            QueueDraw();
            ScheduleTiles();
        });
    }

    private void CancelDownloads()
    {
        _generation++;
        _downloads.Cancel();
        _downloads.Dispose();
        _downloads = new();
        _loading.Clear();
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        _downloads.Cancel();
        _downloads.Dispose();
        foreach (var tile in _images.Values) tile.Image?.Dispose();
        _images.Clear();
    }
}
