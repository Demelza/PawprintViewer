using LibreMetaverse;
using LibreMetaverse.RLV;

namespace Radegast.Gtk;

internal sealed partial class RlvSession
{
    private readonly SemaphoreSlim _inventoryLoad = new(1, 1);
    private readonly AsyncLocal<AttachmentPoint?> _requestedPoint = new();
    private readonly AsyncLocal<InventoryRequest?> _inventoryRequest = new();
    private sealed record InventoryRequest(string Path, bool Recursive, bool Worn, bool Shared = true, bool HonorLocks = false);

    private static string CommandOption(string command)
    {
        var colon = command.IndexOf(':');
        var equals = command.LastIndexOf('=');
        return colon >= 0 && equals > colon ? command[(colon + 1)..equals] : "";
    }

    private static InventoryRequest? InventoryRequestFor(string command, string behavior)
    {
        var path = CommandOption(command);
        if (behavior == "getinv") return new(path, Recursive: false, Worn: false);
        if (behavior == "getinvworn") return new(path, Recursive: true, Worn: true);
        if (behavior is "getattach" or "getoutfit") return new("", Recursive: false, Worn: true, Shared: false);
        if (command.EndsWith("=force", StringComparison.OrdinalIgnoreCase) &&
            behavior is "attach" or "attachall" or "attachover" or "attachallover" or "attachoverorreplace" or
                "attachalloverorreplace" or "detach" or "detachall" or "remattach" or "remoutfit")
            return new(path, Recursive: behavior.Contains("all"), Worn: true, HonorLocks: true);
        return null; // Searches and restrictions require the complete shared tree.
    }

    private InventoryItem? Resolve(InventoryItem item)
    {
        var visited = new HashSet<UUID>();
        while (item.IsLink())
        {
            if (!visited.Add(item.UUID) || _client.Inventory.Store == null ||
                !_client.Inventory.Store.TryGetValue(item.AssetUUID, out InventoryBase? target) ||
                target is not InventoryItem linked) return null;
            item = linked;
        }
        return item;
    }

    private List<InventoryBase> Contents(UUID id) => _client.Inventory.Store?.GetContents(id) ?? new();
    private InventoryFolder? SharedRoot => _client.Inventory.Store?.RootFolder is { } root
        ? Contents(root.UUID).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#RLV") : null;

