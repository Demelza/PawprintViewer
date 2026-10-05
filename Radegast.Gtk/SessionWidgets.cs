using Gtk;

namespace Radegast.Gtk;

/// <summary>The widgets belonging to one account, kept alive while another is selected.</summary>
internal sealed class SessionWidgets : IDisposable
{
    private readonly AccountSession _session;
    private readonly TextBuffer _chatBuffer;
    private readonly Entry _chatInput;
    private readonly ListBox _nearbyList;
    private readonly InventoryPanel _inventoryPanel;

    public Box Root { get; }
    public Box AccountRow { get; }
    public Button AccountButton { get; }
    public Button LogoutButton { get; }
    public Notebook Tabs { get; }
    public int UnreadCount { get; set; }

    public SessionWidgets(AccountSession session)
    {
        _session = session;

        AccountRow = new Box(Orientation.Horizontal, 3);
        AccountButton = new Button(session.Name) { TooltipText = "Select account" };
        LogoutButton = new Button("×") { TooltipText = "Log out this account" };
        AccountRow.PackStart(AccountButton, true, true, 0);
        AccountRow.PackStart(LogoutButton, false, false, 0);

        Root = new Box(Orientation.Horizontal, 0);
        Tabs = new Notebook { Scrollable = true };
        Root.PackStart(Tabs, true, true, 0);
        Root.PackStart(new Separator(Orientation.Vertical), false, false, 0);

        var nearbyPane = new Box(Orientation.Vertical, 6);
        nearbyPane.SetSizeRequest(220, -1);
        var nearbyHeading = new Label("Nearby avatars") { Xalign = 0 };
        nearbyPane.PackStart(nearbyHeading, false, false, 8);
        var nearbyScroll = new ScrolledWindow();
        nearbyScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        _nearbyList = new ListBox { SelectionMode = SelectionMode.None };
        nearbyScroll.Add(_nearbyList);
        nearbyPane.PackStart(nearbyScroll, true, true, 0);
        Root.PackStart(nearbyPane, false, false, 8);

        var chatPage = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        var chatScroll = new ScrolledWindow();
        chatScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        var chatView = new TextView
        {
            Editable = false,
            CursorVisible = false,
            WrapMode = WrapMode.WordChar
        };
        _chatBuffer = chatView.Buffer;
        chatScroll.Add(chatView);
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

        AddPendingTab("IMs");
        AddPendingTab("Group Chats");
        _inventoryPanel = new InventoryPanel(session);
        Tabs.AppendPage(_inventoryPanel, new Label("Inventory"));
        AddPendingTab("Attachments");
        AddPendingTab("Friends");
        Tabs.SwitchPage += (_, _) => GtkDispatch.Post(() =>
        {
            if (Tabs.CurrentPage == 3) _inventoryPanel.StartLoading();
        });
        RefreshNearby();
    }

    private void AddPendingTab(string title)
    {
        var label = new Label($"{title} will be added in a later build.");
        Tabs.AppendPage(label, new Label(title));
    }

    private void SendChat()
    {
        var message = _chatInput.Text.Trim();
        if (message.Length == 0) return;
        _session.SendNearbyChat(message);
        _chatInput.Text = string.Empty;
    }

    public void AppendChat(string line)
    {
        var end = _chatBuffer.EndIter;
        _chatBuffer.Insert(ref end, line + Environment.NewLine);
    }

    public void RefreshNearby()
    {
        foreach (Widget child in _nearbyList.Children)
            _nearbyList.Remove(child);

        if (_session.Nearby.Count == 0)
        {
            var empty = new Label("No nearby avatars") { Xalign = 0, Margin = 8 };
            _nearbyList.Add(empty);
        }
        else
        {
            foreach (var person in _session.Nearby)
            {
                var label = new Label($"{person.Name}  ·  {person.Distance} m")
                {
                    Xalign = 0,
                    Margin = 6,
                    TooltipText = person.Id.ToString()
                };
                _nearbyList.Add(label);
            }
        }
        _nearbyList.ShowAll();
    }

    public void UpdateAccountLabel(bool selected)
    {
        var unread = UnreadCount > 0 ? $" ({UnreadCount})" : string.Empty;
        AccountButton.Label = $"{(selected ? "› " : "")}{_session.Name}{unread}";
        AccountButton.TooltipText = _session.Status;
    }

    public void Dispose() => _inventoryPanel.Stop();
}
