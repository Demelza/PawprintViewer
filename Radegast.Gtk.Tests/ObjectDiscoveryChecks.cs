using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class ObjectDiscoveryChecks
{
    public static Task CameraAndSeatedPosition()
    {
        using var h = new ReconnectHarness(); h.Connect();
        var f = h.Fixture;
        using var scene = new SceneObjectDiscovery(f.Client);
        f.Simulator.AgentMovementComplete = true;
        f.Client.Self.RelativePosition = new(235, 140, 2300);
        var packets = f.CapturePackets();
        scene.Refresh();
        var update = packets().OfType<AgentUpdatePacket>().Last();
        Check(update.AgentData.CameraCenter == f.Client.Self.RelativePosition && update.AgentData.Far == 64,
            "The object's interest camera stayed at ground level or outside the avatar's local area");
        Check(f.Client.Settings.Agent.MultipleSims && f.Client.Settings.World.AlwaysRequestObjects && !f.Client.Settings.World.CachePrimitives &&
            !f.Client.Settings.TexturePipeline.Enabled, "Discovery disabled neighbor/cache recovery or re-enabled scene textures");
        var root = f.Prim(300); root.Position = new(200, 150, 2400); root.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var seat = f.Prim(301, root.LocalID); seat.Position = new(2, 3, 0);
        f.ChangeSeat(seat.LocalID);
        f.Client.Self.RelativePosition = new(1, 0, 1);
        Check(scene.TryPosition(out var position) && Vector3.Distance(position, root.Position + (seat.Position + f.Client.Self.RelativePosition) * root.Rotation) < .01f,
            "Seated camera coordinates were treated as global positions or ignored parent rotation");
        f.Simulator.ObjectsPrimitives.TryRemove(root.LocalID, out _);
        Check(!scene.TryPosition(out _), "A missing furniture parent silently moved the interest camera to a bogus position");
        scene.Refresh();
        Check(packets().OfType<RequestMultipleObjectsPacket>().Any(p => p.ObjectData.Any(b => b.ID == root.LocalID)), "A missing furniture root was never requested");
        return Task.CompletedTask;
    }

    public static async Task CachedUpdatesAndMissingParents()
    {
        using var h = new ReconnectHarness(); h.Connect();
        var f = h.Fixture;
        var clock = new ReconnectClock();
        using var scene = new SceneObjectDiscovery(f.Client, clock);
        var packets = f.CapturePackets();
        f.Simulator.ObjectsPrimitives[300] = new Primitive { LocalID = 300, ID = UUID.Zero };
        var child = f.Prim(302, 301);
        f.Receive(new ObjectUpdateCachedPacket { ObjectData = [new() { ID = 300, CRC = 123 }] });
        await WaitUntil(() => Field<System.Collections.IDictionary>(scene, "_missing").Contains((f.Simulator, 300u)));
        var sent = new List<Packet>(); sent.AddRange(packets());
        scene.Refresh(); sent.AddRange(packets());
        Check(sent.OfType<RequestMultipleObjectsPacket>().Any(p => p.ObjectData.Any(b => b.ID == 300)) &&
            sent.OfType<RequestMultipleObjectsPacket>().Any(p => p.ObjectData.Any(b => b.ID == 301)),
            "Cached placeholders or missing linkset parents were mistaken for fully detected objects");
        clock.Advance(1); scene.Refresh();
        Check(!packets().OfType<RequestMultipleObjectsPacket>().Any(), "Missing-object requests spun without their retry delay");
        clock.Advance(1); scene.Refresh();
        Check(packets().OfType<RequestMultipleObjectsPacket>().Any(), "A lost full object update was not retried");
        var first = f.Prim(300); first.CRC = 123;
        var parent = f.Prim(301); parent.Position = new(10, 0, 0);
        scene.Refresh();
        Check(!packets().OfType<RequestMultipleObjectsPacket>().Any(), "Detected objects kept being requested after recovery");
        Check(h.Account.GetNearbyObjects().Any(o => o.Id == first.ID) && h.Account.GetNearbyObjects().Any(o => o.Id == parent.ID) &&
            h.Account.GetNearbyObjects().All(o => o.Id != child.ID), "Recovered linksets were not listed as separate root objects");
        f.Receive(new ObjectUpdateCachedPacket { ObjectData = [new() { ID = 350, CRC = 555 }] });
        await WaitUntil(() => Field<System.Collections.IDictionary>(scene, "_missing").Contains((f.Simulator, 350u)));
        f.Receive(new KillObjectPacket { ObjectData = [new() { ID = 350 }] });
        await WaitUntil(() => !Field<System.Collections.IDictionary>(scene, "_missing").Contains((f.Simulator, 350u)));
        packets(); clock.Advance(100); scene.Refresh();
        Check(!packets().OfType<RequestMultipleObjectsPacket>().Any(p => p.ObjectData.Any(b => b.ID == 350)), "Deleted objects kept being recovered as missing live objects");
    }

    public static async Task InterestAndLifecycle()
    {
        using var f = new Fixture();
        var clock = new ReconnectClock();
        using var server = new InventoryServer(f.Client, f.Simulator); server.EnableInterest();
        using var scene = new SceneObjectDiscovery(f.Client, clock);
        f.Simulator.AgentMovementComplete = true;
        var packets = f.CapturePackets();
        typeof(NetworkManager).GetProperty(nameof(NetworkManager.Connected))!.SetValue(f.Client.Network, true);
        Invoke(scene, "Start");
        await WaitUntil(() => server.InterestRequests.Count == 1);
        Check(clock.PendingTimers == 1 && server.InterestRequests.Single() == "360", "Startup did not request all-direction visibility or installed duplicate timers");
        clock.Advance(10);
        Check(server.InterestRequests.Count == 1 && clock.PendingTimers == 1, "Idle discovery repeatedly changed the interest mode");
        scene.Refresh(true);
        await WaitUntil(() => server.InterestRequests.Count == 3);
        Check(server.InterestRequests.SequenceEqual(new[] { "360", "reset", "360" }), "Refresh did not ask the simulator to resend missing interest-list objects");
        Invoke(scene, "Stop");
        packets(); clock.Advance(60);
        Check(clock.PendingTimers == 0 && packets().Count == 0, "Disconnected scene tracking kept requesting objects or camera updates");
        typeof(NetworkManager).GetProperty(nameof(NetworkManager.Connected))!.SetValue(f.Client.Network, false);
    }

    public static async Task RlvSitOnUnstreamedFurniture()
    {
        using var h = new ReconnectHarness(); h.Connect();
        var f = h.Fixture;
        var target = UUID.Random();
        var packets = f.CapturePackets();
        var sent = new List<Packet>();
        var command = f.Command($"@sit:{target}=force");
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<RequestObjectPropertiesFamilyPacket>().Any(p => p.ObjectData.ObjectID == target); });
        Properties(f, target);
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<AgentRequestSitPacket>().Any(); });
        Check(sent.OfType<AgentRequestSitPacket>().Single().TargetObject.TargetID == target && !command.IsCompleted,
            "An unloaded UUID was rejected or treated as seated without confirmation");
        var childSeat = f.Prim(400, 401); // Root hasn't arrived yet.
        f.Receive(new AvatarSitResponsePacket { SitObject = { ID = childSeat.ID } });
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<AgentSitPacket>().Any(); });
        Check(!command.IsCompleted, "A linked-seat response reported success before the server seated the avatar");
        f.ChangeSeat(childSeat.LocalID);
        await command.WaitAsync(TimeSpan.FromSeconds(3));
        Check(h.Account.IsSitting, "The mover's UUID request did not complete on its server-confirmed linked seat");
    }

    public static async Task RlvDistanceAndCancellation()
    {
        using var h = new ReconnectHarness(); h.Connect();
        var f = h.Fixture;
        var target = UUID.Random();
        var packets = f.CapturePackets(); var sent = new List<Packet>();
        await f.Command("@sittp=n");
        var command = f.Command($"@sit:{target}=force");
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<RequestObjectPropertiesFamilyPacket>().Any(); });
        Properties(f, target);
        await command.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!packets().OfType<AgentRequestSitPacket>().Any(), "An unknown position bypassed an RLV sit distance restriction");
        await f.Command("@clear");
        command = f.Command($"@sit:{target}=force");
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<AgentRequestSitPacket>().Any(); });
        h.Disconnect();
        try { await command.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Disconnect did not cancel the pending mover sit"); }
        catch (OperationCanceledException) { }
        f.Receive(new AvatarSitResponsePacket { SitObject = { ID = UUID.Random() } });
        await Task.Delay(50);
        Check(!packets().OfType<AgentSitPacket>().Any(), "A stale response seated a disconnected account");
    }

    private static void Properties(Fixture f, UUID id) => f.Receive(new ObjectPropertiesFamilyPacket
        { ObjectData = { ObjectID = id, OwnerID = f.Owner, Name = Utils.StringToBytes("Furniture"), Description = Array.Empty<byte>() } });
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
