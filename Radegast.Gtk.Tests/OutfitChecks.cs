using System.Diagnostics;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class OutfitChecks
{
    public static async Task RepeatedSwaps()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        var cof = f.Outfit.COF!;
        cof.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.EnableAppearance();
        using var batch = f.Outfit.BeginBatch();
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        var packets = f.CapturePackets();
        var initial = server.LinkedItems(cof.UUID);
        for (var pass = 0; pass < 3; pass++)
        {
            await f.Command("@detachall:Cuffs=force,attachallover:Other=force");
            var expected = new HashSet<UUID> { second.Object.UUID, second.Shirt.UUID };
            Check(server.LinkedItems(cof.UUID).SetEquals(expected), "Switching outfits retained links to the old outfit");
            Check(f.Outfit.WornIds.SetEquals(expected), "The controller and server disagree about the selected outfit");
            await f.Command("@getinvworn:Cuffs=3001,getinvworn:Other=3002");
            Check(f.Replies.Last(r => r.Channel == 3001).Text == "|11" && f.Replies.Last(r => r.Channel == 3002).Text == "|33",
                $"Worn-state replies: Cuffs={f.Replies.Last(r => r.Channel == 3001).Text}, Other={f.Replies.Last(r => r.Channel == 3002).Text}");
            await f.Command("@detachall:Other=force,attachallover:Cuffs=force");
            Check(server.LinkedItems(cof.UUID).IsSupersetOf(initial) && !server.LinkedItems(cof.UUID).Contains(second.Object.UUID),
                "A round trip failed to re-add the original outfit or stacked both outfits");
        }
        var before = packets();
        Check(before.OfType<RezSingleAttachmentFromInvPacket>().Any(p => p.ObjectData.ItemID == second.Object.UUID),
            "The actual target of the outfit link was never attached");
        Check(before.OfType<DetachAttachmentIntoInvPacket>().Any(p => p.ObjectData.ItemID == second.Object.UUID),
            "The replacement outfit could not be detached");
        batch.Dispose();
        await WaitUntil(() => server.BakeVersions.Count == 1);
        await Task.Delay(200);
        Check(server.BakeVersions.Single() == cof.Version, "The final appearance request used a stale COF version");
        Check(!packets().Any(p => p is RezMultipleAttachmentsFromInvPacket or RezSingleAttachmentFromInvPacket or DetachAttachmentIntoInvPacket),
            "Finishing the bake replayed or detached an outfit");
    }

    public static async Task StartupAndCanonicalVersion()
    {
        using var f = new Fixture();
        var cof = f.Outfit.COF!;
        cof.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.EnableAppearance();
        server.SetVersion(cof.UUID, 9);
        using var batch = f.Outfit.BeginBatch();
        var packets = f.CapturePackets();
        var stopwatch = Stopwatch.StartNew();
        await f.Outfit.BakeAsync(f.Simulator, CancellationToken.None);
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Baking waited for a rendered avatar or retried the same stale version");
        Check(!f.Simulator.ObjectsAvatars.ContainsKey(f.Client.Self.LocalID), "The test accidentally supplied the avatar which masks the startup delay");
        Check(server.BakeVersions.SequenceEqual(new[] { 7, 9 }) && cof.Version == 9,
            "A server version mismatch did not reload the canonical Current Outfit before retrying");
        Check(server.FolderRequests.Count(id => id == cof.UUID) == 1, "Mismatch handling did not refresh COF or fetched it repeatedly");
        Check(!packets().OfType<RezMultipleAttachmentsFromInvPacket>().Any(p => p.HeaderData.FirstDetachAll),
            "Startup or mismatch recovery asked the simulator to detach every attachment");
        await f.Outfit.BakeAsync(f.Simulator, CancellationToken.None);
        Check(server.BakeVersions.Count == 2, "A confirmed version was baked again without a change");
    }

    public static async Task LateBakeCannotUndoOutfit()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        f.Outfit.COF!.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.EnableAppearance();
        server.BakeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.IgnoreBakeCancellation = true;
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        await WaitUntil(() => server.BakeVersions.Count == 1);
        var packets = f.CapturePackets();
        var change = f.Command("@detachall:Cuffs=force,attachallover:Other=force");
        // Model a service which already processed the request and sends its old
        // response after the new command arrives, despite client cancellation.
        await Task.Delay(100);
        server.BakeGate.TrySetResult();
        await change.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = new HashSet<UUID> { second.Object.UUID, second.Shirt.UUID };
        Check(server.LinkedItems(f.Outfit.COF.UUID).SetEquals(expected) && f.Outfit.WornIds.SetEquals(expected),
            "A late startup bake restored links from the previous outfit");
        var sent = packets();
        Check(!sent.OfType<RezSingleAttachmentFromInvPacket>().Any(p => p.ObjectData.ItemID == f.Object.UUID) &&
            !sent.OfType<RezMultipleAttachmentsFromInvPacket>().Any(), "A late appearance response reattached the old outfit");
        await WaitUntil(() => server.BakeVersions.Count == 2);
        await Task.Delay(100);
        Check(!packets().Any(p => p is RezSingleAttachmentFromInvPacket or RezMultipleAttachmentsFromInvPacket),
            "The replacement bake replayed attachments");
    }

    public static async Task AcknowledgementsAndFailures()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        f.Outfit.COF!.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        var packets = f.CapturePackets();
        server.CreateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var add = f.Outfit.AttachAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None);
        await WaitUntil(() => server.CreatedLinks == 1);
        Check(!packets().OfType<RezSingleAttachmentFromInvPacket>().Any(), "Attachment was rezzed before its COF link was acknowledged");
        server.CreateGate.TrySetResult();
        await add;
        Check(f.Outfit.COF.Version == 8 && packets().OfType<RezSingleAttachmentFromInvPacket>().Single().ObjectData.ItemID == second.Object.UUID,
            "Link creation discarded the canonical version or failed to send the actual attachment");
        var link = f.Client.Inventory.Store!.GetContents(f.Outfit.COF.UUID).OfType<InventoryItem>()
            .Single(l => l.ResolvedItemID == second.Object.UUID);
        server.FailedDeletes.Add(link.UUID);
        await ExpectFailure(() => f.Outfit.DetachAsync(second.Object, CancellationToken.None));
        Check(f.Outfit.WornIds.Contains(second.Object.UUID) && server.LinkedItems(f.Outfit.COF.UUID).Contains(second.Object.UUID) &&
            !packets().OfType<DetachAttachmentIntoInvPacket>().Any(), "A rejected removal was treated as a successful detach");
        server.FailedDeletes.Clear();
        await f.Outfit.DetachAsync(second.Object, CancellationToken.None);
        Check(!f.Outfit.WornIds.Contains(second.Object.UUID) && packets().OfType<DetachAttachmentIntoInvPacket>().Single().ObjectData.ItemID == second.Object.UUID,
            "A rejected removal could not be retried");
        server.FailCreates = true;
        await ExpectFailure(() => f.Outfit.AttachAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None));
        Check(!packets().OfType<RezSingleAttachmentFromInvPacket>().Any() && !server.LinkedItems(f.Outfit.COF.UUID).Contains(second.Object.UUID),
            "A rejected link creation equipped an untracked attachment");
        server.FailCreates = false;
        await f.Outfit.AttachAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None);
        Check(server.LinkedItems(f.Outfit.COF.UUID).Contains(second.Object.UUID), "A failed outfit add remained permanently unavailable");
    }

    public static async Task ReplacementAndLayers()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        f.Outfit.COF!.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        await f.Rlv.AddAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None);
        Check(f.Outfit.WornIds.Contains(f.Object.UUID) && f.Outfit.WornIds.Contains(second.Object.UUID), "Add replaced an existing attachment at the same point");
        await f.Rlv.AddAsync(second.Shirt, null, false, CancellationToken.None);
        Check(f.Outfit.WornIds.Contains(f.Shirt.UUID) && f.Outfit.WornIds.Contains(second.Shirt.UUID), "Add removed another clothing layer");
        await f.Rlv.AddAsync(second.Shirt, null, true, CancellationToken.None);
        Check(!f.Outfit.WornIds.Contains(f.Shirt.UUID) && f.Outfit.WornIds.Contains(second.Shirt.UUID), "Replace kept the old wearable's COF link");
        var third = new InventoryObject(UUID.Random()) { Name = "Third", ParentUUID = f.Object.ParentUUID, OwnerID = f.Owner,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        f.Client.Inventory.Store!.UpdateNodeFor(third);
        await f.Command("@detach=n", f.Attachment.ID.Guid);
        await ExpectFailure(() => f.Rlv.AddAsync(third, AttachmentPoint.Chest, true, CancellationToken.None));
        await f.Command("@clear", f.Attachment.ID.Guid);
        await f.Rlv.AddAsync(third, AttachmentPoint.Chest, true, CancellationToken.None);
        Check(f.Outfit.WornIds.Contains(third.UUID) && !f.Outfit.WornIds.Contains(f.Object.UUID) && !f.Outfit.WornIds.Contains(second.Object.UUID),
            "Attachment replacement left old COF links at that point");
    }

    public static async Task PartialReplacement()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        f.Outfit.COF!.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        await f.Outfit.AttachAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None);
        var link = f.Client.Inventory.Store!.GetContents(f.Outfit.COF.UUID).OfType<InventoryItem>()
            .Single(l => l.ResolvedItemID == second.Object.UUID);
        var third = new InventoryObject(UUID.Random()) { Name = "Third", ParentUUID = f.Object.ParentUUID, OwnerID = f.Owner,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        f.Client.Inventory.Store.UpdateNodeFor(third);
        var packets = f.CapturePackets();
        server.FailedDeletes.Add(link.UUID);
        await ExpectFailure(() => f.Outfit.AttachAsync(third, AttachmentPoint.Chest, true, CancellationToken.None));
        Check(!server.LinkedItems(f.Outfit.COF.UUID).Contains(f.Object.UUID) && server.LinkedItems(f.Outfit.COF.UUID).Contains(second.Object.UUID),
            "The fixture did not commit the first removal and reject the second");
        var first = packets();
        Check(first.OfType<DetachAttachmentIntoInvPacket>().Single().ObjectData.ItemID == f.Object.UUID &&
            !first.OfType<RezSingleAttachmentFromInvPacket>().Any(), "A partial removal left the confirmed removed object worn or attached the replacement early");
        server.FailedDeletes.Clear();
        await f.Outfit.AttachAsync(third, AttachmentPoint.Chest, true, CancellationToken.None);
        Check(f.Outfit.WornIds.Contains(third.UUID) && !f.Outfit.WornIds.Contains(f.Object.UUID) && !f.Outfit.WornIds.Contains(second.Object.UUID),
            "A partial replacement could not be retried without stacking the old objects");
    }

    public static async Task ClothingOrderingGaps()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        var cof = f.Outfit.COF!;
        cof.Version = 7;
        var oldLink = f.Client.Inventory.Store!.GetContents(cof.UUID).OfType<InventoryItem>().Single(l => l.ResolvedItemID == f.Shirt.UUID);
        var prefix = ((int)WearableType.Shirt).ToString();
        oldLink.Description = prefix + "02";
        using var server = new InventoryServer(f.Client, f.Simulator);
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        await f.Outfit.AddToOutfitAsync(second.Shirt, false, CancellationToken.None);
        var added = f.Client.Inventory.Store.GetContents(cof.UUID).OfType<InventoryItem>().Single(l => l.ResolvedItemID == second.Shirt.UUID);
        Check(added.Description == prefix + "03", "A clothing gap caused a duplicate ordering number or placed the new layer underneath the old one");
        await f.Outfit.RemoveFromOutfitAsync(second.Shirt, CancellationToken.None);
        // Use a separate fresh service snapshot to exercise compaction without
        // relying on the controller's own cached version or link descriptions.
        oldLink.Description = prefix + "59";
        using var updatedServer = new InventoryServer(f.Client, f.Simulator);
        await f.Outfit.AddToOutfitAsync(second.Shirt, false, CancellationToken.None);
        var links = f.Client.Inventory.Store.GetContents(cof.UUID).OfType<InventoryItem>().Where(l =>
            l.ResolvedItemID == f.Shirt.UUID || l.ResolvedItemID == second.Shirt.UUID).ToArray();
        Check(links.Select(l => l.Description).Order().SequenceEqual(new[] { prefix + "00", prefix + "01" }) && cof.Version == 11,
            "Clothing compaction did not preserve unique ordering or refresh the version after link updates");
    }

    public static async Task DisconnectCancelsPendingChanges()
    {
        using var f = new Fixture();
        var second = AddOutfit(f, "Other");
        f.Outfit.COF!.Version = 7;
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.EnableAppearance();
        using var batch = f.Outfit.BeginBatch();
        await f.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
        var packets = f.CapturePackets();
        server.CreateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var add = f.Outfit.AttachAsync(second.Object, AttachmentPoint.Chest, false, CancellationToken.None);
        await WaitUntil(() => server.CreatedLinks == 1);
        f.Outfit.Reset();
        try { await add; throw new Exception("Reset did not cancel the pending outfit mutation"); }
        catch (OperationCanceledException) { }
        server.CreateGate.TrySetResult();
        batch.Dispose();
        await Task.Delay(100);
        Check(!f.Outfit.IsReady && server.BakeVersions.Count == 0 && !packets().OfType<RezSingleAttachmentFromInvPacket>().Any(),
            "Disconnect retained a bake timer or sent an attachment from the old session");
    }

    public static async Task CapabilitiesAndCrossings()
    {
        using var f = new Fixture();
        f.Outfit.COF!.Version = 7;
        f.Simulator.Protocols = RegionProtocols.AgentAppearanceService;
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.EnableAppearance();
        var packets = f.CapturePackets();
        typeof(NetworkManager).GetMethod("OnSimChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Client.Network, [new SimChangedEventArgs(null!)]);
        typeof(Caps).GetMethod("OnCapabilitiesReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Simulator.Caps, [f.Simulator]);
        await WaitUntil(() => server.BakeVersions.Count == 1);
        Check(!f.Client.Settings.Agent.SendAppearance && typeof(AppearanceManager).GetField("AppearanceTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Client.Appearance) == null,
            "Capabilities started the SDK's concurrent bake/outfit replay pipeline");
        Check(!packets().OfType<RezMultipleAttachmentsFromInvPacket>().Any(), "Startup used a destructive full outfit replay");
        await Task.Delay(100);
        // Exercise a new region's capabilities against an already initialized
        // inventory. The only physical primitive is deliberately still present.
        typeof(NetworkManager).GetMethod("OnSimChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Client.Network, [new SimChangedEventArgs(f.Simulator)]);
        typeof(Caps).GetMethod("OnCapabilitiesReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Simulator.Caps, [f.Simulator]);
        await WaitUntil(() => server.BakeVersions.Count == 2);
        var sent = packets();
        Check(!sent.Any(p => p is RezSingleAttachmentFromInvPacket or RezMultipleAttachmentsFromInvPacket or DetachAttachmentIntoInvPacket),
            "A region capability refresh reattached or detached the current outfit");
        f.Outfit.Reset();
        f.Client.Network.CurrentSim = null!;
        typeof(Caps).GetMethod("OnCapabilitiesReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Simulator.Caps, [f.Simulator]);
        await Task.Delay(100);
        Check(!f.Outfit.IsReady && server.BakeVersions.Count == 2, "Late capabilities restarted a disconnected account's appearance work");
    }

    private static (InventoryObject Object, InventoryWearable Shirt) AddOutfit(Fixture f, string name)
    {
        var store = f.Client.Inventory.Store!;
        var folder = new InventoryFolder(UUID.Random()) { Name = name, ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        store.UpdateNodeFor(folder);
        store.GetNodeFor(folder.UUID).NeedsUpdate = false;
        var obj = new InventoryObject(UUID.Random()) { Name = name + " object (chest)", ParentUUID = f.Object.ParentUUID,
            OwnerID = f.Owner, AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        var shirt = new InventoryWearable(UUID.Random()) { Name = name + " shirt", ParentUUID = f.Object.ParentUUID,
            OwnerID = f.Owner, AssetUUID = UUID.Random(), AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable,
            WearableType = WearableType.Shirt };
        store.UpdateNodeFor(obj); store.UpdateNodeFor(shirt);
        foreach (var item in new InventoryItem[] { obj, shirt })
            store.UpdateNodeFor(new InventoryItem(UUID.Random()) { Name = item.Name, ParentUUID = folder.UUID, OwnerID = f.Owner,
                AssetType = AssetType.Link, AssetUUID = item.UUID, InventoryType = item.InventoryType });
        return (obj, shirt);
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); throw new Exception("An operation expected to fail succeeded"); }
        catch (InvalidOperationException) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
