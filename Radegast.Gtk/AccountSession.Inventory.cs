using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private readonly SessionInventoryCache _inventoryCache;
    private CancellationTokenSource? _inventoryWarmupCancel;
    private LibreMetaverse.Inventory? _inventoryWarmupStore;
    public InventoryLoader InventoryLoading { get; }

    private void RestoreInventoryCache()
    {
        ResetInventoryLoading();
        if (Net.LoginOptions.Grid == null) return;
        var restored = _inventoryCache.Start(new SavedLogin(Net.LoginOptions.FullName, Net.LoginOptions.Grid.LoginURI));
        if (restored > 0) Logger.Info($"Restored {restored} inventory item(s) from the local cache.", Client);
    }

    private void BeginInventoryWarmup()
    {
        var store = Client.Inventory.Store;
        if (!_inventoryCache.HasSession || store == null || _inventoryWarmupStore == store) return;
        _inventoryWarmupStore = store;
        _inventoryWarmupCancel = new();
        _inventoryWarmupCancel.CancelAfter(TimeSpan.FromSeconds(45));
        var cancel = _inventoryWarmupCancel;
        var token = cancel.Token;
        _ = Task.Run(async () =>
        {
            try { await Rlv.WarmInventoryAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Debug("Inventory startup prefetch: " + ex.Message, Client); }
            finally
            {
                _post(() =>
                {
                    if (ReferenceEquals(_inventoryWarmupCancel, cancel)) _inventoryWarmupCancel = null;
                    cancel.Dispose();
                });
            }
        });
    }

    private void ResetInventoryLoading()
    {
        _inventoryWarmupCancel?.Cancel();
        _inventoryWarmupCancel?.Dispose();
        _inventoryWarmupCancel = null;
        _inventoryWarmupStore = null;
        Outfit.Reset();
        InventoryLoading.Reset();
        _inventoryCache.Stop();
    }
}
