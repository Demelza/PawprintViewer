using Gtk;

namespace Radegast.Gtk;

internal sealed class RlvPanel : Box
{
    private readonly RlvSession _rlv;
    private readonly CheckButton _enabled = new("Enable RLV / RLVa for this account");
    private readonly ListStore _restrictions = new(typeof(string), typeof(string), typeof(string));
    private readonly Label _status = new("") { Xalign = 0, LineWrap = true };

    public RlvPanel(RlvSession rlv) : base(Orientation.Vertical, 8)
    {
        _rlv = rlv;
        BorderWidth = 8;
        _enabled.Active = rlv.Enabled;
        _enabled.Toggled += (_, _) => rlv.SetEnabled(_enabled.Active);
        PackStart(_enabled, false, false, 0);
        var debug = new CheckButton("Show RLV commands and replies in Nearby Chat");
        debug.Toggled += (_, _) => rlv.DebugCommands = debug.Active;
        PackStart(debug, false, false, 0);
        PackStart(new Label("Active restrictions") { Xalign = 0 }, false, false, 0);
        var list = new TreeView(_restrictions);
        list.AppendColumn("Restriction", new CellRendererText(), "text", 0);
        list.AppendColumn("Object", new CellRendererText(), "text", 1);
        list.AppendColumn("Options", new CellRendererText(), "text", 2);
        var scroll = new ScrolledWindow();
        scroll.Add(list);
        PackStart(scroll, true, true, 0);
        PackStart(_status, false, false, 0);
        PackStart(new Label("Turning RLV off clears this account's restrictions. Objects must resend them when it is turned on again.")
        { Xalign = 0, LineWrap = true }, false, false, 0);
        rlv.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _enabled.Active = _rlv.Enabled;
        _restrictions.Clear();
        var restrictions = _rlv.Service.Restrictions.FindRestrictions();
        foreach (var restriction in restrictions.OrderBy(r => r.Behavior).ThenBy(r => r.SenderName))
            _restrictions.AppendValues(restriction.Behavior.ToString(), restriction.SenderName, string.Join("; ", restriction.Args));
        _status.Text = $"{(_rlv.Enabled ? "Enabled" : "Disabled")} · {restrictions.Count} restriction{(restrictions.Count == 1 ? "" : "s")}\n{_rlv.Status}";
    }

    public void Stop() => _rlv.Changed -= Refresh;
}
