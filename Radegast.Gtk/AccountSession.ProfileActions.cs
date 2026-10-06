using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private const string FriendshipOfferMessage = "Would you like to be friends?";
    private readonly HashSet<UUID> _pendingFriendshipOffers = new();
    private readonly Dictionary<UUID, (bool Blocked, string Name)> _residentBlockChanges = new();

    public event Action<AccountSession, UUID>? InstantMessagesRequested;
    public event Action<AccountSession>? BlockListChanged;

    public bool CanInteractWithResident(UUID id) => !_disposed && IsConnected && Client.Network.CurrentSim != null &&
        id != UUID.Zero && id != Client.Self.AgentID;

    public bool IsFriend(UUID id) => Client.Friends.FriendList.ContainsKey(id);
    public bool IsFriendshipOfferPending(UUID id) => _pendingFriendshipOffers.Contains(id);

    public bool CanPayResident(UUID id) => CanInteractWithResident(id);
    public bool CanOfferTeleport(UUID id) => CanInteractWithResident(id) &&
        (!Rlv.Enabled || Rlv.Service.Permissions.CanShowLoc() ||
         (Client.Friends.FriendList.TryGetValue(id, out var friend) && friend.CanSeeMeOnMap));

    public void RequestInstantMessages(UUID id)
    {
        if (!CanOpenConversation(id))
            throw new InvalidOperationException(IsConnected ? "Starting this conversation is restricted by RLV." : "This account is disconnected.");
        InstantMessagesRequested?.Invoke(this, id);
    }

    public void PayResident(UUID id, int amount)
    {
        RequireResident(id);
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Enter a positive whole amount in L$.");
        if (Balance is { } balance && amount > balance)
            throw new InvalidOperationException("This amount exceeds your current balance.");
        Client.Self.GiveAvatarMoney(id, amount);
    }

    public void OfferTeleport(UUID id)
    {
        RequireResident(id);
        if (!CanOfferTeleport(id)) throw new InvalidOperationException("Offering a teleport is restricted by RLV.");
        // The simulator supplies this account's location. Avoid embedding a
        // location hidden by RLV in the invitation text.
        Client.Self.SendTeleportLure(id, "Join me!");
    }

    public bool CanOfferFriendship(UUID id) => CanInteractWithResident(id) && !IsFriend(id) &&
        !IsFriendshipOfferPending(id) && !IsResidentBlocked(id) && (!Rlv.Enabled ||
        (Rlv.Service.Permissions.CanStartIM(id.Guid) && Rlv.Service.Permissions.CanSendIM(FriendshipOfferMessage, id.Guid)));

    public void OfferFriendship(UUID id)
    {
        RequireResident(id);
        if (!CanOfferFriendship(id)) throw new InvalidOperationException("A friendship offer is already pending, or contacting this resident is restricted.");
        Client.Friends.OfferFriendship(id, FriendshipOfferMessage);
        _pendingFriendshipOffers.Add(id);
        FriendsChanged?.Invoke(this);
    }

    public void RemoveFriend(UUID id)
    {
        RequireFriend(id);
        Client.Friends.TerminateFriendship(id);
        _pendingFriendshipOffers.Remove(id);
        // LibreMetaverse removes the friend locally without raising its event.
        FriendsChanged?.Invoke(this);
    }

    public bool IsResidentBlocked(UUID id) => id != UUID.Zero && id != Client.Self.AgentID &&
        (_residentBlockChanges.TryGetValue(id, out var change) ? change.Blocked :
            Client.Self.MuteList.Values.Any(entry => entry.Type == MuteType.Resident && entry.ID == id &&
                (entry.Flags & MuteFlags.TextChat) == 0));

    public void BlockResident(UUID id)
    {
        RequireResident(id);
        if (IsResidentBlocked(id)) return;
        // Store the actual name, not its RLV-redacted display label. UUID remains
        // authoritative when the name has not arrived yet.
        var name = FriendName(id);
        var hadPrevious = _residentBlockChanges.TryGetValue(id, out var previous);
        _residentBlockChanges[id] = (true, name);
        try { Client.Self.UpdateMuteListEntry(MuteType.Resident, id, name, MuteFlags.Default); }
        catch
        {
            if (hadPrevious) _residentBlockChanges[id] = previous;
            else _residentBlockChanges.Remove(id);
            throw;
        }
        foreach (var key in _teleportOffers.Keys.Where(key => key.Sender == id).ToArray()) _teleportOffers.Remove(key);
        TeleportOffersChanged?.Invoke(this);
    }

    public void UnblockResident(UUID id)
    {
        RequireResident(id);
        // The grid mute list can contain multiple names for the same resident.
        var names = Client.Self.MuteList.Values.Where(entry => entry.Type == MuteType.Resident && entry.ID == id)
            .Select(entry => entry.Name).ToList();
        var hadPrevious = _residentBlockChanges.TryGetValue(id, out var change);
        if (hadPrevious) names.Add(change.Name);
        _residentBlockChanges[id] = (false, change.Name ?? FriendName(id));
        try { foreach (var name in names.Distinct()) Client.Self.RemoveMuteListEntry(id, name); }
        catch
        {
            if (hadPrevious) _residentBlockChanges[id] = change;
            else _residentBlockChanges.Remove(id);
            throw;
        }
        BlockListChanged?.Invoke(this);
    }

    private void RequireResident(UUID id)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim == null)
            throw new InvalidOperationException("This account is disconnected.");
        if (id == UUID.Zero || id == Client.Self.AgentID)
            throw new ArgumentOutOfRangeException(nameof(id), "Choose another resident.");
    }

    private void OnBlockListUpdated(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed) return;
        // The asynchronous login download can contain a snapshot taken before
        // a local Block/Unblock. Keep this login's explicit changes authoritative.
        foreach (var (id, change) in _residentBlockChanges)
        {
            foreach (var entry in Client.Self.MuteList.Where(pair => pair.Value.Type == MuteType.Resident && pair.Value.ID == id).ToArray())
                Client.Self.MuteList.TryRemove(entry.Key, out _);
            if (change.Blocked)
                Client.Self.MuteList[$"{id}|{change.Name}"] = new MuteEntry
                { ID = id, Name = change.Name, Type = MuteType.Resident, Flags = MuteFlags.Default };
        }
        BlockListChanged?.Invoke(this);
    });
}
