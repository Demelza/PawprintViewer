using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed class InstantMessagesPanel(AccountSession session) : ChatConversationsPanel(session, false);
internal sealed class GroupChatsPanel(AccountSession session) : ChatConversationsPanel(session, true);

/// <summary>The shared layout for one account's private or group conversations.</summary>
internal class ChatConversationsPanel : Box
{
    private readonly AccountSession _session;
    private readonly bool _groupChats;
    private readonly Box _conversationRows = new(Orientation.Vertical, 6);
    private readonly Dictionary<UUID, Button> _buttons = new();
    private readonly Stack _pages = new();
    private readonly Label _heading = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private readonly TextView _history = new() { Editable = false, CursorVisible = false, WrapMode = WrapMode.WordChar };
    private readonly ScrolledWindow _historyScroll = new();
    private readonly Entry _input = new() { PlaceholderText = "Write an instant message…" };
    private readonly Button _send = new("Send");
    private readonly Label _status = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private readonly Label _railStatus = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private ChatConversation? _selected;
    private ChatConversation? _rendered;
    private string _renderedName = string.Empty;
    private int _renderedCount;
    private bool _restoringDraft;
    private bool _displayed;
    private bool _disposed;

    protected ChatConversationsPanel(AccountSession session, bool groupChats) : base(Orientation.Horizontal, 0)
    {
        _session = session;
        _groupChats = groupChats;
        var grid = new global::Gtk.Grid { ColumnHomogeneous = true, Hexpand = true, Vexpand = true };
        PackStart(grid, true, true, 0);
        var rail = new Box(Orientation.Vertical, 6) { BorderWidth = 8, Vexpand = true };
        rail.PackStart(new Label(groupChats ? "Groups" : "Conversations") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End }, false, false, 0);
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        scroll.Add(_conversationRows);
        rail.PackStart(scroll, true, true, 0);
        if (groupChats)
        {
            var refresh = new Button("Refresh groups");
            refresh.Clicked += (_, _) => _session.RequestGroups(true);
            rail.PackStart(refresh, false, false, 0);
            rail.PackStart(_railStatus, false, false, 0);
            _input.PlaceholderText = "Write a group message…";
        }
        grid.Attach(rail, 0, 0, 1, 1);

