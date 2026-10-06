using LibreMetaverse;
using System.Globalization;

namespace Radegast.Gtk;

internal sealed record FriendResident(UUID Id, string Name, bool IsOnline);

internal sealed partial class AccountSession
{
    private readonly HashSet<UUID> _requestedFriendNames = new();
    private readonly Dictionary<UUID, bool> _friendPresence = new();
    private bool _friendPresenceReady;

    public event Action<AccountSession>? FriendsChanged;

    public IReadOnlyList<FriendResident> Friends
    {
        get
        {
            var friends = Client.Friends.FriendList.Values
                .Select(friend => new FriendResident(friend.UUID, FriendName(friend.UUID), friend.IsOnline)).ToList();
            return friends.OrderByDescending(friend => friend.IsOnline)
                .ThenBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(friend => friend.Id.ToString(), StringComparer.Ordinal).ToArray();
        }
    }

    private string FriendName(UUID id)
    {
        lock (_nameLock)
        {
            if (Client.Friends.FriendList.TryGetValue(id, out var friend) && !string.IsNullOrWhiteSpace(friend.Name))
                _names[id] = friend.Name;
            return _names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name : $"Resident {id.ToString()[..8]}";
        }
    }

    public string DisplayFriendName(UUID id) => !Rlv.Enabled || Rlv.Service.Permissions.CanShowNames(id.Guid)
        ? FriendName(id) : "Resident";

    public void RequestFriendNames()
    {
        if (_disposed || !IsConnected) return;
        var missing = new List<UUID>();
        lock (_nameLock)
            foreach (var friend in Client.Friends.FriendList.Values)
                if (string.IsNullOrWhiteSpace(friend.Name) &&
                    (!_names.TryGetValue(friend.UUID, out var cached) || string.IsNullOrWhiteSpace(cached)) &&
                    _requestedFriendNames.Add(friend.UUID)) missing.Add(friend.UUID);
        if (missing.Count > 0) Client.Avatars.RequestAvatarNames(missing);
    }

    public bool CanPayFriend(UUID id) => !_disposed && IsConnected && Client.Network.CurrentSim != null &&
        id != UUID.Zero && id != Client.Self.AgentID && Client.Friends.FriendList.ContainsKey(id);

    public bool CanOfferFriendTeleport(UUID id) => CanPayFriend(id) &&
        (!Rlv.Enabled || Rlv.Service.Permissions.CanShowLoc() ||
         (Client.Friends.FriendList.TryGetValue(id, out var friend) && friend.CanSeeMeOnMap));

    public static bool TryParsePaymentAmount(string text, out int amount)
    {
        amount = 0;
        return !string.IsNullOrEmpty(text) && text.All(character => character is >= '0' and <= '9') &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount > 0;
    }

    public void PayFriend(UUID id, int amount)
    {
        RequireFriend(id);
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Enter a positive whole amount in L$.");
        if (Balance is { } balance && amount > balance)
            throw new InvalidOperationException("This amount exceeds your current balance.");
        Client.Self.GiveAvatarMoney(id, amount);
    }

    public void OfferFriendTeleport(UUID id)
    {
        RequireFriend(id);
        if (!CanOfferFriendTeleport(id)) throw new InvalidOperationException("Offering a teleport to this friend is restricted by RLV.");
        // The simulator supplies this account's current location in the offer.
        // A generic message also avoids exposing a location hidden by RLV.
        Client.Self.SendTeleportLure(id, "Join me!");
    }

