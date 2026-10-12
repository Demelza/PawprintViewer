using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;

namespace Radegast.Gtk;

/// <summary>Headless interest-list maintenance and recovery of missing object updates.</summary>
internal sealed class SceneObjectDiscovery : IDisposable
{
    internal const float Range = 64;
    private readonly GridClient _client;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Simulator Sim, uint Id), (int Attempts, DateTimeOffset Next, uint? Crc)> _missing = new();
    private readonly Dictionary<Simulator, (bool Ready, DateTimeOffset Next)> _interest = new();
    private readonly Dictionary<UUID, (Simulator Sim, DateTimeOffset Until)> _confirmed = new();
    private CancellationTokenSource _session = new();
    private ITimer? _timer;
    private bool _disposed;

    public SceneObjectDiscovery(GridClient client, TimeProvider? clock = null)
    {
        _client = client;
        _clock = clock ?? TimeProvider.System;
        client.Settings.Agent.MultipleSims = true;
        client.Settings.World.TrackObjects = true;
        client.Settings.World.AlwaysDecodeObjects = true;
        client.Settings.World.AlwaysRequestObjects = true;
        client.Settings.World.CachePrimitives = false;
        client.Settings.World.UseInterpolationTimer = false;
        client.Self.Movement.Camera.Far = Range;
        client.Network.SimChanged += OnSimChanged;
        client.Network.LoginProgress += OnLogin;
        client.Network.Disconnected += OnDisconnected;
        client.Network.RegisterCallback(PacketType.ObjectUpdateCached, OnCached);
        client.Network.RegisterCallback(PacketType.AgentMovementComplete, OnMovement);
        client.Network.RegisterCallback(PacketType.KillObject, OnKilled);
    }

    public IEnumerable<Simulator> Simulators()
    {
        Simulator[] simulators;
        try { simulators = _client.Network.Simulators.ToArray(); }
        catch (ArgumentException) { simulators = []; }
        return simulators.Append(_client.Network.CurrentSim).OfType<Simulator>().Distinct()
            .Where(sim => sim == _client.Network.CurrentSim || sim.Connected).ToArray();
    }

    public (Simulator Sim, Primitive Prim)? Find(UUID id)
    {
        if (id == UUID.Zero) return null;
        foreach (var sim in Simulators())
            if (sim.ObjectsPrimitives.Values.FirstOrDefault(p => p.ID == id) is { } prim) return (sim, prim);
        return null;
    }

    public bool TryPosition(out Vector3 position)
    {
        position = _client.Self.RelativePosition;
        var sim = _client.Network.CurrentSim;
        var parent = _client.Self.SittingOn;
        var visited = new HashSet<uint>();
        while (parent != 0)
        {
            if (sim == null || !visited.Add(parent)) return false;
            if (!sim.ObjectsPrimitives.TryGetValue(parent, out var prim) || prim.ID == UUID.Zero) { Missing(sim, parent); return false; }
            position = prim.Position + position * prim.Rotation;
            parent = prim.ParentID;
        }
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }

    public void Refresh(bool reset = false, Vector3? arrival = null)
    {
        lock (_gate)
        {
            if (_disposed || _client.Network.CurrentSim is not { } current) return;
            var now = _clock.GetUtcNow();
            if (reset) { _missing.Clear(); _interest.Clear(); }
            var havePosition = TryPosition(out var position);
            if (arrival is { } reported)
            {
                position = reported;
                havePosition = float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
            }
            if (havePosition)
            {
                var movement = _client.Self.Movement;
                movement.Camera.Position = position;
                movement.Camera.Far = Range;
                // Do not consume one-shot movement controls while a sit/stand
                // action is using them. The normal SDK updater sends those.
                if (current.AgentMovementComplete && movement.AgentControls == 0) movement.SendUpdate(false);
            }
            foreach (var sim in Simulators())
            {
                if (sim != current && havePosition) SendNeighborInterest(sim, current, position);
                var cap = sim.Caps?.CapabilityURI("InterestList");
                if (cap != null && (!_interest.TryGetValue(sim, out var state) || !state.Ready && state.Next <= now))
                {
                    _interest[sim] = (false, now.AddSeconds(30));
                    _ = ConfigureInterestAsync(sim, cap, reset, _session.Token);
                }
                foreach (var prim in sim.ObjectsPrimitives.Values)
                    if (prim.ParentID != 0 && prim.ParentID != _client.Self.LocalID &&
                        (!sim.ObjectsPrimitives.TryGetValue(prim.ParentID, out var parent) || parent.ID == UUID.Zero) && !sim.ObjectsAvatars.ContainsKey(prim.ParentID))
                        Missing(sim, prim.ParentID);
            }
            foreach (var group in _missing.ToArray().GroupBy(item => item.Key.Sim))
            {
                if (!Simulators().Contains(group.Key))
                {
                    foreach (var item in group) _missing.Remove(item.Key);
                    continue;
                }
                var ids = new List<uint>();
                foreach (var (key, value) in group)
                {
                    if (group.Key.ObjectsPrimitives.TryGetValue(key.Id, out var prim) && prim.ID != UUID.Zero &&
                        (value.Crc == null || prim.CRC == value.Crc) || group.Key.ObjectsAvatars.ContainsKey(key.Id))
                    { _missing.Remove(key); continue; }
                    if (value.Attempts >= 5)
                    {
                        if (now > value.Next.AddMinutes(1)) _missing.Remove(key);
                        continue;
                    }
                    if (value.Next > now) continue;
                    ids.Add(key.Id);
                    _missing[key] = (value.Attempts + 1, now.AddSeconds(2), value.Crc);
                    if (ids.Count == 100) break;
                }
                if (ids.Count > 0) _client.Objects.RequestObjects(group.Key, ids);
            }
            foreach (var id in _confirmed.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) _confirmed.Remove(id);
            var connected = Simulators().ToHashSet();
            foreach (var sim in _interest.Keys.Where(sim => !connected.Contains(sim)).ToArray()) _interest.Remove(sim);
        }
    }

    private void Missing(Simulator sim, uint id, uint? crc = null)
    {
        lock (_gate)
        {
            if (_disposed || id == 0 || _missing.Count >= 2048) return;
            if (!_missing.TryGetValue((sim, id), out var previous) || crc != null && previous.Crc != crc)
                _missing[(sim, id)] = (0, DateTimeOffset.MinValue, crc);
        }
    }

    private void SendNeighborInterest(Simulator sim, Simulator current, Vector3 position)
    {
        Utils.LongToUInts(current.Handle, out var cx, out var cy);
        Utils.LongToUInts(sim.Handle, out var sx, out var sy);
        var movement = _client.Self.Movement;
        _client.Network.SendPacket(new AgentUpdatePacket
        {
            AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID,
                CameraCenter = new((float)((double)cx - sx + position.X), (float)((double)cy - sy + position.Y), position.Z),
                CameraAtAxis = movement.Camera.LeftAxis, CameraLeftAxis = movement.Camera.AtAxis, CameraUpAxis = movement.Camera.UpAxis,
                Far = Range, HeadRotation = movement.HeadRotation, BodyRotation = movement.BodyRotation }
        }, sim);
    }

    private async Task ConfigureInterestAsync(Simulator sim, Uri cap, bool reset, CancellationToken token)
    {
        var success = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (reset)
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, cap);
                using var response = await _client.HttpCapsClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            // Same capability/mode as the SL viewer's panoramic object stream.
            // The camera's 64 m range keeps this viewer's interest local.
            var (reply, _) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml,
                new OSDMap { ["mode"] = "360" }, timeout.Token).ConfigureAwait(false);
            using (reply) { reply.EnsureSuccessStatusCode(); }
            success = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { Logger.Debug("Object interest refresh: " + ex.Message, _client); }
        finally
        {
            lock (_gate)
                if (!token.IsCancellationRequested && !_disposed) _interest[sim] = (success, _clock.GetUtcNow().AddSeconds(30));
        }
    }

    public async Task<Simulator?> LocateAsync(UUID id, CancellationToken token)
    {
        if (Find(id) is { } found) return found.Sim;
        CancellationToken session;
        lock (_gate)
        {
            if (_disposed || id == UUID.Zero) return null;
            if (_confirmed.TryGetValue(id, out var known) && known.Until > _clock.GetUtcNow() && Simulators().Contains(known.Sim)) return known.Sim;
            session = _session.Token;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, session);
        var reply = new TaskCompletionSource<Simulator>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulators = Simulators().ToArray();
        void Properties(object? _, ObjectPropertiesFamilyEventArgs e)
        {
            if (e.Properties.ObjectID == id && simulators.Contains(e.Simulator)) reply.TrySetResult(e.Simulator);
        }
        void Update(object? _, PrimEventArgs e) { if (e.Prim.ID == id && simulators.Contains(e.Simulator)) reply.TrySetResult(e.Simulator); }
        _client.Objects.ObjectPropertiesFamily += Properties;
        _client.Objects.ObjectUpdate += Update;
        try
        {
            Refresh();
            foreach (var region in simulators) _client.Objects.RequestObjectPropertiesFamily(region, id);
            var sim = await reply.Task.WaitAsync(TimeSpan.FromSeconds(3), cancel.Token).ConfigureAwait(false);
            lock (_gate) if (!cancel.IsCancellationRequested && _confirmed.Count < 128) _confirmed[id] = (sim, _clock.GetUtcNow().AddSeconds(30));
            return sim;
        }
        catch (TimeoutException) { return Find(id)?.Sim; }
        finally
        {
            _client.Objects.ObjectPropertiesFamily -= Properties;
            _client.Objects.ObjectUpdate -= Update;
        }
    }

    private void OnCached(object? _, PacketReceivedEventArgs e)
    {
        foreach (var block in ((ObjectUpdateCachedPacket)e.Packet).ObjectData)
            if (!e.Simulator.ObjectsPrimitives.TryGetValue(block.ID, out var prim) || prim.ID == UUID.Zero || prim.CRC != block.CRC)
                Missing(e.Simulator, block.ID, block.CRC);
    }
    private void OnMovement(object? _, PacketReceivedEventArgs e)
    {
        if (e.Simulator != _client.Network.CurrentSim) return;
        Refresh(arrival: ((AgentMovementCompletePacket)e.Packet).Data.Position);
    }
    private void OnKilled(object? _, PacketReceivedEventArgs e)
    {
        lock (_gate)
        {
            foreach (var block in ((KillObjectPacket)e.Packet).ObjectData) _missing.Remove((e.Simulator, block.ID));
            // Properties-only targets have no local ID yet. Invalidate this
            // simulator's short-lived UUID confirmations when objects die.
            foreach (var id in _confirmed.Where(pair => pair.Value.Sim == e.Simulator).Select(pair => pair.Key).ToArray()) _confirmed.Remove(id);
        }
    }
    private void OnLogin(object? _, LoginProgressEventArgs e) { if (e.Status == LoginStatus.Success) Start(); }
    private void OnSimChanged(object? _, SimChangedEventArgs e) { Stop(); Start(); }
    private void OnDisconnected(object? _, DisconnectedEventArgs e) => Stop();

    private void Start()
    {
        lock (_gate)
        {
            if (_disposed || !_client.Network.Connected || _timer != null) return;
            _timer = _clock.CreateTimer(_ =>
            {
                try { Refresh(); }
                catch (Exception ex) { Logger.Debug("Object discovery update: " + ex.Message, _client); }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        Refresh();
    }
    private void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            _session.Cancel(); _session.Dispose(); _session = new();
            _missing.Clear(); _interest.Clear(); _confirmed.Clear();
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop();
        _session.Dispose();
        _client.Network.SimChanged -= OnSimChanged;
        _client.Network.LoginProgress -= OnLogin;
        _client.Network.Disconnected -= OnDisconnected;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCached, OnCached);
        _client.Network.UnregisterCallback(PacketType.AgentMovementComplete, OnMovement);
        _client.Network.UnregisterCallback(PacketType.KillObject, OnKilled);
    }
}
