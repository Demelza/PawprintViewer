using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;
using Radegast;

internal static class InventoryCacheChecks
{
    private static readonly SavedLogin Identity = new("Alice Resident", "https://grid.test/login");

    public static async Task RoundTripAndAppearance()
    {
        using var directory = new CacheDirectory();
        using var f = new Fixture();
        var old = f.Client.Inventory.Store!;
        VersionFolders(old);
        f.Object.Description = "Cached attachment metadata";
        f.Object.CreationDate = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        f.Object.Permissions = new((uint)PermissionMask.All, 0, 0, (uint)PermissionMask.Copy, (uint)PermissionMask.All);
        var cache = new InventoryDiskCache(directory.Path);
        await cache.SaveAsync(Identity, cache.Capture(Identity, old));
        using var server = new InventoryServer(f.Client, f.Simulator);
        var fresh = Skeleton(f.Client, old);
        Check(cache.Restore(Identity, fresh) == 4, "The matching login skeleton did not restore its object, shared link and wearables");
        Check(fresh[f.Object.UUID] is InventoryObject, "The cache lost the attachment's inventory type");
        var obj = (InventoryObject)fresh[f.Object.UUID];
        Check(obj.AttachPoint == AttachmentPoint.Chest && obj.OwnerID == f.Owner && obj.Description == f.Object.Description,
            "The cache lost ownership, description or attachment point metadata");
        Check(obj.CreationDate == f.Object.CreationDate, $"The cache changed creation time: {obj.CreationDate:O}, expected {f.Object.CreationDate:O}");
        Check(obj.Permissions == f.Object.Permissions, "The cache changed permissions: " + obj.Permissions + "; expected " + f.Object.Permissions);
        Check(fresh[f.Shirt.UUID] is InventoryWearable shirt && shirt.WearableType == WearableType.Shirt,
            "The cache lost wearable type metadata");
        Check(!fresh.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate && !fresh.GetNodeFor(f.Object.ParentUUID).NeedsUpdate,
            "Complete matching folders were refetched unnecessarily");
        Check(fresh.GetNodeFor(old.RootFolder!.UUID).NeedsUpdate && fresh.GetNodeFor(f.Outfit.COF!.UUID).NeedsUpdate &&
            fresh.GetContents(f.Outfit.COF.UUID).Count == 0, "Root or Current Outfit contents were incorrectly trusted from disk");
        await f.Rlv.WarmInventoryAsync(CancellationToken.None);
        await f.Command("@getinvworn:Cuffs=2101");
        Check(f.Replies.Contains((2101, "|22")) && !server.FolderRequests.Contains(f.SharedFolder.UUID) && server.ItemBatchSizes.Count <= 1,
            "Cached RLV links required another folder/target download or lost live worn state");
        await f.Client.Appearance.RequestAgentWornAsync();
        // The SDK revalidates COF targets in one batch for each fresh COF reply.
        // Cached targets avoid its separate, sequential per-link fallback requests.
        Check(server.ItemBatchSizes.Count <= 2 && server.ItemBatchSizes.All(size => size == 2) &&
            f.Client.Appearance.GetWearables().Any(wearable => wearable.ItemID == f.Shirt.UUID) &&
            f.Client.Appearance.GetAttachmentsByItemId().ContainsKey(f.Object.UUID),
            "Appearance failed to use cached typed link targets after fetching the live COF");
    }

