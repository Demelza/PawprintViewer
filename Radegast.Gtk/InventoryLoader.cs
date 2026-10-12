using System.Diagnostics;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Shares folder requests and batches link targets. Session resets cancel pending downloads.</summary>
internal sealed class InventoryLoader : IDisposable
{
    private readonly GridClient _client;
    private readonly object _gate = new();
    private readonly Dictionary<(LibreMetaverse.Inventory Store, UUID Id), Task> _folders = new();
    private readonly SemaphoreSlim _items = new(1, 1);
    private CancellationTokenSource _session = new();
    private bool _disposed;

    public InventoryLoader(GridClient client) => _client = client;

    public async Task WaitForCapabilityAsync(string capability, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (_client.Inventory.Store?.RootFolder == null || _client.Network.CurrentSim?.Caps?.CapabilityURI(capability) == null)
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                throw new TimeoutException("The inventory service did not become ready in time.");
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    public Task LoadFolderAsync(UUID id, UUID owner, CancellationToken token, bool force = false)
    {
        token.ThrowIfCancellationRequested();
        var store = _client.Inventory.Store ?? throw new InvalidOperationException("Inventory is still initializing.");
        if (!store.TryGetNodeFor(id, out var node)) throw new InvalidOperationException("This inventory folder is unavailable.");
        var key = (store, id);
        Task task;
        TaskCompletionSource? completion = null;
        CancellationToken session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = _session.Token;
            if (!_folders.TryGetValue(key, out task!))
            {
                if (!force && !node.NeedsUpdate) return Task.CompletedTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _folders[key] = task;
            }
        }
        if (completion != null) _ = FetchFolderAsync(store, id, owner, session, completion);
        return task.WaitAsync(token);
    }

    private async Task FetchFolderAsync(LibreMetaverse.Inventory store, UUID id, UUID owner,
        CancellationToken session, TaskCompletionSource completion)
    {
        try
        {
            await WaitForCapabilityAsync(owner == _client.Self.AgentID ? "FetchInventoryDescendents2" : "FetchLibDescendents2", session).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            bool? success = null;
            void Updated(object? _, FolderUpdatedEventArgs e) { if (e.FolderID == id) success = e.Success; }
            _client.Inventory.FolderUpdated += Updated;
            try
            {
                var contents = await _client.Inventory.RequestFolderContentsAsync(id, owner, true, true,
                    InventorySortOrder.ByName, timeout.Token).ConfigureAwait(false);
                session.ThrowIfCancellationRequested();
                timeout.Token.ThrowIfCancellationRequested();
                if (store != _client.Inventory.Store) throw new OperationCanceledException("Inventory changed during loading.");
                var node = store.GetNodeFor(id);
                if (success != true || node.NeedsUpdate || node.Data is not InventoryFolder folder)
                {
                    node.NeedsUpdate = true;
                    throw new InvalidOperationException($"Inventory folder '{node.Data?.Name ?? id.ToString()}' could not be loaded. Try again shortly.");
                }
                var returned = contents.Select(item => item.UUID).ToHashSet();
                foreach (var old in store.GetContents(id).Where(item => !returned.Contains(item.UUID))) store.RemoveNodeFor(old);
                folder.DescendentCount = contents.Count;
                node.NeedsUpdate = false;
            }
            finally { _client.Inventory.FolderUpdated -= Updated; }
            completion.TrySetResult();
        }
        catch (OperationCanceledException ex) { MarkDirty(); completion.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex) { MarkDirty(); completion.TrySetException(ex); }
        finally
        {
            lock (_gate)
                if (_folders.TryGetValue((store, id), out var task) && ReferenceEquals(task, completion.Task)) _folders.Remove((store, id));
        }
        void MarkDirty()
        {
            if (store == _client.Inventory.Store && store.TryGetNodeFor(id, out var node)) node.NeedsUpdate = true;
        }
    }

    public async Task LoadItemsAsync(IEnumerable<UUID> ids, CancellationToken token)
    {
        CancellationToken session;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); session = _session.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session);
        token = linked.Token;
        await _items.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = _client.Inventory.Store ?? throw new InvalidOperationException("Inventory is still initializing.");
            var missing = ids.Distinct().Where(id => !store.TryGetValue(id, out InventoryBase? value) || value is not InventoryItem).ToArray();
            if (missing.Length == 0) return;
            await WaitForCapabilityAsync("FetchInventory2", token).ConfigureAwait(false);
            foreach (var batch in missing.Chunk(50))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await _client.Inventory.RequestFetchInventoryAsync(batch.ToDictionary(id => id, _ => _client.Self.AgentID),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (store != _client.Inventory.Store) throw new OperationCanceledException("Inventory changed during loading.");
            }
        }
        finally { _items.Release(); }
    }

    public void Reset()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed) return;
            previous = _session;
            _session = new();
            _folders.Clear();
        }
        previous.Cancel(); previous.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Cancel(); _session.Dispose();
            _folders.Clear();
        }
    }
}
