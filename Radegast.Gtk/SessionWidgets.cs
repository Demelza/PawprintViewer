using Gtk;
using System.Globalization;

namespace Radegast.Gtk;

/// <summary>The widgets belonging to one account, kept alive while another is selected.</summary>
internal sealed class SessionWidgets : IDisposable
{
    private readonly AccountSession _session;
    private readonly ChatHistoryView _chatHistory;
    private readonly Entry _chatInput;
    private readonly ListBox _nearbyList;
    private readonly InventoryPanel _inventoryPanel;
    private readonly AttachmentsPanel _attachmentsPanel;
    private readonly FriendsPanel _friendsPanel;
    private readonly ObjectsPanel _objectsPanel;
    private readonly MapPanel _mapPanel;
    private readonly InstantMessagesPanel _imPanel;
    private readonly Label _imTabLabel = new("IMs");
    private readonly GroupChatsPanel _groupPanel;
    private readonly Label _groupTabLabel = new("Group Chats");
    private bool _selectedAccount;
    private readonly AccountSettingsPanel _settingsPanel;
    private readonly Stack _inventoryPages = new();
    private readonly Label _locationLabel = new() { Xalign = 0, MarginStart = 8, Ellipsize = Pango.EllipsizeMode.End };
    private readonly Label _balanceLabel = new() { Xalign = 0, MarginStart = 8, Ellipsize = Pango.EllipsizeMode.End };
    private bool _disposed;

    public Box Root { get; }
    public Box NearbyPane { get; }
    public Box AccountRow { get; }
    public Button AccountButton { get; }
    public Button LogoutButton { get; }
    public Notebook Tabs { get; }
    public int UnreadCount { get; set; }

    public SessionWidgets(AccountSession session)
    {
        _session = session;

        AccountRow = new Box(Orientation.Vertical, 2);
        AccountButton = new Button(session.Name) { TooltipText = "Select account" };
        if (AccountButton.Child is Label accountName) accountName.Ellipsize = Pango.EllipsizeMode.End;
        LogoutButton = new SquareButton("×") { TooltipText = "Log out this account" };
        var accountHeader = new Box(Orientation.Horizontal, 3);
        accountHeader.PackStart(AccountButton, true, true, 0);
        accountHeader.PackStart(LogoutButton, false, false, 0);
        AccountRow.PackStart(accountHeader, false, false, 0);
        AccountRow.PackStart(_locationLabel, false, false, 0);
        AccountRow.PackStart(_balanceLabel, false, false, 0);
        UpdateLocation();
        UpdateBalance();
        GLib.Timeout.Add(1000, () =>
        {
            if (_disposed) return false;
            UpdateLocation();
            return true;
        });

        Root = new Box(Orientation.Horizontal, 0);
        Tabs = new Notebook { Scrollable = true };
        Root.PackStart(Tabs, true, true, 0);

        NearbyPane = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        var nearbyHeading = new Label("Nearby avatars") { Xalign = 0 };
        NearbyPane.PackStart(nearbyHeading, false, false, 8);
        var nearbyScroll = new ScrolledWindow();
        nearbyScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        _nearbyList = new ListBox { SelectionMode = SelectionMode.None };
        nearbyScroll.Add(_nearbyList);
        NearbyPane.PackStart(nearbyScroll, true, true, 0);

        var chatPage = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        var chatScroll = new ScrolledWindow();
        chatScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        _chatHistory = new ChatHistoryView(session);
        chatScroll.Add(_chatHistory);
        chatPage.PackStart(chatScroll, true, true, 0);

        var compose = new Box(Orientation.Horizontal, 6);
        _chatInput = new Entry { PlaceholderText = "Say something nearby…" };
        _chatInput.Activated += (_, _) => SendChat();
        var sendButton = new Button("Send");
        sendButton.Clicked += (_, _) => SendChat();
        compose.PackStart(_chatInput, true, true, 0);
        compose.PackStart(sendButton, false, false, 0);
        chatPage.PackStart(compose, false, false, 0);
        Tabs.AppendPage(chatPage, new Label("Nearby Chat"));

        _friendsPanel = new FriendsPanel(session);
        _friendsPanel.ImRequested += OpenInstantMessages;
        Tabs.AppendPage(_friendsPanel, new Label("Friends"));
        _imPanel = new InstantMessagesPanel(session);
        Tabs.AppendPage(_imPanel, _imTabLabel);
        _groupPanel = new GroupChatsPanel(session);
        Tabs.AppendPage(_groupPanel, _groupTabLabel);
        _inventoryPanel = new InventoryPanel(session);
        _inventoryPages.AddNamed(_inventoryPanel, "inventory");
        _inventoryPages.AddNamed(new Label("Inventory is hidden by an RLV restriction."), "restricted");
        Tabs.AppendPage(_inventoryPages, new Label("Inventory"));
        _attachmentsPanel = new AttachmentsPanel(session);
        Tabs.AppendPage(_attachmentsPanel, new Label("Attachments"));
        _objectsPanel = new ObjectsPanel(session);
        Tabs.AppendPage(_objectsPanel, new Label("Objects"));
        _mapPanel = new MapPanel(session);
        Tabs.AppendPage(_mapPanel, new Label("Map"));
        _settingsPanel = new AccountSettingsPanel(session);
        Tabs.AppendPage(_settingsPanel, new Label("Account Settings"));
        session.Rlv.Changed += UpdateRestrictions;
        session.ConversationChanged += OnConversationChanged;
        session.GroupConversationChanged += OnGroupConversationChanged;
        if (session.Conversations.FirstOrDefault() is { } conversation) OnConversationChanged(session, conversation);
        if (session.GroupConversations.FirstOrDefault() is { } group) OnGroupConversationChanged(session, group);
        Tabs.SwitchPage += (_, args) =>
        {
            if (_disposed) return;
            _imPanel.SetDisplayed(_selectedAccount && args.PageNum == Tabs.PageNum(_imPanel));
            _groupPanel.SetDisplayed(_selectedAccount && args.PageNum == Tabs.PageNum(_groupPanel));
            _objectsPanel.SetDisplayed(_selectedAccount && args.PageNum == Tabs.PageNum(_objectsPanel));
            _mapPanel.SetDisplayed(_selectedAccount && args.PageNum == Tabs.PageNum(_mapPanel));
            GtkDispatch.Post(() =>
            {
                if (_disposed) return;
                if (IsCurrentPage(_groupPanel)) _groupPanel.StartLoading();
                if (IsCurrentPage(_inventoryPages) && _inventoryPages.VisibleChildName == "inventory") _inventoryPanel.StartLoading();
                if (IsCurrentPage(_attachmentsPanel)) _attachmentsPanel.StartLoading();
                if (IsCurrentPage(_friendsPanel)) _friendsPanel.StartLoading();
            });
        };
        UpdateRestrictions();
    }

