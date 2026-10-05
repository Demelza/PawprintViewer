using System.Net;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Appearance;
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
        await f.Command("@getinv=321,sendchat=n");
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
    public void Dispose()
    {
        Rlv.Dispose();
        Outfit.Dispose();
        Simulator.Dispose();
    }
}
