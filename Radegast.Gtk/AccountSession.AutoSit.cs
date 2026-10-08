using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private static readonly TimeSpan AutoSitDelay = TimeSpan.FromMinutes(1);
    private ITimer? _autoSitTimer;
    private CancellationTokenSource? _autoSitCancel;
    private DateTimeOffset? _autoSitDue;
    private int _autoSitGeneration;
    private string _autoSitStatus = "";

    public string AutoSitStatus => RedactText(_autoSitStatus);
    public event Action<AccountSession>? AutoSitChanged;

    private bool TryAutoSitTarget(out UUID target) => UUID.TryParse(Settings.AutoSitObjectId.Trim(), out target) && target != UUID.Zero;

    private void SetAutoSitStatus(string text)
    {
        if (_disposed || _autoSitStatus == text) return;
        _autoSitStatus = text;
        AutoSitChanged?.Invoke(this);
    }

    private void CancelAutoSit()
    {
        _autoSitGeneration++;
        _autoSitTimer?.Dispose();
        _autoSitTimer = null;
        _autoSitCancel?.Cancel();
        _autoSitCancel?.Dispose();
        _autoSitCancel = null;
    }

    private void ResetAutoSit()
    {
        _autoSitDue = null;
        UpdateAutoSit();
    }

    private void AutoSitOnLogin()
    {
        if (_autoSitDue != null) return; // Duplicate success events keep the original deadline.
        _autoSitDue = _clock.GetUtcNow() + AutoSitDelay;
        UpdateAutoSit();
    }

    private void UpdateAutoSit()
    {
        CancelAutoSit();
        if (_disposed) return;
        if (!Settings.AutoSit) { SetAutoSitStatus(""); return; }
        if (!TryAutoSitTarget(out _)) { SetAutoSitStatus("Enter a valid furniture UUID."); return; }
        var now = _clock.GetUtcNow();
        if (!IsConnected || _autoSitDue is not { } due || due <= now)
        {
            SetAutoSitStatus("Auto Sit will run after the next login.");
            return;
        }
        var generation = _autoSitGeneration;
        _autoSitTimer = _clock.CreateTimer(_ => _post(() => BeginAutoSit(generation)), null,
            due - now, Timeout.InfiniteTimeSpan);
        SetAutoSitStatus("Auto Sit scheduled for one minute after login.");
    }

    private void BeginAutoSit(int generation)
    {
        if (_disposed || generation != _autoSitGeneration || !Settings.AutoSit || !IsConnected || !TryAutoSitTarget(out var target)) return;
        _autoSitTimer?.Dispose();
        _autoSitTimer = null;
        if (IsSitting) { SetAutoSitStatus("Already seated; Auto Sit skipped."); return; }
        var cancel = new CancellationTokenSource();
        _autoSitCancel = cancel;
        SetAutoSitStatus("Auto Sit is waiting for the furniture to confirm.");
        _ = CompleteAutoSitAsync(target, generation, cancel);
    }

    private async Task CompleteAutoSitAsync(UUID target, int generation, CancellationTokenSource cancel)
    {
        try
        {
            await SitOnFurnitureAsync(target, cancel.Token).ConfigureAwait(false);
            _post(() => { if (!_disposed && generation == _autoSitGeneration) SetAutoSitStatus("Auto Sit completed."); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _post(() =>
            {
                if (_disposed || generation != _autoSitGeneration) return;
                SetAutoSitStatus("Auto Sit failed: " + ex.Message);
                ChatLine?.Invoke(this, $"[{DateTime.Now:HH:mm}] {AutoSitStatus}");
            });
        }
        finally
        {
            _post(() =>
            {
                if (ReferenceEquals(_autoSitCancel, cancel)) _autoSitCancel = null;
                cancel.Dispose();
            });
        }
    }
}