    public void SetSelected(bool selected)
    {
        _selectedAccount = selected;
        _imPanel.SetDisplayed(selected && IsCurrentPage(_imPanel));
        _groupPanel.SetDisplayed(selected && IsCurrentPage(_groupPanel));
        _objectsPanel.SetDisplayed(selected && IsCurrentPage(_objectsPanel));
        _mapPanel.SetDisplayed(selected && IsCurrentPage(_mapPanel));
    }

    private bool IsCurrentPage(Widget page) => Tabs.CurrentPage == Tabs.PageNum(page);

    internal void OpenInstantMessages(LibreMetaverse.UUID peerId)
    {
        if (!_imPanel.Open(peerId)) return;
        Tabs.CurrentPage = Tabs.PageNum(_imPanel);
        _imPanel.SetDisplayed(_selectedAccount);
    }

    public bool IsNotificationDisplayed(AccountNotification notice) => notice.Category switch
    {
        NotificationCategory.InstantMessages => _imPanel.IsDisplaying(notice.TargetId),
        NotificationCategory.GroupChats => _groupPanel.IsDisplaying(notice.TargetId),
        NotificationCategory.WornObjects => Tabs.CurrentPage == 0,
        NotificationCategory.Friends => IsCurrentPage(_friendsPanel),
        _ => false
    };

    private void OnConversationChanged(AccountSession account, ImConversation conversation)
    {
        var unread = account.UnreadInstantMessages;
        _imTabLabel.Text = unread > 0 ? $"IMs ({unread})" : "IMs";
    }

    private void OnGroupConversationChanged(AccountSession account, GroupConversation conversation)
    {
        var unread = account.UnreadGroupMessages;
        _groupTabLabel.Text = unread > 0 ? $"Group Chats ({unread})" : "Group Chats";
    }

