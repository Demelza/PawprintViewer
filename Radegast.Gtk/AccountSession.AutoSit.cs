using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private static readonly TimeSpan AutoSitDelay = TimeSpan.FromMinutes(1);
    private ITimer? _autoSitTimer;
    private CancellationTokenSource? _autoSitCancel;
    private DateTimeOffset? _autoSitDue;
    private DateTimeOffset? _restartAutoSitDue;
    private ulong _restartAutoSitRegion;
    private int _autoSitGeneration;
    private string _autoSitStatus = "";

    public string AutoSitStatus => RedactText(_autoSitStatus);
    public event Action<AccountSession>? AutoSitChanged;

    private bool TryAutoSitTarget(out UUID target) => UUID.TryParse(Settings.AutoSitObjectId.Trim(), out target) && target != UUID.Zero;

    private bool AutoSitEnabled(AccountSettings settings) => _restartAutoSitDue != null
        ? settings.AutoSitOnRestartReturn && settings.TeleportOnRegionRestart : settings.AutoSit;

    private bool AtAutoSitRegion => _restartAutoSitDue == null || Client.Network.CurrentSim?.Handle == _restartAutoSitRegion;

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
        _restartAutoSitDue = null;
        _restartAutoSitRegion = 0;
        UpdateAutoSit();
    }

    private void AutoSitOnLogin()
    {
        if (_autoSitDue != null) return; // Duplicate success events keep the original deadline.
        _autoSitDue = _clock.GetUtcNow() + AutoSitDelay;
        UpdateAutoSit();
    }

    private void AutoSitOnRestartReturn(RestartLocation origin)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim?.Handle != origin.Region.RegionHandle) return;
        _restartAutoSitRegion = origin.Region.RegionHandle;
        _restartAutoSitDue = _clock.GetUtcNow() + AutoSitDelay;
        UpdateAutoSit();
    }

    private void AutoSitLocationChanged()
    {
        if (AtAutoSitRegion || (_autoSitTimer == null && _autoSitCancel == null)) return;
        CancelAutoSit();
        SetAutoSitStatus("Auto Sit cancelled: the avatar left the return region.");
    }

    private void UpdateAutoSitSettings(AccountSettings previous)
    {
        // Changing the other trigger's checkbox must not interrupt a sit that
        // is already scheduled or waiting for the server's reply.
        if (previous.AutoSitObjectId == Settings.AutoSitObjectId &&
            AutoSitEnabled(previous) == AutoSitEnabled(Settings) &&
            (_autoSitTimer != null || _autoSitCancel != null)) return;
        UpdateAutoSit();
    }

    private void UpdateAutoSit()
    {
        CancelAutoSit();
        if (_disposed) return;
        if (!Settings.AutoSit && !Settings.AutoSitOnRestartReturn) { SetAutoSitStatus(""); return; }
        if (!TryAutoSitTarget(out _)) { SetAutoSitStatus("Enter a valid furniture UUID."); return; }
        var now = _clock.GetUtcNow();
        if (!IsConnected || !AutoSitEnabled(Settings) || !AtAutoSitRegion ||
            (_restartAutoSitDue ?? _autoSitDue) is not { } due || due <= now)
        {
            SetAutoSitStatus(Settings.AutoSit ? "Auto Sit will run after the next login" +
                (Settings.AutoSitOnRestartReturn ? " or return from a region restart." : ".")
                : "Auto Sit will run after returning from a region restart.");
            return;
        }
        var generation = _autoSitGeneration;
        _autoSitTimer = _clock.CreateTimer(_ => _post(() => BeginAutoSit(generation)), null,
            due - now, Timeout.InfiniteTimeSpan);
        SetAutoSitStatus(_restartAutoSitDue != null ? "Auto Sit scheduled for one minute after the restart return."
            : "Auto Sit scheduled for one minute after login.");
    }

    private void BeginAutoSit(int generation)
    {
        if (_disposed || generation != _autoSitGeneration || !AutoSitEnabled(Settings) || !IsConnected ||
            !AtAutoSitRegion || !TryAutoSitTarget(out var target)) return;
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