    private async Task LoadFolderAsync(UUID id, CancellationToken token, bool force = false)
    {
        var store = _client.Inventory.Store;
        if (store == null || !store.TryGetNodeFor(id, out var node) || (!force && !node.NeedsUpdate)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        bool? success = null;
        void Updated(object? _, FolderUpdatedEventArgs e) { if (e.FolderID == id) success = e.Success; }
        _client.Inventory.FolderUpdated += Updated;
        try
        {
            await _client.Inventory.RequestFolderContentsAsync(id, _client.Self.AgentID, true, true,
                InventorySortOrder.ByName, timeout.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The library clears NeedsUpdate before parsing children. A parsing failure
            // must stay retryable instead of turning a partially loaded folder into an empty one.
            if (success != true || node.NeedsUpdate)
            {
                node.NeedsUpdate = true;
                throw new InvalidOperationException($"Inventory folder '{node.Data?.Name ?? id.ToString()}' could not be loaded. Try again shortly.");
            }
        }
        finally { _client.Inventory.FolderUpdated -= Updated; }
    }

    private async Task<UUID?> LoadSharedPathAsync(InventoryFolder shared, string path, CancellationToken token)
    {
        await LoadFolderAsync(shared.UUID, token).ConfigureAwait(false);
        if (path.Length == 0) return shared.UUID;
        var visited = new HashSet<Guid> { shared.UUID.Guid };
        while (true)
        {
            var map = BuildInventoryMap();
            if (map.TryGetFolderFromPath(path, false, out var target))
            {
                await LoadFolderAsync(new UUID(target.Id), token).ConfigureAwait(false);
                return new UUID(target.Id);
            }
            // Login normally supplies the folder skeleton. With a partial skeleton,
            // fetch the deepest known ancestor and resolve again after its reply.
            RlvSharedFolder? ancestor = null;
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                if (map.TryGetFolderFromPath(path[..slash], false, out ancestor)) break;
            if (ancestor == null || !visited.Add(ancestor.Id)) return null;
            await LoadFolderAsync(new UUID(ancestor.Id), token).ConfigureAwait(false);
        }
    }

    private async Task LoadSharedTreeAsync(UUID root, bool recursive, CancellationToken token)
    {
        var folders = new Queue<UUID>();
        var seen = new HashSet<UUID>();
        folders.Enqueue(root);
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<UUID>();
            while (folders.Count > 0 && batch.Count < 4)
            {
                var id = folders.Dequeue();
                if (seen.Add(id)) batch.Add(id);
            }
            await Task.WhenAll(batch.Select(id => LoadFolderAsync(id, token))).ConfigureAwait(false);
            var links = new List<InventoryItem>();
            foreach (var id in batch)
            {
                var contents = Contents(id);
                if (recursive)
                    foreach (var folder in contents.OfType<InventoryFolder>()) folders.Enqueue(folder.UUID);
                links.AddRange(contents.OfType<InventoryItem>().Where(i => i.AssetType == AssetType.Link));
            }
            await LoadLinkTargetsAsync(links, token).ConfigureAwait(false);
        }
    }

    private async Task LoadLinkTargetsAsync(IEnumerable<InventoryItem> links, CancellationToken token)
    {
        // Outfit folders commonly contain hundreds of links. Fetch targets in batches
        // rather than waiting for a separate HTTP request for every item.
        var missing = links.Where(i => Resolve(i) == null).Select(i => i.AssetUUID).Distinct().ToArray();
        if (missing.Length > 0 && _client.Network.CurrentSim?.Caps?.CapabilityURI("FetchInventory2") == null)
            throw new InvalidOperationException("Inventory item service is not ready. Try again shortly.");
        foreach (var batch in missing.Chunk(50))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _client.Inventory.RequestFetchInventoryAsync(batch.ToDictionary(id => id, _ => _client.Self.AgentID),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("Inventory link targets could not be loaded in time.");
            if (DebugCommands)
            {
                var unresolved = batch.Count(id => _client.Inventory.Store == null ||
                    !_client.Inventory.Store.TryGetValue(id, out InventoryBase? entry) || entry is not InventoryItem);
                if (unresolved > 0) Notify($"The server did not return {unresolved} inventory link target(s); those links are unresolved.");
            }
        }
    }

    private async Task LoadInventoryAsync(CancellationToken token)
    {
        await _inventoryLoad.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var request = _inventoryRequest.Value;
            var root = _client.Inventory.Store?.RootFolder;
            if (root == null) throw new InvalidOperationException("Inventory is still initializing.");
            if (request?.Shared != false)
            {
                var rootWasLoaded = !_client.Inventory.Store!.GetNodeFor(root.UUID).NeedsUpdate;
                await LoadFolderAsync(root.UUID, token).ConfigureAwait(false);
                var shared = SharedRoot;
                // A newly created #RLV folder may be missing from an older cached root.
                if (shared == null && rootWasLoaded)
                {
                    await LoadFolderAsync(root.UUID, token, force: true).ConfigureAwait(false);
                    shared = SharedRoot;
                }
                if (shared != null)
                {
                    if (request == null) await LoadSharedTreeAsync(shared.UUID, true, token).ConfigureAwait(false);
                    else
                    {
                        var target = await LoadSharedPathAsync(shared, request.Path, token).ConfigureAwait(false);
                        if (target.HasValue && request.Worn)
                            await LoadSharedTreeAsync(target.Value, request.Recursive, token).ConfigureAwait(false);
                        // Links in another locked folder must still block adding/removing the same item.
                        if (request.HonorLocks)
                            foreach (var id in Service.Restrictions.GetLockedFolders().Keys)
                                await LoadSharedTreeAsync(new UUID(id), false, token).ConfigureAwait(false);
                    }
                }
            }
            if (request?.Worn == false) return;
            var outfitLinks = await _outfit.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
            await LoadLinkTargetsAsync(outfitLinks, token).ConfigureAwait(false);
            foreach (var id in WornIds())
                if (_client.Inventory.Store != null && !_client.Inventory.Store.TryGetValue(id, out InventoryBase? _))
                    await FetchItemAsync(id, token).ConfigureAwait(false);
        }
        finally { _inventoryLoad.Release(); }
    }

    private async Task<InventoryItem?> FetchItemAsync(UUID id, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _client.Inventory.FetchItemHttpAsync(id, _client.Self.AgentID, timeout.Token).ConfigureAwait(false);
    }

