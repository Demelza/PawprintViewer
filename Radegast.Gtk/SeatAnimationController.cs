using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Radegast.Gtk;

/// <summary>Stops this account's furniture animations when its avatar leaves a seat.</summary>
internal sealed class SeatAnimationController : IDisposable
{
    private readonly GridClient _client;
    private readonly object _sync = new();
    private Dictionary<UUID, AnimationState> _animations = new();
    private Simulator? _seatSimulator;
    private uint _seat;
    private readonly HashSet<UUID> _seatSources = new();
    private bool _disposed;

    private readonly record struct AnimationState(int Sequence, UUID Source);

    public SeatAnimationController(GridClient client)
    {
        _client = client;
        // LibreMetaverse's SignaledAnimations omits the animation's source object.
        // Its AvatarAnimation handlers share an asynchronous dispatch flag. Use
        // the default packet observer to capture sources in receive order before
        // a queued seat change can overtake the corresponding animation snapshot.
        client.Network.RegisterCallback(PacketType.Default, OnAvatarAnimation, false);
        client.Objects.AvatarSitChanged += OnAvatarSitChanged;
        client.Network.SimChanged += OnSimChanged;
        client.Network.Disconnected += OnDisconnected;
    }

    private void OnAvatarAnimation(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not AvatarAnimationPacket packet || packet.Sender.ID != _client.Self.AgentID ||
            e.Simulator != _client.Network.CurrentSim) return;
        lock (_sync)
        {
            if (_disposed) return;
            var animations = new Dictionary<UUID, AnimationState>();
            for (var i = 0; i < packet.AnimationList.Length; i++)
            {
                var animation = packet.AnimationList[i];
                var source = i < packet.AnimationSourceList.Length ? packet.AnimationSourceList[i].ObjectID : UUID.Zero;
                // Some updates omit sources. Retain a known source only while the
                // same animation instance is playing; a restart may have a new owner.
                if (source == UUID.Zero && _animations.TryGetValue(animation.AnimID, out var previous) &&
                    previous.Sequence == animation.AnimSequenceID)
                    source = previous.Source;
                animations[animation.AnimID] = new(animation.AnimSequenceID, source);
            }
            _animations = animations;
            if (_seatSimulator == null && _client.Self.SittingOn != 0)
                RememberSeat(e.Simulator, _client.Self.SittingOn);
            else if (_seatSimulator == e.Simulator && _seat == _client.Self.SittingOn)
                _seatSources.UnionWith(GetSeatSources(e.Simulator, _seat));
        }
    }

    private void OnAvatarSitChanged(object? sender, AvatarSitChangedEventArgs e)
    {
        if (e.Avatar.ID != _client.Self.AgentID || e.Simulator != _client.Network.CurrentSim) return;
        lock (_sync)
        {
            if (_disposed) return;
            if (e.OldSeat != 0 && e.OldSeat != e.SittingOn)
            {
                var oldSources = GetSeatSources(e.Simulator, e.OldSeat);
                if (_seatSimulator == e.Simulator && _seat == e.OldSeat)
                    oldSources.UnionWith(_seatSources);
                var newSources = GetSeatSources(e.Simulator, e.SittingOn);
                var stop = _animations.Where(animation => oldSources.Contains(animation.Value.Source) &&
                    !newSources.Contains(animation.Value.Source)).ToDictionary(animation => animation.Key, _ => false);
                if (stop.Count > 0)
                {
                    // Send stops to the simulator: local animation bookkeeping alone
                    // would leave the old pose visible to everyone else.
                    _client.Self.Animate(stop, true);
                    foreach (var id in stop.Keys) _animations.Remove(id);
                }
            }
            RememberSeat(e.Simulator, e.SittingOn);
        }
    }

    private void RememberSeat(Simulator simulator, uint seat)
    {
        _seatSimulator = simulator;
        _seat = seat;
        _seatSources.Clear();
        _seatSources.UnionWith(GetSeatSources(simulator, seat));
    }

    private static HashSet<UUID> GetSeatSources(Simulator simulator, uint seat)
    {
        var sources = new HashSet<UUID>();
        if (seat == 0 || !simulator.ObjectsPrimitives.TryGetValue(seat, out var root)) return sources;
        var visited = new HashSet<uint> { root.LocalID };
        while (root.ParentID != 0 && simulator.ObjectsPrimitives.TryGetValue(root.ParentID, out var parent) &&
            visited.Add(parent.LocalID))
            root = parent;

        // Sitting on a linked child must also clean animations started by the
        // root or another scripted prim in the same furniture linkset.
        var children = simulator.ObjectsPrimitives.Values.ToArray().ToLookup(primitive => primitive.ParentID);
        var pending = new Queue<Primitive>();
        pending.Enqueue(root);
        visited.Clear();
        while (pending.TryDequeue(out var primitive))
        {
            if (!visited.Add(primitive.LocalID)) continue;
            if (primitive.ID != UUID.Zero) sources.Add(primitive.ID);
            foreach (var child in children[primitive.LocalID]) pending.Enqueue(child);
        }
        return sources;
    }

    private void OnSimChanged(object? sender, SimChangedEventArgs e) => Reset();
    private void OnDisconnected(object? sender, DisconnectedEventArgs e) => Reset();

    private void Reset()
    {
        lock (_sync)
        {
            _animations.Clear();
            _seatSources.Clear();
            _seatSimulator = null;
            _seat = 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _client.Network.UnregisterCallback(PacketType.Default, OnAvatarAnimation);
            _client.Objects.AvatarSitChanged -= OnAvatarSitChanged;
            _client.Network.SimChanged -= OnSimChanged;
            _client.Network.Disconnected -= OnDisconnected;
            Reset();
        }
    }
}
