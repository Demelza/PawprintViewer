using Gtk;

namespace Radegast.Gtk;

internal sealed class AccountSettingsPanel : Box
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
    private bool _refreshing, _stopped;

    public AccountSettingsPanel(AccountSession session) : base(Orientation.Vertical, 12)
    {
        _session = session;
        BorderWidth = 12;
        PackStart(_rlvEnabled, false, false, 0);
        PackStart(_debug, false, false, 0);
        PackStart(new Label("Turning RLV off clears this account's restrictions. Objects must resend them when it is turned on again.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        PackStart(_reconnect, false, false, 0);
        var delayRow = new Box(Orientation.Horizontal, 8);
        delayRow.PackStart(new Label("Reconnect delay (seconds)") { Xalign = 0 }, false, false, 0);
        delayRow.PackStart(_delay, false, false, 0);
        PackStart(delayRow, false, false, 0);
        PackStart(_testDisconnect, false, false, 0);
        PackStart(new Label("Reconnect preferences are saved for this account and grid. Logging out or closing the viewer cancels reconnects.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        PackStart(_status, false, false, 0);
        _rlvEnabled.Toggled += (_, _) => { if (!_refreshing) session.Rlv.SetEnabled(_rlvEnabled.Active); };
        _debug.Toggled += (_, _) => { if (!_refreshing) session.Rlv.DebugCommands = _debug.Active; };
        _reconnect.Toggled += (_, _) => Save();
        _delay.ValueChanged += (_, _) => Save();
        _testDisconnect.Clicked += (_, _) => _ = DisconnectForTestAsync();
        session.Rlv.Changed += Refresh;
        session.SettingsChanged += OnSettingsChanged;
        session.StateChanged += OnStateChanged;
        Destroyed += (_, _) => Stop();
        Refresh();
    }

    private void Save()
    {
        if (_refreshing || _stopped) return;
        _session.UpdateSettings(_session.Settings with { AutoReconnect = _reconnect.Active, ReconnectDelaySeconds = _delay.ValueAsInt });
    }

    private void OnSettingsChanged(AccountSession account) => Refresh();
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
        _status.Text = _session.SettingsError ?? "Reconnect changes apply immediately and are saved automatically.";
        _refreshing = false;
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _session.Rlv.Changed -= Refresh;
        _session.SettingsChanged -= OnSettingsChanged;
        _session.StateChanged -= OnStateChanged;
    }
}