    private HashSet<UUID> WornIds()
    {
        var ids = _client.Appearance.GetWearables().Select(w => w.ItemID).ToHashSet();
        ids.UnionWith(_client.Appearance.GetAttachmentsByItemId().Keys);
        ids.UnionWith(_client.Self.ActiveGestures.Keys);
        if (_outfit.COF != null)
            foreach (var item in Contents(_outfit.COF.UUID).OfType<InventoryItem>())
                if (Resolve(item) is InventoryWearable real) ids.Add(real.UUID);
        return ids;
    }

    internal InventoryMap BuildInventoryMap()
    {
        var worn = WornIds();
        var points = _client.Appearance.GetAttachmentsByItemId();
        var roots = MyAttachments()
            .Select(p => (Id: AttachmentItemId(p), Prim: p)).Where(p => p.Id != UUID.Zero)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Prim);
        worn.UnionWith(roots.Keys);
        var shared = SharedRoot;
        var converted = new RlvSharedFolder(shared?.UUID.Guid ?? Guid.Empty, "#RLV");
        var sharedIds = new HashSet<UUID>();
        var seen = new HashSet<UUID>();

        (RlvAttachmentPoint? Point, Guid? Prim, RlvWearableType? Wearable, RlvGestureState? Gesture) State(InventoryItem item)
        {
            if (item is InventoryGesture)
                return (null, null, null, _client.Self.ActiveGestures.ContainsKey(item.UUID) ? RlvGestureState.Active : RlvGestureState.Inactive);
            if (!worn.Contains(item.UUID)) return (null, null, null, null);
            if (item is InventoryWearable wearable) return (null, null, (RlvWearableType)wearable.WearableType, null);
            if (roots.TryGetValue(item.UUID, out var prim))
                return ((RlvAttachmentPoint)prim.PrimData.AttachmentPoint, prim.ID.Guid, null, null);
            if (points.TryGetValue(item.UUID, out var point)) return ((RlvAttachmentPoint)point, null, null, null);
            return (null, null, null, null);
        }

        void AddFolder(UUID id, RlvSharedFolder folder)
        {
            if (!seen.Add(id)) return;
            foreach (var entry in Contents(id))
            {
                if (entry is InventoryFolder child)
                {
                    AddFolder(child.UUID, folder.AddChild(child.UUID.Guid, child.Name ?? child.UUID.ToString()));
                    continue;
                }
                if (entry is not InventoryItem link || Resolve(link) is not { } real ||
                    real.AssetType is not (AssetType.Bodypart or AssetType.Clothing or AssetType.Object or AssetType.Gesture)) continue;
                var state = State(real);
                folder.AddItem(real.UUID.Guid, string.IsNullOrEmpty(link.Name) ? real.UUID.ToString() : link.Name,
                    link.IsLink(), state.Point, state.Prim, state.Wearable, state.Gesture);
                sharedIds.Add(real.UUID);
            }
        }

