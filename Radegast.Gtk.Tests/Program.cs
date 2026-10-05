using System.Net;
using System.Reflection;
using System.Threading.Channels;
using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.Packets;
using LibreMetaverse.RLV;
using Radegast.Gtk;

// Integration checks for the GTK account adapter; no grid login or display is required.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Version replies use the script's channel", async () =>
    {
        using var f = new Fixture();
        await f.Command("@versionnum=123,versionnew=456");
        Check(f.Replies.Any(r => r == (123, RlvService.RLVVersionNum)), "Version number reply missing");
        Check(f.Replies.Any(r => r.Channel == 456 && r.Text.Contains("RestrainedLove")), "Version text reply missing");
        await f.Command("@versionnum=0");
        Check(f.Replies.All(r => r.Channel != 0), "An automatic reply escaped to public chat");
    }),
    ("Commands and restrictions stay within their account", async () =>
    {
        using var a = new Fixture();
        using var b = new Fixture();
        await a.Command("@sendchat=n,showloc=n,shownames=n,shownearby=n,showinv=n");
        Check(!a.Rlv.Service.Permissions.CanSendChat() && !a.Rlv.Service.Permissions.CanShowLoc(), "Restrictions missing");
        Check(!a.Rlv.Service.Permissions.CanShowNames(Guid.NewGuid()) && !a.Rlv.Service.Permissions.CanShowNearby(), "Privacy restrictions missing");
        Check(!a.Rlv.Service.Permissions.CanShowInv(), "Inventory restriction missing");
        Check(b.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Restrictions leaked to another account");
        await a.Command("@clear");
        Check(a.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Clear did not remove restrictions");
    }),
    ("Only owned object owner-say commands are intercepted", async () =>
    {
        using var f = new Fixture();
        ChatEventArgs Chat(ChatSourceType source, ChatType type, UUID owner) => new(f.Simulator,
            "@sendchat=n", ChatAudibleLevel.Fully, type, source, "Object", f.Attachment.ID, owner, Vector3.Zero);
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Agent, ChatType.OwnerSay, f.Owner)), "Avatar command accepted");
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.Normal, f.Owner)), "Public object chat accepted");
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.OwnerSay, UUID.Random())), "Another owner's command accepted");
        Check(f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.OwnerSay, f.Owner)), "Owner-say command ignored");
        await f.Command("@versionnum=789");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "Queued owner-say restriction missing");
    }),
    ("Actual attachments and inventory links share the detach lock", async () =>
    {
        using var f = new Fixture();
        await f.Command("@detach=n", f.Attachment.ID.Guid);
        Check(!f.Rlv.CanDetach(f.Object) && !f.Rlv.CanDetach(f.ObjectLink), "Locked attachment could be detached");
        Check(!f.Rlv.CanModifyInventory(f.ObjectLink), "Locked attachment link could be modified");
        await f.Command("@clear", f.Attachment.ID.Guid);
        Check(f.Rlv.CanDetach(f.Object) && f.Rlv.CanDetach(f.ObjectLink), "Attachment remained locked after clear");
    }),
    ("Attachment point locks cover explicit Add To actions", async () =>
    {
        using var f = new Fixture();
        await f.Command("@addattach:chest=n,remattach:chest=n");
        Check(!f.Rlv.CanAdd(f.Object, AttachmentPoint.Chest), "Locked Add To point allowed");
        Check(f.Rlv.CanAdd(f.Object, AttachmentPoint.LeftHand), "Unrestricted Add To point blocked");
        Check(!f.Rlv.CanDetach(f.Object), "Attachment point removal lock ignored");
        await f.Command("@addoutfit:shirt=n,remoutfit:shirt=n");
        Check(!f.Rlv.CanAdd(f.Shirt, null) && !f.Rlv.CanDetach(f.Shirt), "Wearable locks ignored");
    }),
    ("Replacement cannot remove a locked attachment", async () =>
    {
        using var f = new Fixture();
        var replacement = new InventoryObject(UUID.Random()) { Name = "Replacement", ParentUUID = f.Object.ParentUUID,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        f.Client.Inventory.Store!.UpdateNodeFor(replacement);
        await f.Command("@detach=n", f.Attachment.ID.Guid);
        try
        {
            await f.Rlv.AddAsync(replacement, AttachmentPoint.Chest, true, CancellationToken.None);
            throw new Exception("Replacement was allowed to remove a locked object");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("locked by RLV")) { }
    }),
    ("A command from a linked child applies to the whole attachment", async () =>
    {
        using var f = new Fixture();
        var child = new Primitive { ID = UUID.Random(), LocalID = 101, ParentID = f.Attachment.LocalID };
        f.Simulator.ObjectsPrimitives[child.LocalID] = child;
        var chat = new ChatEventArgs(f.Simulator, "@detach=n", ChatAudibleLevel.Fully, ChatType.OwnerSay,
            ChatSourceType.Object, "Child", child.ID, f.Owner, Vector3.Zero);
        Check(f.Rlv.TryHandleChat(chat), "Child command was not accepted");
        await f.Command("@versionnum=123");
        Check(!f.Rlv.CanDetach(f.Object), "Child restriction did not lock its parent attachment");
    }),
    ("Shared inventory queries resolve links and physical attachment points", async () =>
    {
        using var f = new Fixture();
        var map = f.Rlv.BuildInventoryMap();
        Check(map.TryGetFolderFromPath("Cuffs", false, out var folder), "Shared folder missing");
        var attached = folder!.Items.Single(i => i.Id == f.Object.UUID.Guid);
        Check(attached.IsLink && attached.AttachedPrimId == f.Attachment.ID.Guid && attached.AttachedTo == RlvAttachmentPoint.Chest,
            "Shared link lost its target or live attachment state");
        Check(folder.Items.Single(i => i.Id == f.Unworn.UUID.Guid).WornOn == null, "Unworn wearable reported as worn");
        await f.Command("@getinv=1001,getattach:chest=1002,getoutfit:shirt=1003,getinvworn:Cuffs=1004");
        Check(f.Replies.Any(r => r == (1001, "Cuffs")), "Shared folder query returned the wrong folders");
        Check(f.Replies.Any(r => r == (1002, "1")) && f.Replies.Any(r => r == (1003, "1")), "Worn-state query replies incorrect");
        Check(f.Replies.Any(r => r.Channel == 1004 && r.Text.Length > 0), "Shared worn-state reply missing");
    }),
    ("Shared folder paths accept leading and trailing separators", async () =>
    {
        using var f = new Fixture();
        await f.Command("@getinvworn:Cuffs/=1051,getinvworn:/Cuffs/=1052");
        Check(f.Replies.Contains((1051, "|22")) && f.Replies.Contains((1052, "|22")),
            "A trailing separator made an existing outfit folder disappear");
    }),
    ("An unloaded inventory discovers the real #RLV root and resolves outfit links", async () =>
    {
        using var f = new Fixture();
        var oldStore = f.Client.Inventory.Store!;
        var outfits = new InventoryFolder(UUID.Random()) { Name = "Midori_Outfits", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        var outfit = new InventoryFolder(UUID.Random()) { Name = "Casual", ParentUUID = outfits.UUID, OwnerID = f.Owner };
        oldStore.UpdateNodeFor(outfits);
        oldStore.UpdateNodeFor(outfit);
        f.SharedFolder.Name = "Accessories";
        f.SharedFolder.ParentUUID = outfit.UUID;
        oldStore.UpdateNodeFor(f.SharedFolder);
        using var server = new InventoryServer(f.Client, f.Simulator);
        var store = new LibreMetaverse.Inventory(f.Client, f.Owner) { RootFolder = oldStore.RootFolder };
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client.Inventory, store);
        await f.Command("@getinv=1101,getinv:Midori_Outfits/Casual/=1104,getinvworn:Midori_Outfits/Casual/Accessories/=1102,getpathnew:chest=1103");
        Check(f.Replies.Contains((1101, "Midori_Outfits")), "#RLV was not discovered from the server's root inventory response");
        Check(f.Replies.Contains((1104, "Accessories")), "A nested outfit path was not loaded from the partial skeleton");
        Check(f.Replies.Contains((1102, "|22")), "Shared links or their worn state were missing from the unloaded inventory");
        Check(f.Replies.Contains((1103, "Midori_Outfits/Casual/Accessories")), "Reported outfit path was not relative to #RLV");
        Check(server.FolderRequests.Contains(f.SharedFolder.UUID), "Shared outfit contents were never fetched");
    }),
    ("Folder listing replies do not depend on an unrelated outfit loading", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        server.FailedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getinv=1201");
        Check(f.Replies.Contains((1201, "Cuffs")), "An unrelated folder's failed contents fetch prevented the root folder listing");
        Check(!server.FolderRequests.Contains(f.SharedFolder.UUID), "Listing root folder names fetched an outfit's contents");
    }),
    ("Attachover and detach resolve actual item IDs without loading unrelated outfits", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        f.Shirt.ParentUUID = f.Object.ParentUUID;
        f.Unworn.ParentUUID = f.Object.ParentUUID;
        store.UpdateNodeFor(f.Shirt);
        store.UpdateNodeFor(f.Unworn);
        var other = new InventoryFolder(UUID.Random()) { Name = "Other outfit", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        store.UpdateNodeFor(other);
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.FailedFolders.Add(other.UUID);
        // Simulate login's folder skeleton and an outfit link whose object is not rezzed yet.
        store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        store.RemoveNodeFor(f.Object);
        store.RemoveNodeFor(f.ObjectLink);
        f.Simulator.ObjectsPrimitives.TryRemove(f.Attachment.LocalID, out _);
        var packets = f.CapturePackets();
        await f.Command("@attachover:/Cuffs/=force");
        var added = packets().OfType<RezSingleAttachmentFromInvPacket>().SingleOrDefault();
        Check(added != null && added.ObjectData.ItemID == f.Object.UUID && added.ObjectData.AttachmentPt == (128 | (byte)AttachmentPoint.Chest),
            "Attachover did not request the actual linked object at its attachment point with add semantics");
        await f.Command("@detach:Cuffs/=force");
        var removed = packets().OfType<DetachAttachmentIntoInvPacket>().SingleOrDefault();
        Check(removed?.ObjectData.ItemID == f.Object.UUID, "Detach did not request removal of the actual linked object");
        Check(!server.FolderRequests.Contains(other.UUID), "An outfit action depended on another outfit's unavailable contents");
    }),
    ("A cached root missing #RLV is refreshed before reporting an empty inventory", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        var root = f.Client.Inventory.Store!.RootFolder!;
        var store = new LibreMetaverse.Inventory(f.Client, f.Owner) { RootFolder = root };
        store.GetNodeFor(root.UUID).NeedsUpdate = false;
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client.Inventory, store);
        await f.Command("@getinv=1301");
        Check(f.Replies.Contains((1301, "Cuffs")), "An outdated cached root hid #RLV");
        Check(server.FolderRequests.Contains(root.UUID), "The missing shared root was never checked with the server");
    }),
    ("Current outfit queries fetch unresolved links outside #RLV", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        f.Shirt.ParentUUID = f.Object.ParentUUID;
        store.UpdateNodeFor(f.Shirt);
        using var server = new InventoryServer(f.Client, f.Simulator);
        store.RemoveNodeFor(f.Shirt);
        store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        server.FailedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getoutfit:shirt=1401");
        Check(f.Replies.Contains((1401, "1")), "An unresolved Current Outfit link hid a worn wearable outside #RLV");
        Check(server.FolderRequests.Count == 0, "A wearable query fetched shared folders unnecessarily");
    }),
    ("Failed folder parsing remains retryable", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        var node = f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID);
        node.NeedsUpdate = true;
        server.MalformedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getinvworn:Cuffs=1501");
        Check(node.NeedsUpdate && f.Replies.All(r => r.Channel != 1501), "A failed server response was accepted as a loaded outfit");
        server.MalformedFolders.Clear();
        await f.Command("@getinvworn:Cuffs=1502");
        Check(f.Replies.Contains((1502, "|22")), "The failed folder was not retried successfully");
    }),
    ("An action through one link respects another folder's lock after a refresh", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        var other = new InventoryFolder(UUID.Random()) { Name = "Locked outfit", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        store.UpdateNodeFor(other);
        store.GetNodeFor(other.UUID).NeedsUpdate = false;
        var link = new InventoryItem(UUID.Random()) { Name = f.Object.Name, ParentUUID = other.UUID, OwnerID = f.Owner,
            AssetType = AssetType.Link, InventoryType = InventoryType.Object, AssetUUID = f.Object.UUID };
        store.UpdateNodeFor(link);
        await f.Command("@detachallthis:Locked outfit=n");
        using var server = new InventoryServer(f.Client, f.Simulator);
        store.RemoveNodeFor(link);
        store.GetNodeFor(other.UUID).NeedsUpdate = true;
        var packets = f.CapturePackets();
        await f.Command("@detach:Cuffs=force");
        Check(server.FolderRequests.Contains(other.UUID), "The locked folder's link was not refreshed");
        Check(!packets().OfType<DetachAttachmentIntoInvPacket>().Any(), "A folder lock was bypassed through another inventory link");
    }),
    ("Neighboring simulators cannot replace this avatar's attachment state", () =>
    {
        using var f = new Fixture();
        using var neighbor = new Simulator(f.Client, new IPEndPoint(IPAddress.Loopback, 13001), 2);
        var other = new Primitive { ID = UUID.Random(), LocalID = 200, ParentID = f.Client.Self.LocalID,
            IsAttachment = true, NameValues = f.Attachment.NameValues };
        other.PrimData.AttachmentPoint = AttachmentPoint.LeftHand;
        neighbor.ObjectsPrimitives[other.LocalID] = other;
        f.Client.Network.Simulators.Insert(0, neighbor);
        var item = f.Rlv.BuildInventoryMap().GetItemsById(f.Object.UUID.Guid).Single();
        Check(item.AttachedPrimId == f.Attachment.ID.Guid && item.AttachedTo == RlvAttachmentPoint.Chest,
            "A simulator-local ID collision changed the current avatar's attachment state");
        f.Client.Network.Simulators.Remove(neighbor);
        return Task.CompletedTask;
    }),
    ("Folder locks prevent removal through links and folder mutations", async () =>
    {
        using var f = new Fixture();
        await f.Command("@detachallthis:Cuffs=n");
        Check(!f.Rlv.CanDetach(f.ObjectLink), "Shared folder removal lock ignored");
        Check(!f.Rlv.CanModifyInventory(f.SharedFolder), "Locked folder could be renamed or trashed");
        await f.Command("@clear");
        Check(f.Rlv.CanDetach(f.ObjectLink), "Shared folder lock survived clear");
    }),
    ("Disable clears locks and cancels queued commands", async () =>
    {
        using var f = new Fixture();
        var commands = Enumerable.Range(0, 50).Select(_ => f.Command("@sendchat=n")).ToArray();
        f.Rlv.SetEnabled(false);
        try { await Task.WhenAll(commands); } catch (OperationCanceledException) { }
        Check(!f.Rlv.Enabled && f.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Disable left restrictions behind");
        Check(f.Rlv.CanAdd(f.Object, AttachmentPoint.Chest) && f.Rlv.CanDetach(f.ObjectLink), "Disabled RLV still blocked inventory");
        f.Rlv.SetEnabled(true);
        await f.Command("@versionnum=123");
        Check(f.Rlv.Service.Permissions.CanSendChat(), "Old queued command reached the new engine");
    }),
    ("Unsupported visual commands are advertised and valid batch commands still run", async () =>
    {
        using var f = new Fixture();
        await f.Command("@setenv_sky:blue=force,unknown=n,sendchat=n,getblacklist=301");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "Unsupported command prevented the valid command from running");
        Check(f.Replies.Any(r => r.Channel == 301 && r.Text.Contains("setenv")), "Unavailable visual features missing from blacklist");
        var first = f.Command("@sendchat=n");
        var second = f.Command("@sendchat=y");
        await Task.WhenAll(first, second);
        Check(f.Rlv.Service.Permissions.CanSendChat(), "Commands were processed out of arrival order");
    }),
    ("A failed inventory query does not discard later restrictions in the same message", async () =>
    {
        using var f = new Fixture();
        f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        await f.Command("@getinvworn:Cuffs=321,sendchat=n");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "A failed query discarded the restriction after it");
    }),
    ("Touch restrictions distinguish body attachments and HUDs", async () =>
    {
        using var f = new Fixture();
        await f.Command("@touchattachself=n");
        Check(!f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.Chest), "Body attachment touch lock ignored");
        Check(f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.HUDCenter), "Body touch lock incorrectly blocked HUD");
        await f.Command("@interact=n");
        Check(!f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.HUDCenter), "Interaction lock ignored");
    })
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Fixture : IDisposable
{
    public UUID Owner { get; } = UUID.Random();
    public GridClient Client { get; } = new();
    public CurrentOutfitFolder Outfit { get; }
    public RlvSession Rlv { get; }
    public Simulator Simulator { get; }
    public Primitive Attachment { get; }
    public InventoryObject Object { get; }
    public InventoryItem ObjectLink { get; }
    public InventoryWearable Shirt { get; }
    public InventoryWearable Unworn { get; }
    public InventoryFolder SharedFolder { get; }
    public List<(int Channel, string Text)> Replies { get; } = new();
    private readonly Guid _issuer = Guid.NewGuid();

    public Fixture()
    {
        typeof(AgentManager).GetProperty(nameof(AgentManager.AgentID))!.SetValue(Client.Self, Owner);
        typeof(AgentManager).GetField("localID", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Self, 42u);
        var store = new LibreMetaverse.Inventory(Client, Owner);
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Inventory, store);
        var root = Folder("My Inventory", UUID.Zero, FolderType.Root);
        store.RootFolder = root;
        var shared = Folder("#RLV", root.UUID);
        SharedFolder = Folder("Cuffs", shared.UUID);
        var objects = Folder("Objects", root.UUID);
        var cof = Folder("Current Outfit", root.UUID, FolderType.CurrentOutfit);
        Outfit = new CurrentOutfitFolder(Client);
        typeof(CurrentOutfitFolder).GetProperty(nameof(CurrentOutfitFolder.COF))!.SetValue(Outfit, cof);
        Object = new InventoryObject(UUID.Random()) { Name = "Cuffs (chest)", ParentUUID = objects.UUID, OwnerID = Owner,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        store.UpdateNodeFor(Object);
        ObjectLink = Link(Object, SharedFolder.UUID);
        Link(Object, cof.UUID);
        Shirt = new InventoryWearable(UUID.Random()) { Name = "Shirt", ParentUUID = SharedFolder.UUID, OwnerID = Owner,
            AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable, WearableType = WearableType.Shirt };
        store.UpdateNodeFor(Shirt);
        Link(Shirt, cof.UUID);
        Unworn = new InventoryWearable(UUID.Random()) { Name = "Pants", ParentUUID = SharedFolder.UUID, OwnerID = Owner,
            AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable, WearableType = WearableType.Pants };
        store.UpdateNodeFor(Unworn);
        foreach (var id in new[] { root.UUID, shared.UUID, SharedFolder.UUID, objects.UUID, cof.UUID }) store.GetNodeFor(id).NeedsUpdate = false;
        Simulator = new Simulator(Client, new IPEndPoint(IPAddress.Loopback, 13000), 1);
        Client.Network.Simulators.Add(Simulator);
        Client.Network.CurrentSim = Simulator;
        Attachment = new Primitive { ID = UUID.Random(), LocalID = 100, ParentID = Client.Self.LocalID, IsAttachment = true,
            NameValues = new[] { new NameValue($"AttachItemID STRING RW SV {Object.UUID}") } };
        Attachment.PrimData.AttachmentPoint = AttachmentPoint.Chest;
        Simulator.ObjectsPrimitives[Attachment.LocalID] = Attachment;
        Rlv = new RlvSession(Client, Outfit, action => action(), (channel, text) => Replies.Add((channel, text)));

        InventoryFolder Folder(string name, UUID parent, FolderType type = FolderType.None)
        {
            var folder = new InventoryFolder(UUID.Random()) { Name = name, ParentUUID = parent, OwnerID = Owner, PreferredType = type };
            store.UpdateNodeFor(folder);
            return folder;
        }
        InventoryItem Link(InventoryItem target, UUID parent)
        {
            var link = new InventoryItem(UUID.Random()) { Name = target.Name, AssetType = AssetType.Link,
                InventoryType = target.InventoryType, AssetUUID = target.UUID, ParentUUID = parent, OwnerID = Owner };
            store.UpdateNodeFor(link);
            return link;
        }
    }

    public Task Command(string text, Guid? issuer = null) => Rlv.ProcessCommandAsync(text, issuer ?? _issuer, "Test object");
    public Func<List<Packet>> CapturePackets()
    {
        var outbox = Channel.CreateUnbounded<NetworkManager.OutgoingPacket>();
        typeof(NetworkManager).GetField("_packetOutbox", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Network, outbox);
        return () =>
        {
            var packets = new List<Packet>();
            while (outbox.Reader.TryRead(out var sent))
            {
                var end = sent.Buffer.DataLength - 1;
                packets.Add(Packet.BuildPacket(sent.Buffer.Data, ref end, new byte[65536]));
            }
            return packets;
        };
    }
    public void Dispose()
    {
        Rlv.Dispose();
        Outfit.Dispose();
        Simulator.Dispose();
    }
}
