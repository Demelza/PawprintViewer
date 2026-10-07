using Gtk;

namespace Radegast.Gtk;

internal sealed class AccountSettingsPanel : ScrolledWindow
{
    private readonly AccountSession _session;
    private readonly CheckButton _rlvEnabled = new("Enable RLV / RLVa for this account");
    private readonly CheckButton _debug = new("Show RLV commands and replies in Nearby Chat");
    private readonly CheckButton _reconnect = new("Automatically reconnect after a disconnection");
    private readonly SpinButton _delay = new(1, AccountSettings.MaximumDelaySeconds, 1)
        { Numeric = true, Digits = 0, WidthChars = 7 };
    private readonly Button _testDisconnect = new("Disconnect to test reconnect")
    {
        Halign = Align.Start,
        TooltipText = "Disconnect this account and keep it in the account list. Automatic reconnect uses the configured delay if enabled."
    };
    private readonly Label _status = new("") { Xalign = 0, LineWrap = true };
    private readonly CheckButton _restartTeleport = new("Teleport on region restart");
    private readonly Entry _restartRegion = new() { PlaceholderText = "Temporary destination region", MaxLength = 255 };
    private readonly SpinButton _restartX = CoordinateField();
    private readonly SpinButton _restartY = CoordinateField();
    private readonly SpinButton _restartZ = CoordinateField();
    private readonly SpinButton _returnDelay = new(1, AccountSettings.MaximumReturnDelayMinutes, 1)
        { Numeric = true, Digits = 0, WidthChars = 7 };
    private readonly Label _restartStatus = new("") { Xalign = 0, LineWrap = true };
    private bool _refreshing, _stopped;

    private static SpinButton CoordinateField() => new(0, 65535, 1) { Numeric = true, Digits = 1, WidthChars = 7 };