    public static async Task InvalidationsAndPartialFolders()
    {
        using var directory = new CacheDirectory();
        using var f = new Fixture();
        var old = f.Client.Inventory.Store!;
        VersionFolders(old);
        var empty = new InventoryFolder(UUID.Random()) { Name = "Known empty", ParentUUID = f.SharedFolder.UUID,
            OwnerID = f.Owner, Version = 9, DescendentCount = 0 };
        var partial = new InventoryFolder(UUID.Random()) { Name = "Not fetched", ParentUUID = f.SharedFolder.UUID,
            OwnerID = f.Owner, Version = 9, DescendentCount = 0 };
        old.UpdateNodeFor(empty); old.UpdateNodeFor(partial);
        old.GetNodeFor(empty.UUID).NeedsUpdate = false;
        old.GetNodeFor(partial.UUID).NeedsUpdate = true;
        old.GetNodeFor(f.Object.ParentUUID).NeedsUpdate = true; // Link target metadata, not a complete Objects folder.
        ((InventoryFolder)old[f.SharedFolder.UUID]).DescendentCount = old.GetContents(f.SharedFolder.UUID).Count;
        var disk = new InventoryDiskCache(directory.Path);
        var snapshot = disk.Capture(Identity, old);
        await disk.SaveAsync(Identity, snapshot);
        var fresh = Skeleton(f.Client, old);
        disk.Restore(Identity, fresh);
        Check(!fresh.GetNodeFor(empty.UUID).NeedsUpdate && fresh.GetNodeFor(partial.UUID).NeedsUpdate &&
            fresh.GetNodeFor(f.Object.ParentUUID).NeedsUpdate && fresh.Contains(f.Object.UUID),
            "An unfetched empty/partial folder became complete or useful partial link-target metadata was lost");

        fresh = Skeleton(f.Client, old);
        ((InventoryFolder)fresh[f.SharedFolder.UUID]).Version++;
        disk.Restore(Identity, fresh);
        Check(fresh.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate && !fresh.Contains(f.ObjectLink.UUID) && !fresh.Contains(f.Shirt.UUID),
            "A changed folder resurrected outdated items from the cache");
        fresh = Skeleton(f.Client, old);
        ((InventoryFolder)fresh[f.SharedFolder.UUID]).ParentUUID = f.Object.ParentUUID;
        fresh.UpdateNodeFor(fresh[f.SharedFolder.UUID]);
        disk.Restore(Identity, fresh);
        Check(!fresh.Contains(f.Shirt.UUID), "A moved folder with the same version reused its old contents");
        fresh = Skeleton(f.Client, old);
        fresh.RemoveNodeFor(fresh[f.SharedFolder.UUID]);
        disk.Restore(Identity, fresh);
        Check(!fresh.Contains(f.SharedFolder.UUID) && !fresh.Contains(f.ObjectLink.UUID), "A deleted folder was resurrected from disk");
    }

    public static async Task IsolationAndCorruption()
    {
        using var directory = new CacheDirectory();
        using var f = new Fixture();
        var old = f.Client.Inventory.Store!;
        VersionFolders(old);
        var disk = new InventoryDiskCache(directory.Path);
        var snapshot = disk.Capture(Identity, old);
        await disk.SaveAsync(Identity, snapshot);
        var fresh = Skeleton(f.Client, old);
        Check(disk.Restore(Identity with { AccountName = "Bob Resident" }, fresh) == 0 &&
            disk.Restore(Identity with { LoginUri = "https://other.test/login" }, fresh) == 0,
            "Inventory metadata leaked between accounts or grids");
        Check(disk.Apply(Identity, fresh, snapshot with { Owner = Guid.NewGuid() }) == 0 &&
            disk.Apply(Identity, fresh, snapshot with { Root = Guid.NewGuid() }) == 0 &&
            disk.Apply(Identity, fresh, snapshot with { Format = 99 }) == 0, "A foreign/obsolete cache was applied");
        File.WriteAllText(disk.FilePath(Identity), "not gzip");
        Check(disk.Restore(Identity, fresh) == 0 && fresh.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate,
            "A corrupt cache crashed login or marked unloaded folders as complete");
        await disk.SaveAsync(Identity, snapshot);
        var bytes = File.ReadAllBytes(disk.FilePath(Identity));
        File.WriteAllBytes(disk.FilePath(Identity), bytes[..(bytes.Length / 2)]);
        Check(disk.Restore(Identity, fresh) == 0 && !Directory.EnumerateFiles(directory.Path, "*.tmp").Any(),
            "A truncated cache was trusted or saving left temporary files");
    }

