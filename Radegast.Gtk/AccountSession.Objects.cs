using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Radegast.Gtk;

internal sealed record NearbyObject(UUID Id, uint LocalId, Simulator Simulator,
    string Name, double Distance, bool HasName);

internal sealed partial class AccountSession
{
    public const double ObjectRadius = 50;
    private int _sitInProgress;
    public bool IsSitting => Client.Self.SittingOn != 0 || Client.Self.Movement.SitOnGround;

    public IReadOnlyList<NearbyObject> GetNearbyObjects()
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim is not { } current) return Array.Empty<NearbyObject>();
        var position = Client.Self.GlobalPosition;
        var objects = new List<NearbyObject>();
        Simulator[] simulators;
        try { simulators = Client.Network.Simulators.ToArray(); }
        catch (ArgumentException)
        {
            // Connections can change while copying the library's mutable list.
            // Keep the current region this frame and retry neighbours next time.
            simulators = Array.Empty<Simulator>();
        }
        // Neighbouring regions can contribute objects when standing near a border.
        foreach (var sim in simulators.Append(current).Distinct())
        {
            if (sim == null || (sim != current && !sim.Connected)) continue;
            foreach (var prim in sim.ObjectsPrimitives.Values.ToArray())
            {
                if (!IsRezzedRoot(prim)) continue;
                var distance = ObjectDistance(sim, prim, position);
                if (!double.IsFinite(distance) || distance > ObjectRadius) continue;
                var name = prim.Properties?.Name;
                var hasName = !string.IsNullOrWhiteSpace(name);
                objects.Add(new(prim.ID, prim.LocalID, sim,
                    hasName ? name! : $"Object {prim.ID.ToString()[..8]}", distance, hasName));
            }
        }
        return objects.OrderBy(item => item.Distance)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsRezzedRoot(Primitive prim) => prim.ID != UUID.Zero && prim.LocalID != 0 &&
        prim.ParentID == 0 && !prim.IsAttachment && prim is not Avatar && prim.PrimData.PCode != PCode.Avatar;

    private static double ObjectDistance(Simulator sim, Primitive prim, Vector3d position)
    {
        Utils.LongToUInts(sim.Handle, out var x, out var y);
        var dx = (double)x + prim.Position.X - position.X;
        var dy = (double)y + prim.Position.Y - position.Y;
        var dz = prim.Position.Z - position.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public string? ObjectSitError(NearbyObject item)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim == null) return "This account is disconnected.";
        if ((item.Simulator != Client.Network.CurrentSim && !item.Simulator.Connected) ||
            !item.Simulator.ObjectsPrimitives.TryGetValue(item.LocalId, out var prim) ||
            prim.ID != item.Id || !IsRezzedRoot(prim)) return "This object is no longer available.";
        var distance = ObjectDistance(item.Simulator, prim, Client.Self.GlobalPosition);
        if (!double.IsFinite(distance) || distance > ObjectRadius) return "This object is outside the 50 m radius.";
        if (item.Simulator == Client.Network.CurrentSim && IsSeatInObject(item.Simulator, Client.Self.SittingOn, item.LocalId))
            return "You are already sitting on this object.";
        if (Rlv.Enabled)
        {
            var permissions = Rlv.Service.Permissions;
            if (!permissions.CanSit()) return "Sitting is restricted by RLV.";
            if (IsSitting && !permissions.CanUnsit()) return "Leaving the current seat is restricted by RLV.";
            if (permissions.CanSitTp(out var maxDistance) && distance > maxDistance)
                return "Sitting at this distance is restricted by RLV.";
        }
        return null;
    }

    private static bool IsSeatInObject(Simulator sim, uint seat, uint root)
    {
        var visited = new HashSet<uint>();
        while (seat != 0 && visited.Add(seat))
        {
            if (seat == root) return true;
            if (!sim.ObjectsPrimitives.TryGetValue(seat, out var prim)) break;
            seat = prim.ParentID;
        }
        return false;
    }

    public async Task SitOnObjectAsync(NearbyObject item, CancellationToken token = default)
    {
        if (ObjectSitError(item) is { } error) throw new InvalidOperationException(error);
        if (Interlocked.CompareExchange(ref _sitInProgress, 1, 0) != 0)
            throw new InvalidOperationException("Another sit request is still waiting for the server.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void SitResponse(object? sender, AvatarSitResponseEventArgs e)
        {
            if (e.ObjectID == item.Id || item.Simulator.ObjectsPrimitives.Values.Any(prim =>
                    prim.ID == e.ObjectID && IsSeatInObject(item.Simulator, prim.LocalID, item.LocalId)))
                response.TrySetResult();
        }
        void SeatChanged(object? sender, AvatarSitChangedEventArgs e)
        {
            if (e.Avatar.ID == Client.Self.AgentID && e.Simulator == item.Simulator &&
                IsSeatInObject(e.Simulator, e.SittingOn, item.LocalId)) seated.TrySetResult();
        }
        void Disconnected(object? sender, DisconnectedEventArgs e) => cancel.Cancel();
        Client.Self.AvatarSitResponse += SitResponse;
        Client.Objects.AvatarSitChanged += SeatChanged;
        Client.Network.Disconnected += Disconnected;
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (ObjectSitError(item) is { } changed) throw new InvalidOperationException(changed);
            if (item.Simulator == Client.Network.CurrentSim) Client.Self.RequestSit(item.Id, Vector3.Zero);
            else Client.Network.SendPacket(new AgentRequestSitPacket
            {
                AgentData = { AgentID = Client.Self.AgentID, SessionID = Client.Self.SessionID },
                TargetObject = { TargetID = item.Id, Offset = Vector3.Zero }
            }, item.Simulator);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token).ConfigureAwait(false);
            cancel.Token.ThrowIfCancellationRequested();
            // Recheck locks and the live object, rather than relying on the row's snapshot.
            if (ObjectSitError(item) is { } latest) throw new InvalidOperationException(latest);
            if (item.Simulator == Client.Network.CurrentSim) Client.Self.Sit();
            else Client.Network.SendPacket(new AgentSitPacket
            {
                AgentData = { AgentID = Client.Self.AgentID, SessionID = Client.Self.SessionID }
            }, item.Simulator);
            await seated.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token).ConfigureAwait(false);
            if (Rlv.Enabled) await Rlv.Service.ReportSitAsync(item.Id.Guid, cancel.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The server did not confirm sitting on this object.");
        }
        finally
        {
            Client.Self.AvatarSitResponse -= SitResponse;
            Client.Objects.AvatarSitChanged -= SeatChanged;
            Client.Network.Disconnected -= Disconnected;
            Interlocked.Exchange(ref _sitInProgress, 0);
        }
    }

    public string? StandError => _disposed || !IsConnected ? "This account is disconnected." :
        !IsSitting ? "You are not sitting." :
        Rlv.Enabled && !Rlv.Service.Permissions.CanUnsit() ? "Standing is restricted by RLV." : null;

    public void StandUp()
    {
        if (StandError is { } error) throw new InvalidOperationException(error);
        if (!Client.Self.Stand()) throw new InvalidOperationException("Movement updates are disabled.");
        // SeatAnimationController cleans the furniture's animations on the server's seat update.
    }
}
