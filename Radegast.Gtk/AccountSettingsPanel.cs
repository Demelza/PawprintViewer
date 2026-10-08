using Gtk;

namespace Radegast.Gtk;

internal sealed class AccountSettingsPanel : ScrolledWindow
{
    private readonly AccountSession _session;
    private readonly CheckButton _rlvEnabled = new("Enable RLV / RLVa for this account");
    private readonly CheckButton _debug = new("Show RLV commands and replies in Nearby Chat");
    private readonly CheckButton _autoSit = new("Auto sit after login");
    private readonly CheckButton _autoSitRestart = new("Auto sit after return from region restart");
    private readonly Entry _autoSitObject = new() { PlaceholderText = "Furniture UUID", MaxLength = 36 };
    private readonly Label _autoSitStatus = new("") { Xalign = 0, LineWrap = true, NoShowAll = true };
    private readonly CheckButton _reconnect = new("Automatically reconnect after a disconnection");
    private readonly SpinButton _delay = new(1, AccountSettings.MaximumDelaySeconds, 1)
        { Numeric = true, Digits = 0, WidthChars = 7 };
    private readonly Label _status = new("") { Xalign = 0, LineWrap = true };
    private readonly CheckButton _restartTeleport = new("Teleport on region restart");
    private readonly Entry _restartRegion = new() { PlaceholderText = "Temporary destination region", MaxLength = 255 };
    private readonly SpinButton _restartX = CoordinateField();
    private readonly SpinButton _restartY = CoordinateField();
    private readonly SpinButton _restartZ = CoordinateField();
    private readonly SpinButton _returnDelay = new(1, AccountSettings.MaximumReturnDelayMinutes, 1)
        { Numeric = true, Digits = 0, WidthChars = 7 };
    private bool _refreshing, _stopped;

    private static SpinButton CoordinateField() => new(0, 65535, 1) { Numeric = true, Digits = 1, WidthChars = 7 };

    public AccountSettingsPanel(AccountSession session)
    {
        _session = session;
        SetPolicy(PolicyType.Never, PolicyType.Automatic);
        var content = new Box(Orientation.Vertical, 8) { BorderWidth = 12 };
        Add(content);
        content.PackStart(_rlvEnabled, false, false, 0);
        content.PackStart(_debug, false, false, 0);
        content.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        content.PackStart(_autoSit, false, false, 0);
        content.PackStart(_autoSitRestart, false, false, 0);
        var furnitureRow = new Box(Orientation.Horizontal, 8);
        furnitureRow.PackStart(new Label("Furniture UUID") { Xalign = 0 }, false, false, 0);
        furnitureRow.PackStart(_autoSitObject, true, true, 0);
        content.PackStart(furnitureRow, false, false, 0);
        content.PackStart(_autoSitStatus, false, false, 0);
        content.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        content.PackStart(_reconnect, false, false, 0);
        var delayRow = new Box(Orientation.Horizontal, 8);
        delayRow.PackStart(new Label("Reconnect delay (seconds)") { Xalign = 0 }, false, false, 0);
        delayRow.PackStart(_delay, false, false, 0);
        content.PackStart(delayRow, false, false, 0);
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
        content.PackStart(_status, false, false, 0);
        _rlvEnabled.Toggled += (_, _) => { if (!_refreshing) session.Rlv.SetEnabled(_rlvEnabled.Active); };
        _debug.Toggled += (_, _) => { if (!_refreshing) session.Rlv.DebugCommands = _debug.Active; };
        _autoSit.Toggled += (_, _) => Save();
        _autoSitRestart.Toggled += (_, _) => Save();
        _autoSitObject.Changed += (_, _) => Save();
        _reconnect.Toggled += (_, _) => Save();
        _delay.ValueChanged += (_, _) => Save();
        _restartTeleport.Toggled += (_, _) => Save();
        _restartRegion.Changed += (_, _) => Save();
        _restartX.ValueChanged += (_, _) => Save();
        _restartY.ValueChanged += (_, _) => Save();
        _restartZ.ValueChanged += (_, _) => Save();
        _returnDelay.ValueChanged += (_, _) => Save();
        session.Rlv.Changed += Refresh;
        session.SettingsChanged += OnSettingsChanged;
        session.AutoSitChanged += OnAutoSitChanged;
        Destroyed += (_, _) => Stop();
        Refresh();
    }

    private void Save()
    {
        if (_refreshing || _stopped) return;
        _session.UpdateSettings(_session.Settings with
        {
            AutoSit = _autoSit.Active, AutoSitOnRestartReturn = _autoSitRestart.Active,
            AutoSitObjectId = _autoSitObject.Text.Trim(),
            AutoReconnect = _reconnect.Active, ReconnectDelaySeconds = _delay.ValueAsInt,
            TeleportOnRegionRestart = _restartTeleport.Active, RestartDestinationRegion = _restartRegion.Text,
            RestartDestinationX = (float)_restartX.Value, RestartDestinationY = (float)_restartY.Value,
            RestartDestinationZ = (float)_restartZ.Value, ReturnDelayMinutes = _returnDelay.ValueAsInt
        });
    }

    private void OnSettingsChanged(AccountSession account) => Refresh();
    private void OnAutoSitChanged(AccountSession account)
    {
        if (_stopped) return;
        _autoSitStatus.Text = account.AutoSitStatus;
        _autoSitStatus.Visible = !string.IsNullOrWhiteSpace(_autoSitStatus.Text);
    }
    private void Refresh()
    {
        if (_stopped) return;
        _refreshing = true;
        _rlvEnabled.Active = _session.Rlv.Enabled;
        _debug.Active = _session.Rlv.DebugCommands;
        _autoSit.Active = _session.Settings.AutoSit;
        _autoSitRestart.Active = _session.Settings.AutoSitOnRestartReturn;
        if (_autoSitObject.Text != _session.Settings.AutoSitObjectId) _autoSitObject.Text = _session.Settings.AutoSitObjectId;
        _autoSitObject.Sensitive = _autoSit.Active || _autoSitRestart.Active;
        OnAutoSitChanged(_session);
        _reconnect.Active = _session.Settings.AutoReconnect;
        _delay.Value = _session.Settings.ReconnectDelaySeconds;
        _delay.Sensitive = _reconnect.Active;
        _restartTeleport.Active = _session.Settings.TeleportOnRegionRestart;
        if (_restartRegion.Text != _session.Settings.RestartDestinationRegion) _restartRegion.Text = _session.Settings.RestartDestinationRegion;
        _restartX.Value = _session.Settings.RestartDestinationX;
        _restartY.Value = _session.Settings.RestartDestinationY;
        _restartZ.Value = _session.Settings.RestartDestinationZ;
        _returnDelay.Value = _session.Settings.ReturnDelayMinutes;
        foreach (var widget in new Widget[] { _restartRegion, _restartX, _restartY, _restartZ, _returnDelay })
            widget.Sensitive = _restartTeleport.Active;
        _status.Text = _session.SettingsError ?? "Account settings apply immediately and are saved automatically.";
        _refreshing = false;
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _session.Rlv.Changed -= Refresh;
        _session.SettingsChanged -= OnSettingsChanged;
        _session.AutoSitChanged -= OnAutoSitChanged;
    }
}