    public static async Task CoalescingCancellationAndRefresh()
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var loader = new InventoryLoader(f.Client);
        var store = f.Client.Inventory.Store!;
        store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        var ghost = new InventoryItem(UUID.Random()) { OwnerID = f.Owner, ParentUUID = f.SharedFolder.UUID, Name = "Deleted elsewhere" };
        store.UpdateNodeFor(ghost);
        using var firstCancel = new CancellationTokenSource();
        var first = loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, firstCancel.Token);
        var second = loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, CancellationToken.None);
        await WaitUntil(() => server.FolderRequests.Count == 1);
        firstCancel.Cancel();
        try { await first; throw new InvalidOperationException("The cancelled folder waiter completed normally"); }
        catch (OperationCanceledException) { }
        Check(!second.IsCompleted, "Cancelling one waiter cancelled another consumer's shared request");
        server.Gate.SetResult(); await second;
        Check(!store.Contains(ghost.UUID) && !store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate && server.FolderRequests.Count == 1,
            "Shared folder loading duplicated requests or kept items omitted by the server");
        server.MalformedFolders.Add(f.SharedFolder.UUID);
        try { await loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, CancellationToken.None, true); }
        catch (InvalidOperationException) { }
        Check(store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate, "A malformed refresh poisoned the folder's loaded state");
        server.MalformedFolders.Clear();
        await loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, CancellationToken.None);
        Check(!store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate && server.FolderRequests.Count == 3, "The failed folder could not be retried");
    }

    public static async Task EarlyRlvAndReset()
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        var caps = Field<Dictionary<string, Uri>>(f.Simulator.Caps!, "_Caps");
        var saved = caps.ToArray(); caps.Clear();
        f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        f.Simulator.ObjectsPrimitives.TryRemove(f.Attachment.LocalID, out _);
        var packets = f.CapturePackets();
        var command = f.Command("@attachover:Cuffs=force");
        await Task.Delay(150);
        Check(!command.IsCompleted && server.FolderRequests.Count == 0, "An early RLV action failed before inventory capabilities arrived");
        foreach (var entry in saved) caps[entry.Key] = entry.Value;
        await command.WaitAsync(TimeSpan.FromSeconds(3));
        Check(packets().OfType<RezSingleAttachmentFromInvPacket>().Any(packet => packet.ObjectData.ItemID == f.Object.UUID),
            "The first queued outfit action failed after capabilities became ready");

        using var loader = new InventoryLoader(f.Client);
        caps.Clear();
        f.Client.Inventory.Store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        var waiting = loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, CancellationToken.None);
        loader.Reset();
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("Reset did not cancel an old capability wait"); }
        catch (OperationCanceledException) { }
        foreach (var entry in saved) caps[entry.Key] = entry.Value;
        await loader.LoadFolderAsync(f.SharedFolder.UUID, f.Owner, CancellationToken.None);
        Check(server.FolderRequests.Count == 2, "An old startup callback survived reset or prevented a fresh request");
    }

    public static async Task SavingAndIdleResources()
    {
        using var directory = new CacheDirectory();
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        VersionFolders(store);
        var clock = new ReconnectClock();
        var disk = new InventoryDiskCache(directory.Path);
        using var session = new SessionInventoryCache(f.Client, disk, clock);
        session.Start(Identity);
        store.UpdateNodeFor(new InventoryItem(UUID.Random()) { Name = "New metadata", OwnerID = f.Owner, ParentUUID = f.Object.ParentUUID });
        store.UpdateNodeFor(new InventoryItem(UUID.Random()) { Name = "More metadata", OwnerID = f.Owner, ParentUUID = f.Object.ParentUUID });
        Check(clock.PendingTimers == 1, "Inventory changes scheduled multiple disk writers");
        clock.Advance(5); await session.FlushAsync();
        Check(File.Exists(disk.FilePath(Identity)) && clock.PendingTimers == 0, "The changed inventory was not cached or kept an idle timer");
        clock.Advance(8 * 3600);
        Check(clock.PendingTimers == 0, "Idle accounts kept polling or writing inventory cache files");
        session.Stop();
        store.UpdateNodeFor(new InventoryItem(UUID.Random()) { Name = "After stop", OwnerID = f.Owner, ParentUUID = f.Object.ParentUUID });
        Check(clock.PendingTimers == 0 && !session.HasSession, "A stopped cache retained inventory subscriptions");
        await session.FlushAsync();
    }

    public static async Task AccountLoginLifecycle()
    {
        using var directory = new CacheDirectory();
        var clock = new ReconnectClock();
        using var account = new AccountSession(action => action(), new AccountSettingsStore(System.IO.Path.Combine(directory.Path, "settings")),
            clock, System.IO.Path.Combine(directory.Path, "cache"));
        using var f = new Fixture(account);
        account.Net.ClientLoggingIn += (_, e) => e.Cancel = true;
        account.Login(Identity.AccountName, "synthetic-password", new Radegast.Grid("test", "Test Grid", Identity.LoginUri), StartLocationType.Home);
        var old = account.Client.Inventory.Store!;
        VersionFolders(old);
        var disk = new InventoryDiskCache(System.IO.Path.Combine(directory.Path, "cache"));
        await disk.SaveAsync(Identity, disk.Capture(Identity, old));
        using var server = new InventoryServer(account.Client, f.Simulator);
        var skeleton = Skeleton(account.Client, old);
        var folders = new List<InventoryFolder>();
        var pending = new Queue<InventoryFolder>(); pending.Enqueue(skeleton.RootFolder!);
        while (pending.TryDequeue(out var folder))
        {
            folders.Add(folder);
            foreach (var child in skeleton.GetContents(folder.UUID).OfType<InventoryFolder>()) pending.Enqueue(child);
        }
        var reply = new LoginResponseData { AgentID = f.Owner, InventoryRoot = old.RootFolder!.UUID,
            InventorySkeleton = folders.ToArray(), LibraryRoot = UUID.Random(), LibrarySkeleton = Array.Empty<InventoryFolder>() };
        // Exercise the installed callbacks in their real registration order,
        // without connecting to a grid or changing unrelated SDK session state.
        var callbacks = (NetworkManager.LoginResponseCallback)typeof(NetworkManager)
            .GetField("OnLoginResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(account.Client.Network)!;
        foreach (NetworkManager.LoginResponseCallback callback in callbacks.GetInvocationList())
            if (callback.Target is InventoryManager or AccountSession) callback(true, false, "Synthetic login", "", reply);
        var fresh = account.Client.Inventory.Store!;
        Check(fresh != skeleton && fresh.Contains(f.Object.UUID) && !fresh.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate &&
            fresh.GetNodeFor(f.Outfit.COF!.UUID).NeedsUpdate, "Login restored before the fresh skeleton or trusted stale COF contents");
        typeof(NetCom).GetProperty(nameof(NetCom.IsLoggedIn))!.SetValue(account.Net, true);
        typeof(NetCom).GetMethod("OnClientLoginStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(account.Net,
            new object[] { new LoginProgressEventArgs(LoginStatus.Success, "Synthetic login", "") });
        await WaitUntil(() => Field<LibreMetaverse.Inventory>(account, "_inventoryWarmupStore") == fresh &&
            typeof(AccountSession).GetField("_inventoryWarmupCancel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(account) == null);
        Check(server.FolderRequests.Contains(f.Outfit.COF!.UUID) && !server.FolderRequests.Contains(f.SharedFolder.UUID) &&
            server.ItemBatchSizes.All(size => size == 2), "Login warmup crawled uncached outfits or fetched COF targets individually");
        var sessionCache = Field<SessionInventoryCache>(account, "_inventoryCache");
        await sessionCache.FlushAsync();
        typeof(NetCom).GetProperty(nameof(NetCom.IsLoggedIn))!.SetValue(account.Net, false);
        typeof(NetCom).GetMethod("OnClientDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(account.Net,
            new object[] { new DisconnectedEventArgs(NetworkManager.DisconnectType.NetworkTimeout, "Synthetic disconnect") });
        Check(!sessionCache.HasSession && clock.PendingTimers == 0, "Disconnect retained cache subscriptions or startup timers");
        await sessionCache.FlushAsync();
    }

    private static void VersionFolders(LibreMetaverse.Inventory store)
    {
        var pending = new Queue<UUID>(); pending.Enqueue(store.RootFolder!.UUID);
        while (pending.TryDequeue(out var id))
        {
            var folder = (InventoryFolder)store[id];
            folder.Version = 7;
            var contents = store.GetContents(id);
            folder.DescendentCount = contents.Count;
            foreach (var child in contents.OfType<InventoryFolder>()) pending.Enqueue(child.UUID);
        }
    }

    private static LibreMetaverse.Inventory Skeleton(GridClient client, LibreMetaverse.Inventory source)
    {
        var result = new LibreMetaverse.Inventory(client, source.Owner);
        var pending = new Queue<InventoryFolder>(); pending.Enqueue(source.RootFolder!);
        while (pending.TryDequeue(out var original))
        {
            var folder = new InventoryFolder(original.UUID) { Name = original.Name, ParentUUID = original.ParentUUID,
                OwnerID = original.OwnerID, Version = original.Version, PreferredType = original.PreferredType };
            if (original.UUID == source.RootFolder!.UUID) result.RootFolder = folder;
            else result.UpdateNodeFor(folder);
            foreach (var child in source.GetContents(original.UUID).OfType<InventoryFolder>()) pending.Enqueue(child);
        }
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client.Inventory, result);
        return result;
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class CacheDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pawprint-inventory-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
