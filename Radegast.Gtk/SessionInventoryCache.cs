using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Saves only after inventory changes, with one serialized writer and no idle polling.</summary>
internal sealed class SessionInventoryCache : IDisposable
{
    private readonly GridClient _client;
    private readonly InventoryDiskCache _disk;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private LibreMetaverse.Inventory? _store;
    private SavedLogin? _identity;
    private ITimer? _timer;
    private Task _writes = Task.CompletedTask;
    private DateTimeOffset _nextSave;
    private bool _dirty, _disposed;
    public bool HasSession { get { lock (_gate) return _store != null; } }

    public SessionInventoryCache(GridClient client, InventoryDiskCache disk, TimeProvider clock)
    {
        _client = client; _disk = disk; _clock = clock;
    }

    public int Start(SavedLogin identity)
    {
        Stop();
        var store = _client.Inventory.Store;
        if (store?.RootFolder == null || store.Owner == UUID.Zero) return 0;
        // Restore before appearances and RLV start requesting inventory. Folder
        // names, parents and versions always come from this login's skeleton.
        var restored = _disk.Restore(identity, store);
        lock (_gate)
        {
            if (_disposed) return restored;
            _identity = identity; _store = store;
            _nextSave = DateTimeOffset.MinValue;
            store.InventoryObjectAdded += OnAdded;
            store.InventoryObjectUpdated += OnUpdated;
            store.InventoryObjectRemoved += OnRemoved;
            _client.Inventory.FolderUpdated += OnFolder;
        }
        return restored;
    }

    private void OnAdded(object? sender, InventoryObjectAddedEventArgs e) => Changed();
    private void OnUpdated(object? sender, InventoryObjectUpdatedEventArgs e) => Changed();
    private void OnRemoved(object? sender, InventoryObjectRemovedEventArgs e) => Changed();
    private void OnFolder(object? sender, FolderUpdatedEventArgs e)
    {
        if (e.Success) Changed();
        else
        {
            // The SDK clears this flag before parsing. Preserve retryability if
            // parsing then fails, including requests started by SDK consumers.
            lock (_gate)
                if (_store != null && _store == _client.Inventory.Store && _store.TryGetNodeFor(e.FolderID, out var node)) node.NeedsUpdate = true;
        }
    }

    private void Changed()
    {
        lock (_gate)
        {
            if (_disposed || _store == null) return;
            _dirty = true;
            if (_timer != null) return;
            var due = _nextSave - _clock.GetUtcNow();
            if (due < TimeSpan.FromSeconds(5)) due = TimeSpan.FromSeconds(5);
            _timer = _clock.CreateTimer(_ => { lock (_gate) { _timer?.Dispose(); _timer = null; QueueSave(); } },
                null, due, Timeout.InfiniteTimeSpan);
        }
    }

    private void QueueSave()
    {
        if (!_dirty || _store == null || _identity == null) return;
        var store = _store;
        var identity = _identity;
        _dirty = false;
        _nextSave = _clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        // Capture on the worker, not on the GTK thread. Queueing writes in order
        // prevents a logout snapshot from being overwritten by an older save.
        _writes = _writes.ContinueWith(async _ =>
        {
            try { await _disk.SaveAsync(identity, _disk.Capture(identity, store)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Debug("Inventory cache could not be saved: " + ex.Message, _client); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            QueueSave();
            return _writes;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            if (_store == null) return;
            _store.InventoryObjectAdded -= OnAdded;
            _store.InventoryObjectUpdated -= OnUpdated;
            _store.InventoryObjectRemoved -= OnRemoved;
            _client.Inventory.FolderUpdated -= OnFolder;
            QueueSave();
            _store = null; _identity = null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }
}
