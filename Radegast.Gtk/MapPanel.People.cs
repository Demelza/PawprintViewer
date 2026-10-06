using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class MapPanel
{
    private sealed record Population(MapAvatarMarker[] People, DateTime Received);
    private readonly Dictionary<ulong, Population> _population = new();
    private readonly Dictionary<ulong, DateTime> _populationRequests = new();
    private (ushort MinX, ushort MinY, ushort MaxX, ushort MaxY)? _populationBounds;
    private DateTime _blockRequest, _viewportChanged, _nextPopulationBatch;
    private bool _populationQueued;

    private void SchedulePopulation()
    {
        if (_disposed || !_displayed) return;
        _viewportChanged = DateTime.UtcNow;
        if (_populationQueued) return;
        _populationQueued = true;
        GLib.Timeout.Add(300, () =>
        {
            if (_disposed || !_displayed) { _populationQueued = false; return false; }
            if (DateTime.UtcNow - _viewportChanged < TimeSpan.FromMilliseconds(300)) return true;
            _populationQueued = false;
            RequestPopulation();
            return false;
        });
    }

    private void RequestPopulation()
    {
        if (_disposed || !_displayed || !_session.IsConnected || !_session.CanViewMapPeople || _populationQueued) return;
        var regions = _map.Viewport.VisibleRegions(_map.AllocatedWidth, _map.AllocatedHeight);
        if (regions.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now < _nextPopulationBatch) return;
        var bounds = (regions.Min(region => region.X), regions.Min(region => region.Y),
            regions.Max(region => region.X), regions.Max(region => region.Y));
        if (_populationBounds != bounds || now - _blockRequest > TimeSpan.FromMinutes(1))
        {
            _populationBounds = bounds;
            _blockRequest = now;
            _session.Client.Grid.RequestMapBlocks(GridLayerType.Objects, bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4, false);
        }
        var visible = regions.Select(region => Utils.UIntsToLong((uint)region.X * 256, (uint)region.Y * 256)).ToHashSet();
        foreach (var old in _populationRequests.Keys.Where(handle => !visible.Contains(handle)).ToArray()) _populationRequests.Remove(old);
        var detailed = _session.GetMapAvatarLocations().Select(region => region.Handle).ToHashSet();
        if (_session.Client.Network.CurrentSim is { } sim) detailed.Add(sim.Handle);
        var sent = 0;
        foreach (var (x, y) in regions)
        {
            var handle = Utils.UIntsToLong((uint)x * 256, (uint)y * 256);
            if (detailed.Contains(handle) || !_session.Client.Grid.RegionsByHandleReadOnly.TryGetValue(handle, out var region) ||
                region.Access is SimAccess.NonExistent or SimAccess.Down) continue;
            if (_populationRequests.TryGetValue(handle, out var previous) && now - previous < TimeSpan.FromSeconds(30)) continue;
            _populationRequests[handle] = now;
            // Empty responses have no region identifier. Clear the old dots before
            // refreshing so an empty region does not keep stale occupants forever.
            _population.Remove(handle);
            _session.Client.Grid.RequestMapItems(handle, GridItemType.AgentLocations, GridLayerType.Objects);
            if (++sent == 4) break;
        }
        if (sent > 0) _nextPopulationBatch = now.AddSeconds(1);
    }

    private void OnMapRegion(object? sender, GridRegionEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed && _displayed && !_populationQueued) RequestPopulation(); });

    private void OnMapPopulation(object? sender, GridItemsEventArgs e)
    {
        if (e.Type != GridItemType.AgentLocations) return;
        var points = e.Items.OfType<MapAgentLocation>().Where(point => point.AvatarCount > 0)
            .Select(point => (point.RegionHandle, Marker: new MapAvatarMarker(point.GlobalX, point.GlobalY, point.AvatarCount))).ToArray();
        GtkDispatch.Post(() =>
        {
            if (_disposed || !_displayed || !_session.IsConnected || !_session.CanViewMapPeople) return;
            foreach (var group in points.GroupBy(point => point.RegionHandle))
                if (_populationRequests.ContainsKey(group.Key))
                    _population[group.Key] = new(group.Select(point => point.Marker).ToArray(), DateTime.UtcNow);
            while (_population.Count > 64) _population.Remove(_population.MinBy(pair => pair.Value.Received).Key);
            RefreshPeople();
        });
    }

    private void RefreshPeople()
    {
        if (_disposed || !_displayed) return;
        if (!_session.IsConnected || !_session.CanViewMapPeople) { _map.SetPeople(Array.Empty<MapAvatarMarker>()); return; }
        var snapshots = _session.GetMapAvatarLocations();
        var detailed = snapshots.Select(region => region.Handle).ToHashSet();
        if (_session.Client.Network.CurrentSim is { } sim) detailed.Add(sim.Handle);
        var now = DateTime.UtcNow;
        foreach (var stale in _population.Where(pair => now - pair.Value.Received > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray())
            _population.Remove(stale);
        _map.SetPeople(snapshots.SelectMany(region => region.People)
            .Concat(_population.Where(pair => !detailed.Contains(pair.Key)).SelectMany(pair => pair.Value.People)).ToArray());
    }
}
