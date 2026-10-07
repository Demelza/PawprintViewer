using Gdk;
using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Read-only chat or profile text with account-specific, clickable links.</summary>
internal sealed class ChatHistoryView : TextView
{
    private readonly AccountSession _session;
    private readonly bool _followEnd;
    private readonly bool _profileLinks;
    private readonly Queue<HistoryLine> _lines = new();
    private readonly Dictionary<ProfileTextLink, TextTag> _links = new();
    private readonly Dictionary<ProfileTextLink, int> _linkUses = new();
    private readonly Dictionary<UUID, string> _linkNames = new();
    private Cursor? _hand;
    private ProfileTextLink? _pressedLink;
    private int _pressX, _pressY, _revision;
    private bool _stopped, _connected;
    private int _characters;
    private long _nextLinkTag;

    public event Action<ProfileTextLink>? ProfileLinkActivated;

    public ChatHistoryView(AccountSession session, bool followEnd = true, bool profileLinks = false)
    {
        _session = session;
        _followEnd = followEnd;
        _profileLinks = profileLinks;
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
        var line = new HistoryLine(ChatMemoryLimits.LimitMessage(text), true);
        _lines.Enqueue(line);
        _characters += line.Text.Length;
        InsertText(line);
        var removedCharacters = 0;
        while (_lines.Count > ChatMemoryLimits.Messages || _characters > ChatMemoryLimits.Characters)
        {
            var removed = _lines.Dequeue();
            _characters -= removed.Text.Length;
            removedCharacters += removed.RenderedCharacters;
            foreach (var link in removed.Links)
            {
                if (--_linkUses[link] != 0) continue;
                var tag = _links[link];
                Buffer.TagTable.Remove(tag);
                tag.Dispose();
                _links.Remove(link); _linkUses.Remove(link);
                if (link.AvatarId != UUID.Zero && !_links.Keys.Any(other => other.AvatarId == link.AvatarId))
                    _linkNames.Remove(link.AvatarId);
            }
        }
        if (removedCharacters == 0) return;
        _revision++;
        _pressedLink = null;
        var start = Buffer.StartIter;
        var end = Buffer.GetIterAtOffset(removedCharacters);
        Buffer.Delete(ref start, ref end);
    }

    public void SetText(string text)
    {
        if (_stopped) return;
        Clear();
        var line = new HistoryLine(text, false);
        _lines.Enqueue(line);
        _characters = text.Length;
        InsertText(line);
        Buffer.PlaceCursor(Buffer.StartIter);
        ScrollToIter(Buffer.StartIter, 0, false, 0, 0);
    }

    public void Clear()
    {
        if (_stopped) return;
        _lines.Clear();
        _characters = 0;
        ClearBuffer();
    }

    private void ClearBuffer()
    {
        _revision++;
        _pressedLink = null;
        Buffer.Text = string.Empty;
        foreach (var tag in _links.Values)
        {
            Buffer.TagTable.Remove(tag);
            tag.Dispose();
        }
        _links.Clear();
        _linkUses.Clear();
        _linkNames.Clear();
    }

    private void InsertText(HistoryLine line)
    {
        var firstOffset = Buffer.CharCount;
        line.Links.Clear();
        foreach (var span in _profileLinks ? _session.FormatProfileText(line.Text) : _session.FormatChatText(line.Text))
        {
            var offset = Buffer.CharCount;
            var end = Buffer.EndIter;
            Buffer.Insert(ref end, span.Text);
            var start = Buffer.GetIterAtOffset(offset);
            // Text inserted at a tag boundary can inherit the preceding avatar's tag.
            Buffer.RemoveAllTags(start, end);
            var link = span.Link ?? (span.AvatarId != UUID.Zero ? ProfileTextLink.Avatar(span.AvatarId) : null);
            if (link != null)
            {
                if (!_links.TryGetValue(link, out var tag))
                {
                    tag = new TextTag(span.Link == null ? "avatar-" + span.AvatarId : "link-" + _nextLinkTag++)
                        { Underline = Pango.Underline.Single };
                    tag.ForegroundRgba = StyleContext.GetColor(StateFlags.Link);
                    Buffer.TagTable.Add(tag);
                    _links.Add(link, tag);
                }
                Buffer.ApplyTag(tag, start, end);
                line.Links.Add(link);
                if (span.AvatarId != UUID.Zero) _linkNames[span.AvatarId] = span.Text;
            }
        }
        foreach (var link in line.Links) _linkUses[link] = _linkUses.GetValueOrDefault(link) + 1;
        if (line.NewLine)
        {
            var lastOffset = Buffer.CharCount;
            var last = Buffer.EndIter;
            Buffer.Insert(ref last, Environment.NewLine);
            Buffer.RemoveAllTags(Buffer.GetIterAtOffset(lastOffset), last);
        }
        line.RenderedCharacters = Buffer.CharCount - firstOffset;
    }

