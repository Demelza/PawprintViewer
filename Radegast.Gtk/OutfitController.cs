using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.Packets;
using System.Collections.Concurrent;

namespace Radegast.Gtk;

/// <summary>
/// One writer for the live Current Outfit. Inventory acknowledgements precede
/// attachment packets; baking never replays an earlier attachment list.
/// </summary>
internal sealed partial class OutfitController : IDisposable
{
    private readonly GridClient _client;
    private readonly InventoryLoader _inventory;
    private readonly bool _ownsInventory;
    private readonly CompositeCurrentOutfitPolicy _policy = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly object _state = new();
    private CancellationTokenSource _session = new();
    private LibreMetaverse.Inventory? _store;
    private InventoryFolder? _cof;
    private InventoryItem[] _links = [];
    private readonly ConcurrentDictionary<UUID, AttachmentPoint> _points = new();
    private LibreMetaverse.Inventory? _restoredStore;
    private volatile bool _ready;
    private bool _disposed;
    private int _batchDepth;
    private bool _clientSideBaking;
    private Caps? _watchedCaps;
    private Simulator? _startingSim;
    private Task? _regionStart;

    public InventoryFolder? COF
    {
        get
        {
            var store = _client.Inventory.Store;
            return store?.RootFolder is { } root ? store.GetContents(root.UUID).OfType<InventoryFolder>()
                .FirstOrDefault(f => f.PreferredType == FolderType.CurrentOutfit) : null;
        }
    }
    public bool IsReady => _ready && ReferenceEquals(_store, _client.Inventory.Store);
    public event Action? Changed;

    public OutfitController(GridClient client, InventoryLoader? inventory = null)
    {
        _client = client;
        _inventory = inventory ?? new InventoryLoader(client);
        _ownsInventory = inventory == null;
        // The SDK's SSB workflow also re-rezzes the whole outfit and waits for a
        // rendered avatar. This viewer coordinates SSB separately. CSB is enabled
        // after capabilities arrive only on regions that require it.
        client.Settings.Agent.SendAppearance = false;
        client.Network.SimChanged += OnSimChanged;
        client.Network.Disconnected += OnDisconnected;
        client.Network.RegisterCallback(PacketType.RebakeAvatarTextures, OnRebake);
    }

    public void AddPolicy(ICurrentOutfitPolicy policy) => _policy.AddPolicy(policy);
    public void RemovePolicy(ICurrentOutfitPolicy policy) => _policy.RemovePolicy(policy);

    public HashSet<UUID> WornIds => IsReady
        ? Volatile.Read(ref _links).Select(l => l.ResolvedItemID).ToHashSet() : [];

    public IDisposable BeginBatch()
    {
        lock (_state) { _batchDepth++; CancelBakeLocked(); }
        return new Batch(this);
    }

