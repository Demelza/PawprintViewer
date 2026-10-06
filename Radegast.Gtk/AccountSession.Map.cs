using LibreMetaverse;
using System.Collections.Concurrent;

namespace Radegast.Gtk;

internal readonly record struct MapAvatarMarker(double X, double Y, int Count = 1);
internal sealed record MapAvatarRegion(ulong Handle, IReadOnlyList<MapAvatarMarker> People);

internal sealed partial class AccountSession
{
    private readonly ConcurrentDictionary<Simulator, KeyValuePair<UUID, Vector3>[]> _mapAvatarPositions = new();
    private int _mapTeleportInProgress;
    public Uri? MapTileServer { get; private set; }
    public bool CanViewWorldMap => !Rlv.Enabled ||
        (Rlv.Service.Permissions.CanShowWorldMap() && Rlv.Service.Permissions.CanShowLoc());
    public bool CanViewMapPeople => CanViewWorldMap && (!Rlv.Enabled || Rlv.Service.Permissions.CanShowNearby());

    private void RecordMapAvatarPositions(Simulator sim, KeyValuePair<UUID, Vector3>[] positions)
    {
        if (_disposed) return;
        _mapAvatarPositions[sim] = positions;
        foreach (var previous in _mapAvatarPositions.Keys)
            if (previous != Client.Network.CurrentSim && !previous.Connected) _mapAvatarPositions.TryRemove(previous, out _);
    }

    public IReadOnlyList<MapAvatarRegion> GetMapAvatarLocations()
    {
        if (_disposed || !IsConnected || !CanViewMapPeople) return Array.Empty<MapAvatarRegion>();
        var result = new List<MapAvatarRegion>();
        var seen = new HashSet<UUID> { Client.Self.AgentID };
        foreach (var (sim, positions) in _mapAvatarPositions.OrderByDescending(pair => pair.Key == Client.Network.CurrentSim))
        {
            if (sim != Client.Network.CurrentSim && !sim.Connected) continue;
            Utils.LongToUInts(sim.Handle, out var x, out var y);
            var people = new List<MapAvatarMarker>();
            foreach (var (id, position) in positions)
                if (float.IsFinite(position.X) && float.IsFinite(position.Y) && position.X >= 0 && position.X < sim.SizeX &&
                    position.Y >= 0 && position.Y < sim.SizeY && id != UUID.Zero && seen.Add(id))
                    people.Add(new((double)x + position.X, (double)y + position.Y));
            result.Add(new(sim.Handle, people));
        }
        return result;
    }

    public async Task<GridRegion> FindMapRegionAtAsync(double x, double y, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected || Client.Network.CurrentSim is not { } sim)
            throw new InvalidOperationException("This account is disconnected.");
        if (!CanViewWorldMap) throw new InvalidOperationException("The map is hidden by RLV.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= 65536d * 256 || y >= 65536d * 256)
            throw new InvalidOperationException("This point is outside the world map.");
        Utils.LongToUInts(sim.Handle, out var simX, out var simY);
        var handle = Utils.UIntsToLong((uint)Math.Floor(x / 256) * 256, (uint)Math.Floor(y / 256) * 256);
        GridRegion? found = x >= simX && x < (double)simX + sim.SizeX && y >= simY && y < (double)simY + sim.SizeY
            ? new GridRegion { Name = sim.Name, RegionHandle = sim.Handle, Access = sim.Access }
            : await Client.Grid.GetGridRegionAsync(handle, GridLayerType.Objects, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected) throw new InvalidOperationException("This account is disconnected.");
        if (!CanViewWorldMap) throw new InvalidOperationException("The map is hidden by RLV.");
        if (found is not { } region || region.Access == SimAccess.NonExistent || string.IsNullOrWhiteSpace(region.Name))
            throw new InvalidOperationException("There is no region at this point.");
        return region;
    }

    public async Task<GridRegion> FindMapRegionAsync(string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected || Client.Network.CurrentSim is not { } sim)
            throw new InvalidOperationException("This account is disconnected.");
        if (!CanViewWorldMap) throw new InvalidOperationException("The map is hidden by RLV.");
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Enter a region name.");
        GridRegion? found = string.Equals(name, sim.Name, StringComparison.OrdinalIgnoreCase)
            ? new GridRegion { Name = sim.Name, RegionHandle = sim.Handle, Access = sim.Access }
            : await Client.Grid.GetGridRegionAsync(name, GridLayerType.Objects, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected) throw new InvalidOperationException("This account is disconnected.");
        if (!CanViewWorldMap) throw new InvalidOperationException("The map is hidden by RLV.");
        if (found is not { } region || region.Access == SimAccess.NonExistent ||
            !string.Equals(name, region.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Region ‘{name}’ was not found.");
        return region;
    }

    public string? MapTeleportError(GridRegion region, Vector3 position)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim is not { } sim)
            return "This account is disconnected.";
        if (!CanViewWorldMap) return "The map is hidden by RLV.";
        if (string.IsNullOrWhiteSpace(region.Name) || region.Access == SimAccess.NonExistent)
            return "Select an existing region.";
        var sizeX = region.RegionHandle == sim.Handle ? sim.SizeX : 256;
        var sizeY = region.RegionHandle == sim.Handle ? sim.SizeY : 256;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            position.X < 0 || position.X >= sizeX || position.Y < 0 || position.Y >= sizeY || position.Z < 0)
            return $"X must be between 0 and {sizeX - 1}, Y between 0 and {sizeY - 1}, and Z at least 0.";
        if (Rlv.Enabled)
        {
            var permissions = Rlv.Service.Permissions;
            if (!permissions.CanTpLoc()) return "Teleporting to a location is restricted by RLV.";
            if (IsSitting && !permissions.CanUnsit()) return "Leaving the current seat is restricted by RLV.";
            if (region.RegionHandle == sim.Handle && permissions.CanTpLocal(out var distance) &&
                Vector3.Distance(position, Client.Self.SimPosition) > distance)
                return "Teleporting this distance is restricted by RLV.";
        }
        return null;
    }

    public async Task TeleportMapAsync(GridRegion region, Vector3 position, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (MapTeleportError(region, position) is { } error) throw new InvalidOperationException(error);
        if (Interlocked.CompareExchange(ref _mapTeleportInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A teleport request is already pending.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Disconnected(object? sender, DisconnectedEventArgs e) => cancel.Cancel();
        void RestrictionsChanged() { if (MapTeleportError(region, position) != null) cancel.Cancel(); }
        Client.Network.Disconnected += Disconnected;
        Rlv.Changed += RestrictionsChanged;
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (MapTeleportError(region, position) is { } changed) throw new InvalidOperationException(changed);
            if (!await Client.Self.TeleportAsync(region.RegionHandle, position, cancel.Token).ConfigureAwait(false))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(Client.Self.TeleportMessage)
                    ? "The server did not complete the teleport." : Client.Self.TeleportMessage);
        }
        finally
        {
            Client.Network.Disconnected -= Disconnected;
            Rlv.Changed -= RestrictionsChanged;
            Interlocked.Exchange(ref _mapTeleportInProgress, 0);
        }
    }
}
