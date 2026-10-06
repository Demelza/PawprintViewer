using Gdk;
using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Plain chat text with account-specific, clickable avatar profile names.</summary>
internal sealed class ChatHistoryView : TextView
{
    private readonly AccountSession _session;
    private readonly List<string> _lines = new();
    private readonly Dictionary<UUID, TextTag> _links = new();
    private readonly Dictionary<UUID, string> _linkNames = new();
    private Cursor? _hand;
    private UUID _pressedLink;
    private int _pressX, _pressY, _revision;
    private bool _stopped, _connected;

    public ChatHistoryView(AccountSession session)
    {
        _session = session;
        _connected = session.IsConnected;
        Editable = false;
        CursorVisible = false;
        WrapMode = WrapMode.WordChar;
        _session.AvatarNamesChanged += OnNamesChanged;
        _session.FriendsChanged += OnNamesChanged;
        _session.StateChanged += OnStateChanged;
        _session.Rlv.Changed += RefreshText;
        Destroyed += (_, _) => Stop();
    }

    public void AppendLine(string text)
    {
        if (_stopped) return;
        _lines.Add(text);
        InsertLine(text);
    }

    public void Clear()
    {
        if (_stopped) return;
        _lines.Clear();
        ClearBuffer();
    }

    private void ClearBuffer()
    {
        _revision++;
        _pressedLink = UUID.Zero;
        Buffer.Text = string.Empty;
        foreach (var tag in _links.Values)
        {
            Buffer.TagTable.Remove(tag);
            tag.Dispose();
        }
        _links.Clear();
        _linkNames.Clear();
    }

    private void InsertLine(string text)
    {
        foreach (var span in _session.FormatChatText(text))
        {
            var offset = Buffer.CharCount;
            var end = Buffer.EndIter;
            Buffer.Insert(ref end, span.Text);
            var start = Buffer.GetIterAtOffset(offset);
            // Text inserted at a tag boundary can inherit the preceding avatar's tag.
            Buffer.RemoveAllTags(start, end);
            if (span.AvatarId != UUID.Zero)
            {
                if (!_links.TryGetValue(span.AvatarId, out var tag))
                {
                    tag = new TextTag("avatar-" + span.AvatarId) { Underline = Pango.Underline.Single };
                    tag.ForegroundRgba = StyleContext.GetColor(StateFlags.Link);
                    Buffer.TagTable.Add(tag);
                    _links.Add(span.AvatarId, tag);
                }
                Buffer.ApplyTag(tag, start, end);
                _linkNames[span.AvatarId] = span.Text;
            }
        }
        var lastOffset = Buffer.CharCount;
        var last = Buffer.EndIter;
        Buffer.Insert(ref last, Environment.NewLine);
        Buffer.RemoveAllTags(Buffer.GetIterAtOffset(lastOffset), last);
    }

    private void OnNamesChanged(AccountSession account)
    {
        if (_linkNames.Any(link => link.Value != _session.DisplayChatAvatarName(link.Key) || !_session.CanViewAvatarProfile(link.Key)))
            RefreshText();
    }

    private void OnStateChanged(AccountSession account)
    {
        if (_connected == account.IsConnected) return;
        _connected = account.IsConnected;
        OnNamesChanged(account);
    }

    private void RefreshText()
    {
        if (_stopped) return;
        var adjustment = Vadjustment;
        var scroll = adjustment?.Value ?? 0;
        var bottom = adjustment == null || scroll + adjustment.PageSize >= adjustment.Upper - 24;
        var selected = Buffer.GetSelectionBounds(out var start, out var end);
        var startOffset = start.Offset; var endOffset = end.Offset;
        ClearBuffer();
        foreach (var line in _lines) InsertLine(line);
        if (selected) Buffer.SelectRange(Buffer.GetIterAtOffset(Math.Min(startOffset, Buffer.CharCount)),
            Buffer.GetIterAtOffset(Math.Min(endOffset, Buffer.CharCount)));
        var revision = _revision;
        GtkDispatch.Post(() =>
        {
            if (_stopped || _revision != revision) return;
            if (bottom) ScrollToIter(Buffer.EndIter, 0, false, 0, 1);
            else if (adjustment != null) adjustment.Value = scroll;
        });
    }

    private UUID LinkAt(Event evnt, double x, double y)
    {
        if (_stopped || evnt.Window == null) return UUID.Zero;
        WindowToBufferCoords(GetWindowType(evnt.Window), (int)x, (int)y, out var bx, out var by);
        if (!GetIterAtLocation(out var iter, bx, by)) return UUID.Zero;
        foreach (var (id, tag) in _links)
            if (iter.HasTag(tag) && _session.CanViewAvatarProfile(id)) return id;
        return UUID.Zero;
    }

    protected override bool OnButtonPressEvent(EventButton evnt)
    {
        _pressedLink = evnt.Button == 1 && evnt.Type == EventType.ButtonPress ? LinkAt(evnt, evnt.X, evnt.Y) : UUID.Zero;
        _pressX = (int)evnt.X; _pressY = (int)evnt.Y;
        return base.OnButtonPressEvent(evnt);
    }

    protected override bool OnButtonReleaseEvent(EventButton evnt)
    {
        var handled = base.OnButtonReleaseEvent(evnt);
        var id = _pressedLink;
        _pressedLink = UUID.Zero;
        if (evnt.Button == 1 && id != UUID.Zero && id == LinkAt(evnt, evnt.X, evnt.Y) && !Buffer.HasSelection &&
            !global::Gtk.Drag.CheckThreshold(this, _pressX, _pressY, (int)evnt.X, (int)evnt.Y))
        {
            _session.OpenAvatarProfile(id);
            return true;
        }
        return handled;
    }

    protected override bool OnMotionNotifyEvent(EventMotion evnt)
    {
        var window = GetWindow(TextWindowType.Text);
        if (window != null)
        {
            var link = LinkAt(evnt, evnt.X, evnt.Y) != UUID.Zero;
            if (link) _hand ??= new Cursor(Display, CursorType.Hand2);
            window.Cursor = link ? _hand : null;
        }
        return base.OnMotionNotifyEvent(evnt);
    }

    protected override void OnStyleUpdated()
    {
        base.OnStyleUpdated();
        if (_links == null) return;
        foreach (var tag in _links.Values) tag.ForegroundRgba = StyleContext.GetColor(StateFlags.Link);
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _session.AvatarNamesChanged -= OnNamesChanged;
        _session.FriendsChanged -= OnNamesChanged;
        _session.StateChanged -= OnStateChanged;
        _session.Rlv.Changed -= RefreshText;
        _hand?.Dispose();
        _hand = null;
    }
}
