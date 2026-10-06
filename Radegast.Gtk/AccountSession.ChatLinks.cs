using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    public event Action<AccountSession>? AvatarNamesChanged;
    public event Action<AccountSession, UUID>? AvatarProfileRequested;

    public bool CanShowAvatarName(UUID id) => id == Client.Self.AgentID || !Rlv.Enabled ||
        Rlv.Service.Permissions.CanShowNames(id.Guid);

    public bool CanViewAvatarProfile(UUID id) => !_disposed && id != UUID.Zero && CanShowAvatarName(id);

    public void OpenAvatarProfile(UUID id)
    {
        if (CanViewAvatarProfile(id)) AvatarProfileRequested?.Invoke(this, id);
    }

    public string DisplayChatAvatarName(UUID id)
    {
        if (!CanShowAvatarName(id)) return "Resident";
        if (id == Client.Self.AgentID) return Name;
        lock (_nameLock)
        {
            if (Client.Friends.FriendList.TryGetValue(id, out var friend) && !string.IsNullOrWhiteSpace(friend.Name))
                _names[id] = friend.Name;
            return _names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name : IsConnected ? "Loading name…" : "Resident";
        }
    }

    private void RememberAvatarName(UUID id, string name)
    {
        lock (_nameLock)
        {
            if (_names.GetValueOrDefault(id) == name) return;
            _names[id] = name;
        }
        AvatarNamesChanged?.Invoke(this);
    }

    public void RequestChatAvatarNames(IEnumerable<UUID> avatars)
    {
        if (_disposed || !IsConnected) return;
        var missing = new List<UUID>();
        lock (_nameLock)
            foreach (var id in avatars.Distinct())
                if (id != UUID.Zero && id != Client.Self.AgentID && CanShowAvatarName(id) &&
                    (!_names.TryGetValue(id, out var name) || string.IsNullOrWhiteSpace(name)) &&
                    _requestedNames.Add(id)) missing.Add(id);
        if (missing.Count > 0) Client.Avatars.RequestAvatarNames(missing);
    }

    public IReadOnlyList<ChatTextSpan> FormatChatText(string text)
    {
        var parsed = AvatarProfileLinks.Parse(text);
        // Prime cached friend names before redacting the surrounding plain text.
        foreach (var span in parsed.Where(span => span.AvatarId != UUID.Zero)) DisplayChatAvatarName(span.AvatarId);
        var spans = parsed.Select(span => span.AvatarId == UUID.Zero
            ? new ChatTextSpan(RedactText(span.Text))
            : new ChatTextSpan(DisplayChatAvatarName(span.AvatarId), CanViewAvatarProfile(span.AvatarId) ? span.AvatarId : UUID.Zero)).ToArray();
        RequestChatAvatarNames(parsed.Select(span => span.AvatarId));
        return spans;
    }
}
