using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed record TeleportOffer(UUID SenderId, UUID LureId);

internal sealed partial class AccountSession
{
    private readonly Dictionary<(UUID Sender, UUID Lure), TeleportOffer> _teleportOffers = new();
    private readonly HashSet<(UUID Sender, UUID Lure)> _seenTeleportOffers = new();
    private readonly Queue<(UUID Sender, UUID Lure)> _recentTeleportOffers = new();

    public event Action<AccountSession, TeleportOffer>? TeleportOfferReceived;
    public event Action<AccountSession>? TeleportOffersChanged;

    public bool IsTeleportOfferPending(TeleportOffer offer) =>
        _teleportOffers.TryGetValue((offer.SenderId, offer.LureId), out var pending) && ReferenceEquals(pending, offer);

    public string? TeleportOfferError(TeleportOffer offer)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim == null) return "This account is disconnected.";
        if (!IsTeleportOfferPending(offer)) return "This teleport offer is no longer pending.";
        if (IsResidentBlocked(offer.SenderId)) return "This resident is blocked.";
        if (Rlv.Enabled)
        {
            var permissions = Rlv.Service.Permissions;
            if (!permissions.CanTPLure(offer.SenderId.Guid)) return "Accepting this teleport offer is restricted by RLV.";
            if (IsSitting && !permissions.CanUnsit()) return "Leaving the current seat is restricted by RLV.";
        }
        return null;
    }

    public void RespondToTeleportOffer(TeleportOffer offer, bool accept)
    {
        if (_disposed || !IsConnected || Client.Network.CurrentSim == null)
            throw new InvalidOperationException("This account is disconnected.");
        if (!IsTeleportOfferPending(offer)) throw new InvalidOperationException("This teleport offer is no longer pending.");
        if (accept && TeleportOfferError(offer) is { } error) throw new InvalidOperationException(error);
        Client.Self.TeleportLureRespond(offer.SenderId, offer.LureId, accept);
        _teleportOffers.Remove((offer.SenderId, offer.LureId));
        TeleportOffersChanged?.Invoke(this);
    }

    private void ReceiveTeleportOffer(InstantMessage message)
    {
        if (message.Dialog != InstantMessageDialog.RequestTeleport || message.GroupIM ||
            message.FromAgentID == UUID.Zero || message.FromAgentID == Client.Self.AgentID || message.IMSessionID == UUID.Zero ||
            (message.ToAgentID != UUID.Zero && message.ToAgentID != Client.Self.AgentID)) return;
        _post(() =>
        {
            if (_disposed || !IsConnected || Client.Network.CurrentSim == null || IsResidentBlocked(message.FromAgentID)) return;
            var key = (message.FromAgentID, message.IMSessionID);
            if (_teleportOffers.ContainsKey(key) || !_seenTeleportOffers.Add(key)) return;
            _recentTeleportOffers.Enqueue(key);
            while (_recentTeleportOffers.Count > 256) _seenTeleportOffers.Remove(_recentTeleportOffers.Dequeue());
            if (!string.IsNullOrWhiteSpace(message.FromAgentName))
                lock (_nameLock) _names[message.FromAgentID] = message.FromAgentName;
            var permissions = Rlv.Service.Permissions;
            if (Rlv.Enabled && !permissions.CanTPLure(message.FromAgentID.Guid))
            {
                Client.Self.TeleportLureRespond(message.FromAgentID, message.IMSessionID, false);
                return;
            }
            if (Rlv.Enabled && permissions.IsAutoAcceptTp(message.FromAgentID.Guid) && (!IsSitting || permissions.CanUnsit()))
                Client.Self.TeleportLureRespond(message.FromAgentID, message.IMSessionID, true);
            else
            {
                var offer = new TeleportOffer(message.FromAgentID, message.IMSessionID);
                _teleportOffers.Add(key, offer);
                TeleportOfferReceived?.Invoke(this, offer);
            }
            Notify(NotificationCategory.TeleportOffers, DisplayFriendName(message.FromAgentID), "Teleport offer", message.FromAgentID);
        });
    }

    private void ResetTeleportOffers()
    {
        _teleportOffers.Clear();
        _seenTeleportOffers.Clear();
        _recentTeleportOffers.Clear();
        TeleportOffersChanged?.Invoke(this);
    }
}