        var content = new Box(Orientation.Horizontal, 0) { Vexpand = true };
        content.PackStart(new Separator(Orientation.Vertical), false, false, 0);
        content.PackStart(_pages, true, true, 0);
        grid.Attach(content, 1, 0, 3, 1);
        _pages.AddNamed(new Label(groupChats
            ? "Select a group on the left to join its chat.\nIncoming group messages also appear here."
            : "Use IM in the Friends tab to start a conversation.\nIncoming messages also appear here.")
            { Wrap = true, Margin = 12 }, "empty");
        var conversationPage = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        conversationPage.PackStart(_heading, false, false, 0);
        _historyScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        _historyScroll.Add(_history);
        conversationPage.PackStart(_historyScroll, true, true, 0);
        var compose = new Box(Orientation.Horizontal, 6);
        _input.Changed += (_, _) =>
        {
            if (!_restoringDraft && _selected != null) _selected.Draft = _input.Text;
            UpdateComposer();
        };
        _input.Activated += (_, _) => SendMessage();
        _send.Clicked += (_, _) => SendMessage();
        compose.PackStart(_input, true, true, 0);
        compose.PackStart(_send, false, false, 0);
        conversationPage.PackStart(compose, false, false, 0);
        conversationPage.PackStart(_status, false, false, 0);
        _pages.AddNamed(conversationPage, "conversation");
        _pages.VisibleChildName = "empty";
        if (groupChats)
        {
            _session.GroupConversationChanged += OnGroupConversationChanged;
            _session.GroupsChanged += OnGroupsChanged;
        }
        else _session.ConversationChanged += OnPrivateConversationChanged;
        _session.StateChanged += OnStateChanged;
        _session.Rlv.Changed += OnRestrictionsChanged;
        if (Conversations.FirstOrDefault(chat => !groupChats || chat.Messages.Count > 0) is { } conversation) Select(conversation);
        else RefreshRows();
    }

    private IReadOnlyList<ChatConversation> Conversations => _groupChats ? _session.GroupConversations : _session.Conversations;
    private string ConversationName(UUID id) => _groupChats ? _session.DisplayGroupName(id) : _session.DisplayConversationName(id);
    private void MarkRead(UUID id)
    {
        if (_groupChats) _session.MarkGroupChatRead(id);
        else _session.MarkConversationRead(id);
    }

    public void StartLoading()
    {
        if (!_disposed && _groupChats) { _session.RequestGroups(); RefreshRows(); }
    }

    public void SetDisplayed(bool displayed)
    {
        if (_disposed) return;
        _displayed = displayed;
        if (displayed && _selected != null) MarkRead(_selected.Id);
        if (displayed) RenderConversation();
    }

    public bool IsDisplaying(UUID id) => _displayed && _selected?.Id == id;

    public bool Open(UUID peerId)
    {
        if (_disposed) return false;
        try
        {
            Select(_groupChats ? _session.OpenGroupChat(peerId) : _session.OpenConversation(peerId));
            GtkDispatch.Post(() => { if (!_disposed && _displayed) _input.GrabFocus(); });
            return true;
        }
        catch (Exception ex) { _status.Text = ex.Message; _railStatus.Text = ex.Message; return false; }
    }

    private void Select(ChatConversation conversation)
    {
        _selected = conversation;
        if (_displayed) MarkRead(conversation.Id);
        RefreshRows();
        RenderConversation();
    }

    private void OnPrivateConversationChanged(AccountSession account, ImConversation conversation) => OnConversationChanged(conversation);
    private void OnGroupConversationChanged(AccountSession account, GroupConversation conversation) => OnConversationChanged(conversation);

    private void OnGroupsChanged(AccountSession account)
    {
        if (_disposed) return;
        RefreshRows();
        RenderConversation();
    }

    private void OnConversationChanged(ChatConversation conversation)
    {
        if (_disposed) return;
        _selected ??= conversation;
        if (_displayed && _selected == conversation && conversation.UnreadCount > 0)
        {
            MarkRead(conversation.Id);
            return;
        }
        RefreshRows();
        if (_selected == conversation) RenderConversation();
    }

    private void RefreshRows()
    {
        var conversations = Conversations;
        var ids = conversations.Select(chat => chat.Id).ToHashSet();
        foreach (var id in _buttons.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _conversationRows.Remove(_buttons[id]);
            _buttons.Remove(id);
        }
        if (_selected != null && !ids.Contains(_selected.Id))
        {
            _selected = null;
            _pages.VisibleChildName = "empty";
        }
        var index = 0;
        foreach (var conversation in conversations)
        {
            if (!_buttons.TryGetValue(conversation.Id, out var button))
            {
                button = new Button();
                button.Clicked += (_, _) =>
                {
                    if (_groupChats && _session.CanOpenGroupChat(conversation.Id)) Open(conversation.Id);
                    else Select(conversation);
                };
                _buttons.Add(conversation.Id, button);
                _conversationRows.PackStart(button, false, false, 0);
            }
            _conversationRows.ReorderChild(button, index++);
            var name = ConversationName(conversation.Id);
            var unread = conversation.UnreadCount > 0 ? $" ({conversation.UnreadCount})" : string.Empty;
            button.Label = $"{(_selected == conversation ? "› " : "")}{name}{unread}";
            if (button.Child is Label label) label.Ellipsize = Pango.EllipsizeMode.End;
            button.TooltipText = name;
        }
        if (_groupChats) _railStatus.Text = !_session.IsConnected ? "Not connected" : !_session.GroupsLoaded ? "Loading groups…" :
            conversations.Count == 0 ? "No groups found." : string.Empty;
        _conversationRows.ShowAll();
    }

    private void RenderConversation(bool force = false)
    {
        if (_selected == null) return;
        _pages.VisibleChildName = "conversation";
        var name = ConversationName(_selected.Id);
        _heading.Text = name;
        _heading.TooltipText = name;
        var adjustment = _historyScroll.Vadjustment;
        var previousScroll = adjustment.Value;
        var switched = _rendered != _selected;
        var atBottom = adjustment.Value + adjustment.PageSize >= adjustment.Upper - 24;
        var rebuild = switched || force || name != _renderedName;
        if (rebuild) { _history.Buffer.Text = string.Empty; _renderedCount = 0; }
        var appended = _selected.Messages.Count > _renderedCount;
        var outgoing = !rebuild && appended && _selected.Messages[^1].Outgoing;
        for (; _renderedCount < _selected.Messages.Count; _renderedCount++)
        {
            var message = _selected.Messages[_renderedCount];
            var from = _groupChats ? _session.DisplayGroupSender(message) : message.Outgoing ? _session.Name : name;
            var time = message.Timestamp.Date == DateTime.Today ? message.Timestamp.ToString("HH:mm") : message.Timestamp.ToString("yyyy-MM-dd HH:mm");
            var text = message.Text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase)
                ? $"[{time}] {from} {message.Text[4..]}" : $"[{time}] {from}: {message.Text}";
            var end = _history.Buffer.EndIter;
            _history.Buffer.Insert(ref end, _session.RedactText(text) + Environment.NewLine);
        }
        _rendered = _selected;
        _renderedName = name;
        if (_input.Text != _selected.Draft)
        {
            _restoringDraft = true;
            _input.Text = _selected.Draft;
            _restoringDraft = false;
        }
        UpdateComposer();
        if (!rebuild && !appended) return;
        var selected = _selected;
        GtkDispatch.Post(() =>
        {
            if (_disposed || _selected != selected) return;
            if (switched || atBottom || outgoing) _history.ScrollToIter(_history.Buffer.EndIter, 0, false, 0, 1);
            else adjustment.Value = previousScroll;
        });
    }

    private void UpdateComposer()
    {
        if (_disposed) return;
        _input.Sensitive = _selected != null && _session.IsConnected;
        _send.Sensitive = _selected != null && (_groupChats
            ? _session.CanSendGroupMessage(_selected.Id, _input.Text) : _session.CanSendInstantMessage(_selected.Id, _input.Text));
        if (_groupChats)
        {
            _status.Text = _selected != null ? _session.GroupChatStatus(_selected.Id, _input.Text) : string.Empty;
            return;
        }
        _status.Text = !_session.IsConnected ? "This account is disconnected." :
            _selected != null && _session.Rlv.Enabled && !_session.Rlv.Service.Permissions.CanSendIM(_input.Text, _selected.Id.Guid)
                ? "Sending instant messages to this resident is restricted by RLV." : string.Empty;
    }

    private void SendMessage()
    {
        if (_disposed || _selected == null || string.IsNullOrWhiteSpace(_input.Text)) return;
        try
        {
            if (_groupChats) _session.SendGroupMessage(_selected.Id, _input.Text.Trim());
            else _session.SendInstantMessage(_selected.Id, _input.Text.Trim());
        }
        catch (Exception ex) { _status.Text = $"Message failed: {ex.Message}"; }
    }

    private void OnStateChanged(AccountSession account)
    {
        if (_disposed) return;
        UpdateComposer();
        if (_groupChats) RefreshRows();
    }
    private void OnRestrictionsChanged()
    {
        if (_disposed) return;
        RefreshRows();
        RenderConversation(true);
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _session.ConversationChanged -= OnPrivateConversationChanged;
        _session.GroupConversationChanged -= OnGroupConversationChanged;
        _session.GroupsChanged -= OnGroupsChanged;
        _session.StateChanged -= OnStateChanged;
        _session.Rlv.Changed -= OnRestrictionsChanged;
    }
}
