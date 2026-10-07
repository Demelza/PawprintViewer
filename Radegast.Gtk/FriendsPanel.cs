using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>One account's friends and the actions available for each resident.</summary>
internal sealed class FriendsPanel : Box
{
    private readonly AccountSession _session;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.None };
    private readonly Label _status = new("Open this tab to load friends.") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private readonly Dictionary<UUID, ResidentPaymentWindow> _payments = new();
    private bool _active;
    private bool _refreshQueued;
    private bool _disposed;
    public event Action<UUID>? ImRequested;

    public FriendsPanel(AccountSession session) : base(Orientation.Vertical, 6)
    {
        _session = session;
        BorderWidth = 8;
        PackStart(new Label("Friends") { Xalign = 0 }, false, false, 0);
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        scroll.Add(_list);
        PackStart(scroll, true, true, 0);
        PackStart(_status, false, false, 0);
    }

    public void StartLoading()
    {
        if (_disposed) return;
        if (!_active)
        {
            _active = true;
            _session.FriendsChanged += OnFriendsChanged;
            _session.StateChanged += OnFriendsChanged;
            _session.ConversationChanged += OnConversationChanged;
        }
        Refresh();
    }

    private void OnFriendsChanged(AccountSession session)
    {
        if (_disposed || !_active || _refreshQueued) return;
        _refreshQueued = true;
        GLib.Timeout.Add(180, () =>
        {
            _refreshQueued = false;
            if (!_disposed) Refresh();
            return false;
        });
    }

    private void OnConversationChanged(AccountSession session, ImConversation conversation) => OnFriendsChanged(session);

    private void Refresh()
    {
        if (_disposed || !_active) return;
        GtkWidgetLifetime.Clear(_list);
        if (!_session.IsConnected)
        {
            _list.Add(new Label("Connect to see your friends.") { Xalign = 0, Margin = 8 });
            _status.Text = "Not connected";
            _list.ShowAll();
            return;
        }
        _session.RequestFriendNames();
        var friends = _session.Friends;
        foreach (var friend in friends)
        {
            var row = new Box(Orientation.Horizontal, 10) { Margin = 4 };
            var actions = new Box(Orientation.Horizontal, 6);
            var im = new Button("IM")
            {
                Sensitive = _session.CanOpenConversation(friend.Id),
                TooltipText = _session.CanOpenConversation(friend.Id) ? "Open an instant message conversation" : "Starting this conversation is restricted by RLV"
            };
            im.Clicked += (_, _) => ImRequested?.Invoke(friend.Id);
            actions.PackStart(im, false, false, 0);
            var pay = new Button("Pay") { Sensitive = _session.CanPayFriend(friend.Id), TooltipText = "Pay this friend" };
            pay.Clicked += (_, _) => OpenPayment(friend.Id);
            actions.PackStart(pay, false, false, 0);
            var teleport = new Button("Offer TP")
            {
                Sensitive = _session.CanOfferFriendTeleport(friend.Id),
                TooltipText = _session.CanOfferFriendTeleport(friend.Id)
                    ? "Offer a teleport to this account's current location" : "Offering a teleport is restricted by RLV"
            };
            teleport.Clicked += (_, _) => OfferTeleport(friend.Id);
            actions.PackStart(teleport, false, false, 0);
            row.PackStart(actions, false, false, 0);
            var name = new Box(Orientation.Horizontal, 6);
            if (friend.IsOnline) name.PackStart(new Label("●") { TooltipText = "Online" }, false, false, 0);
            var displayName = _session.DisplayFriendName(friend.Id);
            name.PackStart(new Label(displayName)
            {
                Xalign = 0,
                Ellipsize = Pango.EllipsizeMode.End,
                TooltipText = $"{displayName} · {(friend.IsOnline ? "Online" : "Offline")}"
            }, true, true, 0);
            row.PackStart(name, true, true, 0);
            _list.Add(row);
        }
        if (friends.Count == 0) _list.Add(new Label("No friends found.") { Xalign = 0, Margin = 8 });
        _status.Text = $"{friends.Count} friend{(friends.Count == 1 ? "" : "s")} · {friends.Count(friend => friend.IsOnline)} online";
        _list.ShowAll();
    }

    private void OpenPayment(UUID id)
    {
        if (_disposed || !_session.CanPayFriend(id)) return;
        if (_payments.TryGetValue(id, out var existing))
        {
            ShowPayment(existing);
            return;
        }
        if (Toplevel is not Window parent) return;
        var payment = new ResidentPaymentWindow(parent, _session, id, requireFriend: true);
        _payments.Add(id, payment);
        payment.Closed += () => _payments.Remove(id);
        ShowPayment(payment);
    }

    private void ShowPayment(ResidentPaymentWindow payment)
    {
        if (Toplevel is MainWindow main) main.ShowChildWindow(payment);
        else { payment.ShowAll(); payment.Present(); }
    }

    private void OfferTeleport(UUID id)
    {
        try
        {
            _session.OfferFriendTeleport(id);
            _status.Text = $"Teleport offer sent to {_session.DisplayFriendName(id)}.";
        }
        catch (Exception ex) { _status.Text = $"Teleport offer failed: {ex.Message}"; }
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _session.FriendsChanged -= OnFriendsChanged;
        _session.StateChanged -= OnFriendsChanged;
        _session.ConversationChanged -= OnConversationChanged;
        foreach (var payment in _payments.Values.ToArray()) payment.ClosePayment();
        _payments.Clear();
    }
}