    private void SendChat()
    {
        var message = _chatInput.Text.Trim();
        if (message.Length == 0) return;
        if (_session.SendNearbyChat(message)) _chatInput.Text = string.Empty;
    }

    public void AppendChat(string line)
    {
        _chatHistory.AppendLine(line);
    }

    public void RefreshNearby()
    {
        foreach (Widget child in _nearbyList.Children)
            _nearbyList.Remove(child);

        if (_session.Rlv.Enabled && !_session.Rlv.Service.Permissions.CanShowNearby())
            _nearbyList.Add(new Label("Nearby avatars hidden by RLV")
                { Xalign = 0, Margin = 8, Ellipsize = Pango.EllipsizeMode.End });
        else if (_session.Nearby.Count == 0)
        {
            var empty = new Label("No nearby avatars") { Xalign = 0, Margin = 8 };
            _nearbyList.Add(empty);
        }
        else
        {
            foreach (var person in _session.Nearby)
            {
                var showName = !_session.Rlv.Enabled || _session.Rlv.Service.Permissions.CanShowNames(person.Id.Guid);
                var label = new Label($"{(showName ? person.Name : "Resident")}  ·  {person.Distance} m")
                {
                    Xalign = 0,
                    Margin = 6,
                    Ellipsize = Pango.EllipsizeMode.End,
                    TooltipText = showName ? $"{person.Name} · {person.Distance} m\n{person.Id}" : null
                };
                _nearbyList.Add(label);
            }
        }
        _nearbyList.ShowAll();
    }

    public void UpdateAccountLabel(bool selected)
    {
        var totalUnread = UnreadCount + _session.UnreadInstantMessages + _session.UnreadGroupMessages;
        var unread = totalUnread > 0 ? $" ({totalUnread})" : string.Empty;
        AccountButton.Label = $"{(selected ? "› " : "")}{_session.Name}{unread}";
        // Gtk.Button replaces its child label when its text changes.
        if (AccountButton.Child is Label accountName) accountName.Ellipsize = Pango.EllipsizeMode.End;
        AccountButton.TooltipText = $"{_session.Name}\n{_session.Status}";
        UpdateLocation();
        UpdateBalance();
    }

    private void UpdateBalance() => _balanceLabel.Text = _session.Balance is { } balance
        ? $"{balance.ToString("N0", CultureInfo.InvariantCulture)} L$"
        : "… L$";

    private void UpdateLocation()
    {
        var sim = _session.Client.Network.CurrentSim;
        if (!_session.IsConnected || sim == null)
        {
            _locationLabel.Text = _session.Status;
            _locationLabel.TooltipText = _locationLabel.Text;
            return;
        }

        if (_session.Rlv.Enabled && !_session.Rlv.Service.Permissions.CanShowLoc())
        {
            _locationLabel.Text = "Location hidden by RLV";
            _locationLabel.TooltipText = null;
            return;
        }
        var position = _session.Client.Self.SimPosition;
        _locationLabel.Text = $"{sim.Name}  ({(int)position.X}, {(int)position.Y}, {(int)position.Z})";
        _locationLabel.TooltipText = _locationLabel.Text;
    }

    private void UpdateRestrictions()
    {
        if (_disposed) return;
        _inventoryPages.VisibleChildName = _session.Rlv.Enabled && !_session.Rlv.Service.Permissions.CanShowInv()
            ? "restricted" : "inventory";
        UpdateLocation();
        RefreshNearby();
        if (IsCurrentPage(_inventoryPages) && _inventoryPages.VisibleChildName == "inventory") _inventoryPanel.StartLoading();
    }

    public void Dispose()
    {
        _disposed = true;
        _chatHistory.Stop();
        _session.Rlv.Changed -= UpdateRestrictions;
        _session.ConversationChanged -= OnConversationChanged;
        _session.GroupConversationChanged -= OnGroupConversationChanged;
        _friendsPanel.ImRequested -= OpenInstantMessages;
        _imPanel.Stop();
        _groupPanel.Stop();
        _settingsPanel.Stop();
        _inventoryPanel.Stop();
        _attachmentsPanel.Stop();
        _friendsPanel.Stop();
        _objectsPanel.Stop();
        _mapPanel.Stop();
    }
}