    private sealed class Batch(OutfitController owner) : IDisposable
    {
        private OutfitController? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value == null) return;
            lock (value._state)
            {
                value._batchDepth--;
                if (value._batchDepth == 0) value.ScheduleBakeLocked();
            }
        }
    }

    public async Task<List<InventoryItem>> GetCurrentOutfitLinksAsync(CancellationToken token)
    {
        using var session = SessionToken(token);
        await _changes.WaitAsync(session.Token).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(session.Token).ConfigureAwait(false);
            return _links.ToList();
        }
        finally { _changes.Release(); }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        using var session = SessionToken(token);
        await _changes.WaitAsync(session.Token).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(session.Token).ConfigureAwait(false);
            await FetchCofAsync(session.Token, true).ConfigureAwait(false);
            Changed?.Invoke();
        }
        finally { _changes.Release(); }
    }

    private CancellationTokenSource SessionToken(CancellationToken token)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _session.Token);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken token)
    {
        var store = _client.Inventory.Store ?? throw new InvalidOperationException("Inventory is still initializing.");
        if (_ready && ReferenceEquals(_store, store)) return;
        _ready = false;
        _store = store;
        _cof = COF;
        if (_cof == null)
        {
            await _inventory.LoadFolderAsync((store.RootFolder ?? throw new InvalidOperationException("Inventory is still initializing.")).UUID, _client.Self.AgentID, token).ConfigureAwait(false);
            _cof = COF ?? throw new InvalidOperationException("The Current Outfit folder is unavailable.");
        }
        await FetchCofAsync(token, false).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Initial restoration is additive. A later bake cannot detach or restore
        // this list. Subsequent outfit changes use only individual differences.
        if (!ReferenceEquals(_restoredStore, store)) foreach (var item in RealItems())
        {
            if (item is InventoryObject or InventoryAttachment)
            {
                var point = Point(item);
                _points[item.UUID] = point;
                if (!IsRezzed(item.UUID)) _client.Appearance.Attach(item, point, false);
            }
            else if (item is InventoryGesture gesture && !_client.Self.ActiveGestures.ContainsKey(item.UUID))
                _client.Self.ActivateGesture(item.UUID, gesture.AssetUUID);
        }
        _restoredStore = store;
        _ready = true;
        lock (_state) { _bakeDirty = true; ScheduleBakeLocked(); }
        Changed?.Invoke();
    }

    private async Task FetchCofAsync(CancellationToken token, bool force)
    {
        var store = _store!;
        var id = _cof!.UUID;
        await _inventory.LoadFolderAsync(id, _client.Self.AgentID, token, force).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (store != _client.Inventory.Store) throw new OperationCanceledException("Inventory changed during outfit loading.");
        _cof = (InventoryFolder)store[id];
        var links = store.GetContents(id).OfType<InventoryItem>().Where(l => l.IsLink()).ToArray();
        await _inventory.LoadItemsAsync(links.Select(l => l.ResolvedItemID), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _links = links;
    }

    private InventoryItem? Resolve(InventoryItem item)
    {
        var visited = new HashSet<UUID>();
        while (item.IsLink())
        {
            if (!visited.Add(item.UUID) || _client.Inventory.Store?.TryGetValue(item.AssetUUID, out InventoryBase? value) != true ||
                value is not InventoryItem target) return null;
            item = target;
        }
        return item;
    }

    private IEnumerable<InventoryItem> RealItems() => _links.Select(Resolve).OfType<InventoryItem>().DistinctBy(i => i.UUID);

    private AttachmentPoint Point(InventoryItem item)
    {
        if (_points.TryGetValue(item.UUID, out var point) && point != AttachmentPoint.Default) return point;
        var prim = _client.Network.CurrentSim?.ObjectsPrimitives.Values.FirstOrDefault(p =>
            p.ParentID == _client.Self.LocalID && CurrentOutfitFolder.GetAttachmentItemID(p) == item.UUID);
        if (prim != null && prim.PrimData.AttachmentPoint != AttachmentPoint.Default) return prim.PrimData.AttachmentPoint;
        return item switch
        {
            InventoryAttachment attachment => attachment.AttachmentPoint,
            InventoryObject obj => obj.AttachPoint,
            _ => AttachmentPoint.Default
        };
    }

    private bool IsRezzed(UUID id) => _client.Network.CurrentSim?.ObjectsPrimitives.Values.Any(p =>
        p.ParentID == _client.Self.LocalID && CurrentOutfitFolder.GetAttachmentItemID(p) == id) == true;

    public Task AttachAsync(InventoryItem item, AttachmentPoint point, bool replace, CancellationToken token) =>
        AddAsync(item, point, replace, token);
    public Task AddToOutfitAsync(InventoryItem item, bool replace, CancellationToken token) => AddAsync(item, null, replace, token);
    public Task DetachAsync(InventoryItem item, CancellationToken token) => RemoveFromOutfitAsync(item, token);

    private async Task AddAsync(InventoryItem requested, AttachmentPoint? attachmentPoint, bool replace, CancellationToken token)
    {
        using var session = SessionToken(token);
        token = session.Token;
        using var batch = BeginBatch();
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(token).ConfigureAwait(false);
            var item = Resolve(requested) ?? throw new InvalidOperationException("The inventory link target is unavailable.");
            if (!_policy.CanAttach(item)) throw new InvalidOperationException("Adding this item is restricted by RLV.");
            var isAttachment = item is InventoryObject or InventoryAttachment;
            var point = attachmentPoint is { } explicitPoint && explicitPoint != AttachmentPoint.Default ? explicitPoint : Point(item);
            var current = RealItems().ToList();
            var removals = current.Where(old => old.UUID != item.UUID &&
                (item is InventoryWearable wearable && old is InventoryWearable worn && wearable.WearableType == worn.WearableType &&
                 (replace || item.AssetType == AssetType.Bodypart || wearable.WearableType == WearableType.Physics) ||
                 isAttachment && replace && point != AttachmentPoint.Default && old is InventoryObject or InventoryAttachment && Point(old) == point)).ToList();
            if (removals.Any(old => !_policy.CanDetach(old)))
                throw new InvalidOperationException("An item this would replace is locked by RLV.");
            if (item is InventoryWearable && item.AssetType != AssetType.Bodypart && !current.Any(i => i.UUID == item.UUID) &&
                current.Count(i => i is InventoryWearable && i.AssetType != AssetType.Bodypart) - removals.Count >= 60)
                throw new InvalidOperationException("The clothing layer limit has been reached.");
            if (isAttachment && !current.Any(i => i.UUID == item.UUID) && current.Count(i => i is InventoryObject or InventoryAttachment) - removals.Count >= 38)
                throw new InvalidOperationException("The attachment limit has been reached.");

            await RemoveLinksAsync(removals.Select(i => i.UUID).ToHashSet(), token).ConfigureAwait(false);
            var alreadyLinked = _links.Any(l => l.ResolvedItemID == item.UUID);
            if (!alreadyLinked)
            {
                var description = item is InventoryWearable w && item.AssetType != AssetType.Bodypart
                    ? await LayerDescriptionAsync(w, token).ConfigureAwait(false) : "";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var created = await _client.Inventory.CreateLinkAsync(_cof!.UUID, item.UUID, item.Name, description,
                    item.InventoryType, UUID.Random(), timeout.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (created == null || created.ResolvedItemID != item.UUID)
                    throw new InvalidOperationException("The server did not confirm the Current Outfit link.");
                _store!.UpdateNodeFor(created);
                InventoryItem[] confirmed = [created];
                _links = _links.Concat(confirmed).DistinctBy(l => l.UUID).ToArray();
            }
            token.ThrowIfCancellationRequested();
            if (isAttachment)
            {
                // This also repairs a linked item which has not rezzed yet.
                if (!alreadyLinked || !IsRezzed(item.UUID) || Point(item) != point)
                    _client.Appearance.Attach(item, point, false);
                _points[item.UUID] = point;
            }
            else if (item is InventoryGesture gesture) _client.Self.ActivateGesture(item.UUID, gesture.AssetUUID);
            else if (_clientSideBaking) _client.Appearance.AddToOutfit(item, replace);
            await _policy.ReportItemChangeAsync([item], [], token).ConfigureAwait(false);
            OutfitChanged();
        }
        catch
        {
            // A partial server mutation is still authoritative. Never replay a
            // saved outfit as a rollback; reconcile on the next operation.
            if (_cof != null && _store != null)
            {
                _store.GetNodeFor(_cof.UUID).NeedsUpdate = true;
                _ready = false;
            }
            throw;
        }
        finally { _changes.Release(); }
    }

    public async Task RemoveFromOutfitAsync(InventoryItem requested, CancellationToken token)
    {
        using var session = SessionToken(token);
        token = session.Token;
        using var batch = BeginBatch();
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(token).ConfigureAwait(false);
            var item = Resolve(requested) ?? throw new InvalidOperationException("The inventory link target is unavailable.");
            if (item.AssetType == AssetType.Bodypart) return;
            if (!_policy.CanDetach(item)) throw new InvalidOperationException("Removing this item is restricted by RLV.");
            var linked = _links.Any(l => l.ResolvedItemID == item.UUID);
            await RemoveLinksAsync([item.UUID], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!linked)
            {
                RemovePhysical(item);
                await _policy.ReportItemChangeAsync([], [item], token).ConfigureAwait(false);
            }
            OutfitChanged();
        }
        finally { _changes.Release(); }
    }

    private async Task<string> LayerDescriptionAsync(InventoryWearable wearable, CancellationToken token)
    {
        var prefix = ((int)wearable.WearableType).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var layers = _links.Where(link => Resolve(link) is InventoryWearable other && other.WearableType == wearable.WearableType)
            .Select((link, index) => (Link: link, Order: link.Description?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                int.TryParse(link.Description[prefix.Length..], out var order) ? order : index))
            .OrderBy(layer => layer.Order).ToArray();
        var next = layers.Length == 0 ? 0 : layers.Max(layer => layer.Order) + 1;
        if (next >= 60)
        {
            // Removed layers leave gaps. Compact only when the next ordering
            // number would exceed the layer range, preserving the remaining
            // layers' relative order and avoiding duplicate descriptions.
            for (var i = 0; i < layers.Length; i++)
            {
                var description = $"{prefix}{i:00}";
                if (_client.AisClient.IsAvailable)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    if (!await _client.AisClient.UpdateItemAsync(layers[i].Link.UUID,
                        new LibreMetaverse.StructuredData.OSDMap { ["desc"] = description }, timeout.Token).ConfigureAwait(false))
                        throw new InvalidOperationException("The server did not confirm clothing layer ordering.");
                }
                layers[i].Link.Description = description;
                if (!_client.AisClient.IsAvailable) _client.Inventory.RequestUpdateItem(layers[i].Link);
            }
            // UpdateItemAsync does not apply AIS category versions; fetch the
            // acknowledged version rather than estimating an increment.
            await FetchCofAsync(token, true).ConfigureAwait(false);
            next = layers.Length;
        }
        return $"{prefix}{next:00}";
    }

    private async Task RemoveLinksAsync(HashSet<UUID> targets, CancellationToken token)
    {
        var links = _links.Where(l => targets.Contains(l.ResolvedItemID)).ToArray();
        // InventoryManager.RemoveItemsAsync swallows AIS failures. Use the AIS
        // acknowledgements directly so a failed removal cannot masquerade as a
        // successful outfit change.
        foreach (var link in links)
        {
            if (_client.AisClient.IsAvailable)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (!await _client.AisClient.RemoveItemAsync(link.UUID, timeout.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("The server did not confirm removing the Current Outfit link.");
                token.ThrowIfCancellationRequested();
                _store!.RemoveNodeFor(link);
            }
            else await _client.Inventory.RemoveItemsAsync([link.UUID], token).ConfigureAwait(false);
            _links = _links.Where(l => l.UUID != link.UUID).ToArray();
            lock (_state) _bakeDirty = true;
            // Apply each confirmed difference immediately. If a later removal
            // fails, earlier successes must not leave untracked objects worn.
            if (!_links.Any(l => l.ResolvedItemID == link.ResolvedItemID) && Resolve(link) is { } removed)
            {
                RemovePhysical(removed);
                await _policy.ReportItemChangeAsync([], [removed], token).ConfigureAwait(false);
            }
        }
    }

    private void RemovePhysical(InventoryItem item)
    {
        if (item is InventoryObject or InventoryAttachment) _client.Appearance.Detach(item);
        else if (item is InventoryGesture) _client.Self.DeactivateGesture(item.UUID);
        else if (_clientSideBaking) _client.Appearance.RemoveFromOutfit([item]);
        _points.TryRemove(item.UUID, out _);
    }

    private void OutfitChanged()
    {
        lock (_state) { _bakeDirty = true; ScheduleBakeLocked(); }
        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_state)
        {
            if (_disposed) return;
            _session.Cancel();
            _session.Dispose();
            _session = new();
            CancelBakeLocked();
            _bakeDirty = false;
            _ready = false;
            _store = null;
            _restoredStore = null;
            _links = [];
            _points.Clear();
            _lastBakedVersion = -1;
            _clientSideBaking = false;
            if (_watchedCaps != null) _watchedCaps.CapabilitiesReceived -= OnCapabilitiesReceived;
            _watchedCaps = null;
            _startingSim = null;
            _regionStart = null;
            _client.Settings.Agent.SendAppearance = false;
        }
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e) => Reset();

    private void OnRebake(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Simulator != _client.Network.CurrentSim) return;
        if (_clientSideBaking) { _ = StartClientBakeAsync(); return; }
        lock (_state) { _lastBakedVersion = -1; _bakeDirty = true; ScheduleBakeLocked(); }
    }

    private void OnSimChanged(object? sender, SimChangedEventArgs e)
    {
        lock (_state)
        {
            if (_disposed) return;
            CancelBakeLocked();
            if (_watchedCaps != null) _watchedCaps.CapabilitiesReceived -= OnCapabilitiesReceived;
            _watchedCaps = _client.Network.CurrentSim?.Caps;
            if (_watchedCaps != null) _watchedCaps.CapabilitiesReceived += OnCapabilitiesReceived;
            _startingSim = null;
            _regionStart = null;
            _clientSideBaking = false;
            _client.Settings.Agent.SendAppearance = false;
            _lastBakedVersion = -1;
        }
        StartRegion(_client.Network.CurrentSim);
    }

    private void OnCapabilitiesReceived(object? sender, CapabilitiesReceivedEventArgs e) => StartRegion(e.Simulator);

    private void StartRegion(Simulator? sim)
    {
        if (sim == null || sim != _client.Network.CurrentSim) return;
        lock (_state)
        {
            if (_disposed || _startingSim == sim && _regionStart?.IsCompleted == false) return;
            _startingSim = sim;
            var token = _session.Token;
            _regionStart = Task.Run(() => StartRegionAsync(sim, token));
        }
    }

    private async Task StartClientBakeAsync()
    {
        try { await _client.Appearance.RequestSetAppearance(true).ConfigureAwait(false); }
        catch (Exception ex) { Logger.Warn($"Client appearance update failed: {ex.Message}", _client); }
    }

    private async Task StartRegionAsync(Simulator? sim, CancellationToken token)
    {
        if (sim == null) return;
        try
        {
            using var session = SessionToken(token);
            await _inventory.WaitForCapabilityAsync("FetchInventoryDescendents2", session.Token).ConfigureAwait(false);
            if (sim != _client.Network.CurrentSim) return;
            if ((sim.Protocols & RegionProtocols.AgentAppearanceService) == 0 && sim.Caps?.CapabilityURI("UpdateAvatarAppearance") == null)
            {
                _clientSideBaking = true;
                await StartClientBakeAsync().ConfigureAwait(false);
                return;
            }
            await GetCurrentOutfitLinksAsync(session.Token).ConfigureAwait(false);
            lock (_state) { _bakeDirty = true; ScheduleBakeLocked(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn($"Appearance startup failed: {ex.Message}", _client); }
    }

    public void Dispose()
    {
        Reset();
        lock (_state) { _disposed = true; _session.Cancel(); _session.Dispose(); }
        _client.Network.SimChanged -= OnSimChanged;
        _client.Network.Disconnected -= OnDisconnected;
        _client.Network.UnregisterCallback(PacketType.RebakeAvatarTextures, OnRebake);
        if (_ownsInventory) _inventory.Dispose();
    }
}
