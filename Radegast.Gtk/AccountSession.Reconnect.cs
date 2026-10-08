using LibreMetaverse;
using Radegast;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private readonly AccountSettingsStore _settingsStore;
    private readonly TimeProvider _clock;
    private SavedLogin? _loginIdentity;
    private ITimer? _reconnectTimer;
    private int _reconnectGeneration;
    private bool _hadSuccessfulLogin, _reconnectNeeded, _reconnecting, _reconnectBlocked;
    private bool _testDisconnecting;

    public AccountSettings Settings { get; private set; } = new();
    public string? SettingsError { get; private set; }
    public event Action<AccountSession>? SettingsChanged;
    public bool CanTestDisconnect => !_disposed && IsConnected && !Net.IsLoggingIn && !_testDisconnecting;

    public async Task DisconnectForReconnectTestAsync()
    {
        if (!CanTestDisconnect) return;
        _testDisconnecting = true;
        Status = "Disconnecting to test reconnect…";
        StateChanged?.Invoke(this);
        try
        {
            // Close the actual connections and let the normal disconnected event
            // drive cleanup/reconnect. A timeout reason keeps this distinct from
            // the Logout action, which deliberately cancels automatic reconnect.
            await Task.Run(() => Client.Network.ShutdownAsync(NetworkManager.DisconnectType.NetworkTimeout,
                "Disconnected to test automatic reconnect")).ConfigureAwait(false);
        }
        finally
        {
            _post(() =>
            {
                if (_disposed) return;
                _testDisconnecting = false;
                StateChanged?.Invoke(this);
            });
        }
    }

    private void LoadAccountSettings()
    {
        var identity = new SavedLogin(Net.LoginOptions.FullName, Net.LoginOptions.Grid!.LoginURI);
        if (_loginIdentity?.SecretKey == identity.SecretKey) return;
        _loginIdentity = identity;
        Settings = _settingsStore.Load(identity, out var error);
        SettingsError = error;
        _restartRecovery.UpdateSettings(Settings);
        UpdateAutoSit();
        SettingsChanged?.Invoke(this);
    }

    public void UpdateSettings(AccountSettings settings)
    {
        if (_disposed) return;
        settings.Validate();
        var reconnectChanged = Settings.AutoReconnect != settings.AutoReconnect ||
            Settings.ReconnectDelaySeconds != settings.ReconnectDelaySeconds;
        var autoSitChanged = Settings.AutoSit != settings.AutoSit || Settings.AutoSitObjectId != settings.AutoSitObjectId;
        Settings = settings;
        SettingsError = null;
        _restartRecovery.UpdateSettings(settings);
        if (autoSitChanged) UpdateAutoSit();
        if (reconnectChanged)
        {
            CancelReconnectTimer();
            if (_reconnectNeeded && !_reconnecting)
            {
                if (!settings.AutoReconnect) Status = "Disconnected";
                else ScheduleReconnect();
            }
        }
        try { if (_loginIdentity != null) _settingsStore.Save(_loginIdentity, settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsError = "Applied for this session, but account settings could not be saved.";
        }
        SettingsChanged?.Invoke(this);
        StateChanged?.Invoke(this);
    }

    private void CancelReconnectTimer()
    {
        _reconnectGeneration++;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }

    private void ResetReconnect()
    {
        CancelReconnectTimer();
        _hadSuccessfulLogin = _reconnectNeeded = _reconnecting = _reconnectBlocked = false;
    }

    private void ReconnectOnDisconnect(bool unexpected)
    {
        if (!unexpected) { ResetReconnect(); return; }
        if (!_hadSuccessfulLogin) return;
        _reconnectNeeded = true;
        // A failed retry can report a disconnect before its final login status.
        // The login callback schedules the next attempt in that case.
        if (!_reconnecting) ScheduleReconnect();
    }

    private void ReconnectOnLoginProgress(LoginStatus status, string message, string reason)
    {
        if (status == LoginStatus.Success)
        {
            CancelReconnectTimer();
            _hadSuccessfulLogin = true;
            _reconnectNeeded = _reconnecting = _reconnectBlocked = false;
            Net.LoginOptions.MfaToken = string.Empty;
        }
        else if (status == LoginStatus.Failed && _reconnecting)
        {
            _reconnecting = false;
            // These responses require a fresh user login rather than another
            // unattended attempt using the same credentials or expired OTP.
            if (reason.ToLowerInvariant() is "key" or "mfa_challenge" or "tos" or "update" or "disabled")
            {
                _reconnectBlocked = true;
                CancelReconnectTimer();
                Status = $"Automatic reconnect stopped: {RedactText(message)} Log in again.";
            }
            else ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (_disposed || !Settings.AutoReconnect || !_reconnectNeeded || _reconnectBlocked ||
            _reconnecting || IsConnected) return;
        if (_reconnectTimer == null)
        {
            var generation = _reconnectGeneration;
            _reconnectTimer = _clock.CreateTimer(_ => _post(() => BeginReconnect(generation)), null,
                TimeSpan.FromSeconds(Settings.ReconnectDelaySeconds), Timeout.InfiniteTimeSpan);
        }
        Status = $"Disconnected · reconnect scheduled ({Settings.ReconnectDelaySeconds} s delay)";
        StateChanged?.Invoke(this);
    }

    private void BeginReconnect(int generation)
    {
        if (_disposed || generation != _reconnectGeneration || !Settings.AutoReconnect ||
            !_reconnectNeeded || _reconnectBlocked || _reconnecting || IsConnected) return;
        CancelReconnectTimer();
        if (Net.IsLoggingIn) { ScheduleReconnect(); return; }
        _reconnecting = true;
        Status = "Reconnecting…";
        _friendPresence.Reset();
        _pendingFriendshipOffers.Clear();
        _residentBlockChanges.Clear();
        // Return to the avatar's last position, retaining this account's grid,
        // password and MFA trust hash, but never replaying a one-time code.
        Net.LoginOptions.StartLocation = StartLocationType.Last;
        Net.LoginOptions.MfaToken = string.Empty;
        StateChanged?.Invoke(this);
        try { Net.Login(); }
        catch (Exception ex)
        {
            _reconnecting = false;
            // NetCom sets IsLoggingIn before preparing the SDK request.
            // Clear it if preparation throws, so a later retry can proceed.
            try { Net.CancelLogin(); } catch { }
            Status = $"Reconnect failed: {RedactText(ex.Message)}";
            ScheduleReconnect();
            StateChanged?.Invoke(this);
        }
    }
}
