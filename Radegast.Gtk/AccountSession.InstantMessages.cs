using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed record PrivateMessage(DateTime Timestamp, string Text, bool Outgoing);

internal sealed class ImConversation(UUID peerId, UUID sessionId)
{
    private readonly List<PrivateMessage> _messages = new();
    public UUID PeerId { get; } = peerId;
    public UUID SessionId { get; } = sessionId;
    public IReadOnlyList<PrivateMessage> Messages => _messages;
    public string Draft { get; set; } = string.Empty;
    public int UnreadCount { get; internal set; }
    internal void Append(PrivateMessage message) => _messages.Add(message);
}

internal sealed partial class AccountSession
{
    private readonly Dictionary<UUID, ImConversation> _conversations = new();
    public IReadOnlyList<ImConversation> Conversations => _conversations.Values.ToArray();
    public int UnreadInstantMessages => _conversations.Values.Sum(conversation => conversation.UnreadCount);
    public event Action<AccountSession, ImConversation>? ConversationChanged;

    public string DisplayConversationName(UUID peerId) => DisplayFriendName(peerId);

    public bool CanOpenConversation(UUID peerId) => !_disposed && IsConnected && peerId != UUID.Zero &&
        peerId != Client.Self.AgentID && (_conversations.ContainsKey(peerId) || !Rlv.Enabled ||
            Rlv.Service.Permissions.CanStartIM(peerId.Guid));

    public bool CanSendInstantMessage(UUID peerId, string message) => !_disposed && IsConnected &&
        Client.Network.CurrentSim != null && peerId != UUID.Zero && peerId != Client.Self.AgentID &&
        !string.IsNullOrWhiteSpace(message) && (_conversations.ContainsKey(peerId) || !Rlv.Enabled ||
            Rlv.Service.Permissions.CanStartIM(peerId.Guid)) &&
        (!Rlv.Enabled || Rlv.Service.Permissions.CanSendIM(message, peerId.Guid));

    public ImConversation OpenConversation(UUID peerId)
    {
        if (peerId == UUID.Zero || peerId == Client.Self.AgentID)
            throw new ArgumentOutOfRangeException(nameof(peerId), "Choose another resident to message.");
        if (!CanOpenConversation(peerId))
            throw new InvalidOperationException(IsConnected ? "Starting this conversation is restricted by RLV." : "This account is disconnected.");
        var conversation = GetConversation(peerId);
        ConversationChanged?.Invoke(this, conversation);
        return conversation;
    }

    private ImConversation GetConversation(UUID peerId)
    {
        if (!_conversations.TryGetValue(peerId, out var conversation))
        {
            // Second Life direct IM sessions use the XOR of the two avatar IDs.
            conversation = new ImConversation(peerId, peerId ^ Client.Self.AgentID);
            _conversations.Add(peerId, conversation);
        }
        return conversation;
    }

    public void SendInstantMessage(UUID peerId, string message)
    {
        if (!IsConnected || Client.Network.CurrentSim == null)
            throw new InvalidOperationException("This account is disconnected.");
        if (peerId == UUID.Zero || peerId == Client.Self.AgentID || string.IsNullOrWhiteSpace(message))
            throw new ArgumentOutOfRangeException(nameof(peerId), "Choose a resident and enter a message.");
        if (!CanSendInstantMessage(peerId, message))
            throw new InvalidOperationException("Sending this instant message is restricted by RLV.");
        var conversation = GetConversation(peerId);
        // The default avatar IM API requests offline delivery as well as online delivery.
        // Chat channel prefixes are deliberately left as literal private-message text.
        Client.Self.InstantMessage(peerId, message, conversation.SessionId);
        conversation.Append(new PrivateMessage(DateTime.Now, message, true));
        conversation.Draft = string.Empty;
        ConversationChanged?.Invoke(this, conversation);
    }

    public void MarkConversationRead(UUID peerId)
    {
        if (!_conversations.TryGetValue(peerId, out var conversation) || conversation.UnreadCount == 0) return;
        conversation.UnreadCount = 0;
        ConversationChanged?.Invoke(this, conversation);
    }

    private void ReceivePrivateInstantMessage(InstantMessage message)
    {
        // Script menus, inventory offers, typing events and group/conference sessions
        // share this protocol event but are not resident-to-resident messages.
        if (message.Dialog is not (InstantMessageDialog.MessageFromAgent or InstantMessageDialog.BusyAutoResponse) ||
            message.GroupIM || Client.Self.IsGroupMessage(message) || message.BinaryBucket is { Length: > 1 } ||
            message.FromAgentID == UUID.Zero || message.FromAgentID == Client.Self.AgentID ||
            (message.ToAgentID != UUID.Zero && message.ToAgentID != Client.Self.AgentID) ||
            string.IsNullOrEmpty(message.Message)) return;
        _post(() =>
        {
            if (_disposed || (Rlv.Enabled && !Rlv.Service.Permissions.CanReceiveIM(message.Message, message.FromAgentID.Guid))) return;
            if (!string.IsNullOrWhiteSpace(message.FromAgentName))
                lock (_nameLock) _names[message.FromAgentID] = message.FromAgentName;
            var conversation = GetConversation(message.FromAgentID);
            var timestamp = message.Timestamp;
            // LibreMetaverse 3.1.5's UDP handler constructs DateTime from Unix seconds
            // as ticks. Accept already decoded dates as well.
            if (timestamp.Year < 2000)
                timestamp = timestamp.Ticks is > 0 and <= uint.MaxValue
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Ticks).LocalDateTime : DateTime.Now;
            else if (timestamp.Kind == DateTimeKind.Utc) timestamp = timestamp.ToLocalTime();
            conversation.Append(new PrivateMessage(timestamp, message.Message, false));
            conversation.UnreadCount++;
            ConversationChanged?.Invoke(this, conversation);
        });
    }

    private async Task RetrieveOfflineInstantMessagesAsync()
    {
        try { await Client.Self.RetrieveInstantMessagesAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _post(() => { if (!_disposed) OnRlvMessage($"Could not retrieve offline instant messages: {ex.Message}"); });
        }
    }
}
