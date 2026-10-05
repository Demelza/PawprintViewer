using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Conversation selection and a shared transcript/composer for one account.</summary>
internal sealed class InstantMessagesPanel : Box
{
    private readonly AccountSession _session;
    private readonly Box _conversationRows = new(Orientation.Vertical, 6);
    private readonly Dictionary<UUID, Button> _buttons = new();
    private readonly Stack _pages = new();
    private readonly Label _heading = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private readonly TextView _history = new() { Editable = false, CursorVisible = false, WrapMode = WrapMode.WordChar };
    private readonly ScrolledWindow _historyScroll = new();
    private readonly Entry _input = new() { PlaceholderText = "Write an instant message…" };
    private readonly Button _send = new("Send");
    private readonly Label _status = new() { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
    private ImConversation? _selected;
    private ImConversation? _rendered;
    private string _renderedName = string.Empty;
    private int _renderedCount;
    private bool _restoringDraft;
    private bool _displayed;
    private bool _disposed;

    public InstantMessagesPanel(AccountSession session) : base(Orientation.Horizontal, 0)
    {
        _session = session;
        var grid = new global::Gtk.Grid { ColumnHomogeneous = true, Hexpand = true, Vexpand = true };
        PackStart(grid, true, true, 0);
        var rail = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        rail.PackStart(new Label("Conversations") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End }, false, false, 0);
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        scroll.Add(_conversationRows);
        rail.PackStart(scroll, true, true, 0);
        grid.Attach(rail, 0, 0, 1, 1);

        var content = new Box(Orientation.Horizontal, 0);
        content.PackStart(new Separator(Orientation.Vertical), false, false, 0);
        content.PackStart(_pages, true, true, 0);
        grid.Attach(content, 1, 0, 3, 1);
        _pages.AddNamed(new Label("Use IM in the Friends tab to start a conversation.\nIncoming messages also appear here.")
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
        _session.ConversationChanged += OnConversationChanged;
        _session.StateChanged += OnStateChanged;
        _session.Rlv.Changed += OnRestrictionsChanged;
        if (_session.Conversations.FirstOrDefault() is { } conversation) Select(conversation);
        else RefreshRows();
    }

    public void SetDisplayed(bool displayed)
    {
        if (_disposed) return;
        _displayed = displayed;
        if (displayed && _selected != null) _session.MarkConversationRead(_selected.PeerId);
    }

    public bool Open(UUID peerId)
    {
        if (_disposed) return false;
        try
        {
            Select(_session.OpenConversation(peerId));
            GtkDispatch.Post(() => { if (!_disposed && _displayed) _input.GrabFocus(); });
            return true;
        }
        catch (Exception ex) { _status.Text = ex.Message; return false; }
    }

    private void Select(ImConversation conversation)
    {
        _selected = conversation;
        if (_displayed) _session.MarkConversationRead(conversation.PeerId);
        RefreshRows();
        RenderConversation();
    }

    private void OnConversationChanged(AccountSession account, ImConversation conversation)
    {
        if (_disposed) return;
        _selected ??= conversation;
        if (_displayed && _selected == conversation && conversation.UnreadCount > 0)
        {
            _session.MarkConversationRead(conversation.PeerId);
            return;
        }
        RefreshRows();
        if (_selected == conversation) RenderConversation();
    }

    private void RefreshRows()
    {
        foreach (var conversation in _session.Conversations)
        {
            if (!_buttons.TryGetValue(conversation.PeerId, out var button))
            {
                button = new Button();
                button.Clicked += (_, _) => Select(conversation);
                _buttons.Add(conversation.PeerId, button);
                _conversationRows.PackStart(button, false, false, 0);
            }
            var name = _session.DisplayConversationName(conversation.PeerId);
            var unread = conversation.UnreadCount > 0 ? $" ({conversation.UnreadCount})" : string.Empty;
            button.Label = $"{(_selected == conversation ? "› " : "")}{name}{unread}";
            if (button.Child is Label label) label.Ellipsize = Pango.EllipsizeMode.End;
            button.TooltipText = name;
        }
        _conversationRows.ShowAll();
    }

    private void RenderConversation(bool force = false)
    {
        if (_selected == null) return;
        _pages.VisibleChildName = "conversation";
        var name = _session.DisplayConversationName(_selected.PeerId);
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
            var from = message.Outgoing ? _session.Name : name;
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
        _send.Sensitive = _selected != null && _session.CanSendInstantMessage(_selected.PeerId, _input.Text);
        _status.Text = !_session.IsConnected ? "This account is disconnected." :
            _selected != null && _session.Rlv.Enabled && !_session.Rlv.Service.Permissions.CanSendIM(_input.Text, _selected.PeerId.Guid)
                ? "Sending instant messages to this resident is restricted by RLV." : string.Empty;
    }

    private void SendMessage()
    {
        if (_disposed || _selected == null || string.IsNullOrWhiteSpace(_input.Text)) return;
        try
        {
            _session.SendInstantMessage(_selected.PeerId, _input.Text.Trim());
        }
        catch (Exception ex) { _status.Text = $"Message failed: {ex.Message}"; }
    }

    private void OnStateChanged(AccountSession account) => UpdateComposer();
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
        _session.ConversationChanged -= OnConversationChanged;
        _session.StateChanged -= OnStateChanged;
        _session.Rlv.Changed -= OnRestrictionsChanged;
    }
}