    private void OnNamesChanged(AccountSession account)
    {
        if (_profileLinks || _linkNames.Any(link => link.Value != _session.DisplayChatAvatarName(link.Key) || !_session.CanViewAvatarProfile(link.Key)))
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
        foreach (var line in _lines) InsertText(line);
        if (selected) Buffer.SelectRange(Buffer.GetIterAtOffset(Math.Min(startOffset, Buffer.CharCount)),
            Buffer.GetIterAtOffset(Math.Min(endOffset, Buffer.CharCount)));
        var revision = _revision;
        GtkDispatch.Post(() =>
        {
            if (_stopped || _revision != revision) return;
            if (_followEnd && bottom) ScrollToIter(Buffer.EndIter, 0, false, 0, 1);
            else if (adjustment != null) adjustment.Value = scroll;
        });
    }

    private ProfileTextLink? LinkAt(Event evnt, double x, double y)
    {
        if (_stopped || evnt.Window == null) return null;
        WindowToBufferCoords(GetWindowType(evnt.Window), (int)x, (int)y, out var bx, out var by);
        if (!GetIterAtLocation(out var iter, bx, by)) return null;
        foreach (var (link, tag) in _links)
            if (iter.HasTag(tag) && _session.CanUseProfileLink(link)) return link;
        return null;
    }

    protected override bool OnButtonPressEvent(EventButton evnt)
    {
        _pressedLink = evnt.Button == 1 && evnt.Type == EventType.ButtonPress ? LinkAt(evnt, evnt.X, evnt.Y) : null;
        _pressX = (int)evnt.X; _pressY = (int)evnt.Y;
        return base.OnButtonPressEvent(evnt);
    }

    protected override bool OnButtonReleaseEvent(EventButton evnt)
    {
        var handled = base.OnButtonReleaseEvent(evnt);
        var link = _pressedLink;
        _pressedLink = null;
        if (evnt.Button == 1 && link != null && link == LinkAt(evnt, evnt.X, evnt.Y) && !Buffer.HasSelection &&
            !global::Gtk.Drag.CheckThreshold(this, _pressX, _pressY, (int)evnt.X, (int)evnt.Y))
        {
            if (link.Action == ProfileLinkAction.AvatarProfile) _session.OpenAvatarProfile(link.AvatarId);
            else ProfileLinkActivated?.Invoke(link);
            return true;
        }
        return handled;
    }

    protected override bool OnMotionNotifyEvent(EventMotion evnt)
    {
        var window = GetWindow(TextWindowType.Text);
        if (window != null)
        {
            var link = LinkAt(evnt, evnt.X, evnt.Y) != null;
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
        Clear();
        _stopped = true;
        _session.AvatarNamesChanged -= OnNamesChanged;
        _session.FriendsChanged -= OnNamesChanged;
        _session.StateChanged -= OnStateChanged;
        _session.Rlv.Changed -= RefreshText;
        _hand?.Dispose();
        _hand = null;
    }

    private sealed class HistoryLine(string text, bool newLine)
    {
        public string Text { get; } = text;
        public bool NewLine { get; } = newLine;
        public int RenderedCharacters;
        public HashSet<ProfileTextLink> Links { get; } = new();
    }
}