    private void RequireFriend(UUID id)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim == null)
            throw new InvalidOperationException("This account is disconnected.");
        if (id == UUID.Zero || id == Client.Self.AgentID || !Client.Friends.FriendList.ContainsKey(id))
            throw new InvalidOperationException("This resident is no longer in this account's friend list.");
    }

    private void InitializeFriends()
    {
        Client.Friends.friendsListReady += OnFriendsReady;
        Client.Friends.FriendOnline += OnFriendOnline;
        Client.Friends.FriendOffline += OnFriendOffline;
        Client.Friends.FriendRightsUpdate += OnFriendChanged;
        Client.Friends.FriendNames += OnFriendNames;
        Client.Friends.FriendshipResponse += OnFriendshipResponse;
        Client.Friends.FriendshipTerminated += OnFriendshipTerminated;
        Client.Self.MoneyBalanceReply += OnFriendPaymentReply;
    }

    private void NotifyFriendsChanged() => _post(() => { if (!_disposed) FriendsChanged?.Invoke(this); });
    private void OnFriendsReady(object? sender, FriendsReadyEventArgs e)
    {
        var statuses = Client.Friends.FriendList.Values.Select(friend => (friend.UUID, friend.IsOnline)).ToArray();
        _post(() =>
        {
            if (_disposed) return;
            _friendPresence.Clear();
            foreach (var (id, online) in statuses) _friendPresence[id] = online;
            _friendPresenceReady = true;
            FriendsChanged?.Invoke(this);
        });
    }
    private void OnFriendOnline(object? sender, FriendInfoEventArgs e) => OnFriendPresence(e.Friend.UUID, true);
    private void OnFriendOffline(object? sender, FriendInfoEventArgs e) => OnFriendPresence(e.Friend.UUID, false);
    private void OnFriendPresence(UUID id, bool online) => _post(() =>
    {
        if (_disposed) return;
        var known = _friendPresence.TryGetValue(id, out var previous);
        _friendPresence[id] = online;
        FriendsChanged?.Invoke(this);
        if (_friendPresenceReady && known && previous != online && IsConnected)
            Notify(NotificationCategory.Friends, $"{DisplayFriendName(id)} is {(online ? "online" : "offline")}",
                string.Empty, id);
    });
    private void OnFriendChanged(object? sender, FriendInfoEventArgs e) => NotifyFriendsChanged();
    private void OnFriendNames(object? sender, FriendNamesEventArgs e) => NotifyFriendsChanged();
    private void OnFriendshipResponse(object? sender, FriendshipResponseEventArgs e) => NotifyFriendsChanged();
    private void OnFriendshipTerminated(object? sender, FriendshipTerminatedEventArgs e) => NotifyFriendsChanged();

    private void OnFriendPaymentReply(object? sender, MoneyBalanceReplyEventArgs e)
    {
        var transaction = e.TransactionInfo;
        var ownGift = transaction.SourceID == Client.Self.AgentID && !transaction.IsSourceGroup &&
            !transaction.IsDestGroup && transaction.TransactionType == (int)MoneyTransactionType.Gift;
        if (!ownGift && (e.Success || e.TransactionID == UUID.Zero || string.IsNullOrWhiteSpace(e.Description))) return;
        var success = e.Success;
        var description = e.Description;
        var amount = transaction.Amount;
        var recipient = transaction.DestID;
        _post(() =>
        {
            if (_disposed) return;
            var message = success
                ? $"Payment confirmed: {amount.ToString("N0", CultureInfo.InvariantCulture)} L$ to {DisplayFriendName(recipient)}."
                : $"Payment failed: {(string.IsNullOrWhiteSpace(description) ? "The server rejected the payment." : RedactText(description))}";
            ChatLine?.Invoke(this, $"[{DateTime.Now:HH:mm}] {message}");
        });
    }

    private void StopFriends()
    {
        Client.Friends.friendsListReady -= OnFriendsReady;
        Client.Friends.FriendOnline -= OnFriendOnline;
        Client.Friends.FriendOffline -= OnFriendOffline;
        Client.Friends.FriendRightsUpdate -= OnFriendChanged;
        Client.Friends.FriendNames -= OnFriendNames;
        Client.Friends.FriendshipResponse -= OnFriendshipResponse;
        Client.Friends.FriendshipTerminated -= OnFriendshipTerminated;
        Client.Self.MoneyBalanceReply -= OnFriendPaymentReply;
    }
}