    public AccountSettingsPanel(AccountSession session)
    {
        _session = session;
        SetPolicy(PolicyType.Never, PolicyType.Automatic);
        var content = new Box(Orientation.Vertical, 12) { BorderWidth = 12 };
        Add(content);
        content.PackStart(_rlvEnabled, false, false, 0);
        content.PackStart(_debug, false, false, 0);
        content.PackStart(new Label("Turning RLV off clears this account's restrictions. Objects must resend them when it is turned on again.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        content.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        content.PackStart(_reconnect, false, false, 0);
        var delayRow = new Box(Orientation.Horizontal, 8);
        delayRow.PackStart(new Label("Reconnect delay (seconds)") { Xalign = 0 }, false, false, 0);
        delayRow.PackStart(_delay, false, false, 0);
        content.PackStart(delayRow, false, false, 0);
        content.PackStart(_testDisconnect, false, false, 0);
        content.PackStart(new Label("Reconnect preferences are saved for this account and grid. Logging out or closing the viewer cancels reconnects.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        content.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        content.PackStart(_restartTeleport, false, false, 0);
        var regionRow = new Box(Orientation.Horizontal, 8);
        regionRow.PackStart(new Label("Temporary region") { Xalign = 0 }, false, false, 0);
        regionRow.PackStart(_restartRegion, true, true, 0);
        content.PackStart(regionRow, false, false, 0);
        var coordinates = new Box(Orientation.Horizontal, 8);
        foreach (var (label, field) in new[] { ("X", _restartX), ("Y", _restartY), ("Z", _restartZ) })
        {
            coordinates.PackStart(new Label(label), false, false, 0);
            coordinates.PackStart(field, false, false, 0);
        }
        content.PackStart(coordinates, false, false, 0);
        var returnRow = new Box(Orientation.Horizontal, 8);
        returnRow.PackStart(new Label("Return delay (minutes)") { Xalign = 0 }, false, false, 0);
        returnRow.PackStart(_returnDelay, false, false, 0);
        content.PackStart(returnRow, false, false, 0);
        content.PackStart(new Label("Leave when 60 seconds remain on the server's restart countdown, then return to the original position after this delay. " +
            "The delay starts on arrival. Failed returns retry once a minute. Moving to another region, disconnecting, " +
            "or turning this off cancels the return. RLV teleport restrictions apply.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        content.PackStart(_restartStatus, false, false, 0);
        content.PackStart(_status, false, false, 0);
        _rlvEnabled.Toggled += (_, _) => { if (!_refreshing) session.Rlv.SetEnabled(_rlvEnabled.Active); };
        _debug.Toggled += (_, _) => { if (!_refreshing) session.Rlv.DebugCommands = _debug.Active; };
        _reconnect.Toggled += (_, _) => Save();
        _delay.ValueChanged += (_, _) => Save();
        _testDisconnect.Clicked += (_, _) => _ = DisconnectForTestAsync();
        _restartTeleport.Toggled += (_, _) => Save();
        _restartRegion.Changed += (_, _) => Save();
        _restartX.ValueChanged += (_, _) => Save();
        _restartY.ValueChanged += (_, _) => Save();
        _restartZ.ValueChanged += (_, _) => Save();
        _returnDelay.ValueChanged += (_, _) => Save();
        session.Rlv.Changed += Refresh;
        session.SettingsChanged += OnSettingsChanged;
        session.StateChanged += OnStateChanged;
        session.RestartTeleportChanged += OnRestartTeleportChanged;
        Destroyed += (_, _) => Stop();
        Refresh();
    }

    private void Save()
    {
        if (_refreshing || _stopped) return;
        _session.UpdateSettings(_session.Settings with
        {
            AutoReconnect = _reconnect.Active, ReconnectDelaySeconds = _delay.ValueAsInt,
            TeleportOnRegionRestart = _restartTeleport.Active, RestartDestinationRegion = _restartRegion.Text,
            RestartDestinationX = (float)_restartX.Value, RestartDestinationY = (float)_restartY.Value,
            RestartDestinationZ = (float)_restartZ.Value, ReturnDelayMinutes = _returnDelay.ValueAsInt
        });
    }

    private void OnSettingsChanged(AccountSession account) => Refresh();
    private void OnRestartTeleportChanged(AccountSession account)
    {
        if (!_stopped) _restartStatus.Text = account.RestartTeleportStatus;
    }
    private void OnStateChanged(AccountSession account)
    {
        if (!_stopped) _testDisconnect.Sensitive = account.CanTestDisconnect;
    }

    private async Task DisconnectForTestAsync()
    {
        if (_stopped) return;
        try { await _session.DisconnectForReconnectTestAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            GtkDispatch.Post(() =>
            {
                if (!_stopped) _status.Text = $"Could not disconnect: {_session.RedactText(ex.Message)}";
            });
        }
    }

    private void Refresh()
    {
        if (_stopped) return;
        _refreshing = true;
        _rlvEnabled.Active = _session.Rlv.Enabled;
        _debug.Active = _session.Rlv.DebugCommands;
        _reconnect.Active = _session.Settings.AutoReconnect;
        _delay.Value = _session.Settings.ReconnectDelaySeconds;
        _delay.Sensitive = _reconnect.Active;
        _testDisconnect.Sensitive = _session.CanTestDisconnect;
        _restartTeleport.Active = _session.Settings.TeleportOnRegionRestart;
        if (_restartRegion.Text != _session.Settings.RestartDestinationRegion) _restartRegion.Text = _session.Settings.RestartDestinationRegion;
        _restartX.Value = _session.Settings.RestartDestinationX;
        _restartY.Value = _session.Settings.RestartDestinationY;
        _restartZ.Value = _session.Settings.RestartDestinationZ;
        _returnDelay.Value = _session.Settings.ReturnDelayMinutes;
        foreach (var widget in new Widget[] { _restartRegion, _restartX, _restartY, _restartZ, _returnDelay })
            widget.Sensitive = _restartTeleport.Active;
        _restartStatus.Text = _session.RestartTeleportStatus;
        _status.Text = _session.SettingsError ?? "Account settings apply immediately and are saved automatically.";
        _refreshing = false;
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _session.Rlv.Changed -= Refresh;
        _session.SettingsChanged -= OnSettingsChanged;
        _session.StateChanged -= OnStateChanged;
        _session.RestartTeleportChanged -= OnRestartTeleportChanged;
    }
}