        if (shared != null) AddFolder(shared.UUID, converted);
        var external = new List<RlvInventoryItem>();
        foreach (var id in worn)
        {
            if (sharedIds.Contains(id) || _client.Inventory.Store == null ||
                !_client.Inventory.Store.TryGetValue(id, out InventoryBase? entry) ||
                entry is not InventoryItem item || Resolve(item) is not { } real) continue;
            var state = State(real);
            external.Add(new RlvInventoryItem(real.UUID.Guid, string.IsNullOrEmpty(real.Name) ? real.UUID.ToString() : real.Name,
                false, real.ParentUUID.Guid, state.Point, state.Prim, state.Wearable, state.Gesture));
        }
        return new InventoryMap(converted, external);
    }

    public async Task<(bool Success, InventoryMap? InventoryMap)> TryGetInventoryMapAsync(CancellationToken token)
    {
        await LoadInventoryAsync(token).ConfigureAwait(false);
        var map = BuildInventoryMap();
        if (DebugCommands)
            Notify(map.Root.Id == Guid.Empty ? "No #RLV folder was found directly under My Inventory."
                : $"Shared inventory root: My Inventory/#RLV ({map.Root.Id}); {map.Folders.Count - 1} cached folders, {map.Items.Count} resolved items.");
        return (true, map);
    }

    private static AttachmentPoint SavedPoint(InventoryItem item) => item switch
    {
        InventoryObject obj => obj.AttachPoint,
        InventoryAttachment attachment => attachment.AttachmentPoint,
        _ => AttachmentPoint.Default
    };

    public bool CanAttach(InventoryItem? item) => item != null && CanAdd(item, _requestedPoint.Value);

    public bool CanAdd(InventoryItem item, AttachmentPoint? point, InventoryMap? inventoryMap = null)
    {
        if (!Enabled) return true;
        var real = Resolve(item);
        if (real == null) return false;
        var variants = (inventoryMap ?? BuildInventoryMap()).GetItemsById(real.UUID.Guid);
        var wearable = real is InventoryWearable w ? (RlvWearableType?)w.WearableType : null;
        var attachPoint = real is InventoryObject or InventoryAttachment ? (RlvAttachmentPoint?)EffectivePoint(real, point) : null;
        return variants.Count > 0
            ? variants.All(i => Service.Permissions.CanAttach(i.FolderId, i.Folder != null, attachPoint, wearable))
            : Service.Permissions.CanAttach(real.ParentUUID.Guid, IsShared(real.ParentUUID), attachPoint, wearable);
    }

    private static AttachmentPoint EffectivePoint(InventoryItem item, AttachmentPoint? requested) =>
        !requested.HasValue || requested.Value == AttachmentPoint.Default ? SavedPoint(item) : requested.Value;

    public bool CanDetach(InventoryItem? item)
    {
        if (!Enabled) return true;
        if (item == null || Resolve(item) is not { } real) return false;
        var map = BuildInventoryMap();
        var variants = map.GetItemsById(real.UUID.Guid);
        if (variants.Count > 0) return variants.All(Service.Permissions.CanDetach);
        var prim = MyAttachments().FirstOrDefault(p => AttachmentItemId(p) == real.UUID);
        return Service.Permissions.CanDetach(real.UUID.Guid, prim?.ID.Guid, real.ParentUUID.Guid, IsShared(real.ParentUUID),
            real is InventoryObject or InventoryAttachment ? (RlvAttachmentPoint?)(prim?.PrimData.AttachmentPoint ?? SavedPoint(real)) : null,
            real is InventoryWearable w ? (RlvWearableType?)w.WearableType : null);
    }

    private bool IsShared(UUID folderId)
    {
        var shared = SharedRoot;
        var seen = new HashSet<UUID>();
        while (shared != null && folderId != UUID.Zero && seen.Add(folderId))
        {
            if (folderId == shared.UUID) return true;
            if (_client.Inventory.Store == null || !_client.Inventory.Store.TryGetValue(folderId, out InventoryBase? folder)) break;
            folderId = folder.ParentUUID;
        }
        return false;
    }

    public bool CanModifyInventory(InventoryBase entry)
    {
        if (!Enabled) return true;
        var map = BuildInventoryMap();
        var seen = new HashSet<UUID>();
        bool Allowed(InventoryBase value)
        {
            if (!seen.Add(value.UUID)) return true;
            if (Service.Restrictions.TryGetLockedFolder(value.UUID.Guid, out var locked) &&
                (!locked.CanAttach || !locked.CanDetach)) return false;
            if (value is InventoryFolder) return Contents(value.UUID).All(Allowed);
            if (value is InventoryItem item && Resolve(item) is { } real)
                return map.GetItemsById(real.UUID.Guid).All(i => Service.Permissions.CanDetach(i) &&
                    Service.Permissions.CanAttach(i.FolderId, i.Folder != null, i.AttachedTo, i.WornOn));
            return true;
        }
        return Allowed(entry);
    }

    // Bulk COF addition in LibreMetaverse 3.1.5 does not invoke CanAttach/CanDetach.
    // Check additions and everything they would replace before calling that path.
    public async Task AddAsync(InventoryItem item, AttachmentPoint? point, bool replace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var real = Resolve(item) ?? throw new InvalidOperationException("The inventory link target is not loaded.");
        if (!CanAdd(item, point)) throw new InvalidOperationException("Adding this item is restricted by RLV.");
        var actualPoint = EffectivePoint(real, point);
        if (Enabled && (replace || real.AssetType == AssetType.Bodypart))
        {
            var worn = WornIds();
            var points = _client.Appearance.GetAttachmentsByItemId();
            foreach (var prim in MyAttachments().Where(p => p.PrimData.AttachmentPoint == actualPoint))
            {
                var id = AttachmentItemId(prim);
                if (real is not (InventoryObject or InventoryAttachment) || id == real.UUID) continue;
                var detachable = _client.Inventory.Store != null &&
                    _client.Inventory.Store.TryGetValue(id, out InventoryBase? existing) && existing is InventoryItem old
                    ? CanDetach(old)
                    : Service.Permissions.CanDetach(id == UUID.Zero ? null : id.Guid, prim.ID.Guid,
                        null, false, (RlvAttachmentPoint)actualPoint, null);
                if (!detachable) throw new InvalidOperationException("An attachment this would replace is locked by RLV.");
            }
            foreach (var id in worn)
            {
                if (_client.Inventory.Store == null || !_client.Inventory.Store.TryGetValue(id, out InventoryBase? entry) ||
                    entry is not InventoryItem old || old.UUID == real.UUID) continue;
                var replaces = real is InventoryWearable w && old is InventoryWearable other && w.WearableType == other.WearableType ||
                    real is InventoryObject or InventoryAttachment && old is InventoryObject or InventoryAttachment &&
                    (points.TryGetValue(old.UUID, out var oldPoint) ? oldPoint : SavedPoint(old)) == actualPoint;
                if (replaces && !CanDetach(old)) throw new InvalidOperationException("An item this would replace is locked by RLV.");
            }
        }
        if (real is InventoryObject or InventoryAttachment)
        {
            // COF's policy callback has no attachment-point parameter. Carry the
            // requested point through its async call instead of checking stale metadata.
            var previous = _requestedPoint.Value;
            _requestedPoint.Value = actualPoint;
            try
            {
                var links = await _outfit.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!CanAdd(item, actualPoint)) throw new InvalidOperationException("Adding this item is restricted by RLV.");
                if (links.Any(link => link.ResolvedItemID == real.UUID) && !_client.Appearance.GetAttachmentsByItemId().ContainsKey(real.UUID))
                {
                    _client.Appearance.Attach(real, actualPoint, replace);
                    await ReportItemChangeAsync(new() { real }, null, token).ConfigureAwait(false);
                }
                else await _outfit.AttachAsync(real, actualPoint, replace, token).ConfigureAwait(false);
            }
            finally { _requestedPoint.Value = previous; }
        }
        else await _outfit.AddToOutfitAsync(real, replace, token).ConfigureAwait(false);
    }

    public async Task RemoveAsync(InventoryItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var real = Resolve(item) ?? throw new InvalidOperationException("The inventory link target is not loaded.");
        if (!CanDetach(real)) throw new InvalidOperationException("Removing this item is restricted by RLV.");
        if (real is InventoryObject or InventoryAttachment) await _outfit.DetachAsync(real, token).ConfigureAwait(false);
        else await _outfit.RemoveFromOutfitAsync(real, token).ConfigureAwait(false);
    }

    public async Task ReportItemChangeAsync(List<InventoryItem>? added, List<InventoryItem>? removed, CancellationToken token = default)
    {
        if (!Enabled) return;
        foreach (var (items, adding) in new[] { (added, true), (removed, false) })
            foreach (var item in items ?? new())
            {
                var real = Resolve(item);
                if (real == null) continue;
                var variant = BuildInventoryMap().GetItemsById(real.UUID.Guid).FirstOrDefault(i => i.Folder != null);
                var folderId = variant?.FolderId ?? real.ParentUUID.Guid;
                var shared = variant != null || IsShared(real.ParentUUID);
                if (real is InventoryWearable wearable)
                {
                    if (adding) await Service.ReportItemWornAsync(folderId, shared, (RlvWearableType)wearable.WearableType, token);
                    else await Service.ReportItemUnwornAsync(real.UUID.Guid, folderId, shared, (RlvWearableType)wearable.WearableType, token);
                }
                else if (real is InventoryObject or InventoryAttachment)
                {
                    var prim = MyAttachments().FirstOrDefault(p => AttachmentItemId(p) == real.UUID);
                    var point = (RlvAttachmentPoint)((adding ? _requestedPoint.Value : null) ?? prim?.PrimData.AttachmentPoint ?? SavedPoint(real));
                    if (adding) await Service.ReportItemAttachedAsync(folderId, shared, point, token);
                    else await Service.ReportItemDetachedAsync(real.UUID.Guid, prim?.ID.Guid ?? Guid.Empty, folderId, shared, point, token);
                }
            }
        RaiseChanged();
    }
}
