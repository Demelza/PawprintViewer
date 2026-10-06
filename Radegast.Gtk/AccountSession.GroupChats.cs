using LibreMetaverse;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Messages.Linden;
using System.Text;

namespace Radegast.Gtk;

internal enum GroupChatState { NotJoined, Joining, Joined, Failed }

internal sealed class GroupConversation(UUID groupId, string name) : ChatConversation(groupId)
{
    public string Name { get; internal set; } = name;
    public UUID SessionId { get; internal set; } = groupId;
    public GroupChatState State { get; internal set; }
    public string Error { get; internal set; } = string.Empty;
    internal int JoinAttempt { get; set; }
    internal string LastSentMessage { get; set; } = string.Empty;
    internal List<(string Text, DateTime Expires)> PendingEchoes { get; } = new();
}

internal sealed partial class AccountSession
{
    private readonly Dictionary<UUID, string> _groups = new();
    private readonly Dictionary<UUID, GroupConversation> _groupChats = new();
    private readonly Queue<InstantMessage> _pendingGroupMessages = new();
    private readonly CancellationTokenSource _groupChatStop = new();
    private bool _groupsRequested;
    public bool GroupsLoaded { get; private set; }
    public IReadOnlyList<GroupConversation> GroupConversations => _groupChats.Values
        .Where(chat => _groups.ContainsKey(chat.Id) || chat.Messages.Count > 0)
        .OrderBy(chat => chat.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(chat => chat.Id.ToString()).ToArray();
    public int UnreadGroupMessages => _groupChats.Values.Sum(chat => chat.UnreadCount);
    public event Action<AccountSession>? GroupsChanged;
    public event Action<AccountSession, GroupConversation>? GroupConversationChanged;

    private void InitializeGroupChats()
    {
        Client.Groups.CurrentGroups += OnCurrentGroups;
        Client.Groups.GroupDropped += OnGroupDropped;
        Client.Self.GroupChatJoined += OnGroupChatJoined;
        Client.Self.ChatSessionMemberLeft += OnGroupChatMemberLeft;
        Client.Network.EventQueueRunning += OnGroupEventQueueRunning;
        Client.Network.RegisterEventCallback("ChatterBoxSessionEventReply", OnGroupChatSendReply);
    }

    public void RequestGroups(bool refresh = false)
    {
        if (_disposed || !IsConnected || (!refresh && (_groupsRequested || GroupsLoaded))) return;
        _groupsRequested = true;
        Client.Groups.RequestCurrentGroups();
    }

    private void OnGroupEventQueueRunning(object? sender, EventQueueRunningEventArgs e)
    {
        if (e.Simulator != Client.Network.CurrentSim) return;
        _post(() => { if (!GroupsLoaded) RequestGroups(true); });
    }

    private void OnCurrentGroups(object? sender, CurrentGroupsEventArgs e)
    {
        var groups = e.Groups.Select(pair => (pair.Key, pair.Value.Name)).ToArray();
        _post(() =>
        {
            if (_disposed || !IsConnected) return;
            _groups.Clear();
            foreach (var (id, name) in groups)
            {
                if (id == UUID.Zero) continue;
                _groups[id] = name;
                if (!_groupChats.TryGetValue(id, out var chat)) _groupChats.Add(id, new GroupConversation(id, name));
                else chat.Name = name;
            }
            foreach (var chat in _groupChats.Values.Where(chat => !_groups.ContainsKey(chat.Id)))
            {
                chat.State = GroupChatState.NotJoined;
                chat.JoinAttempt++;
                chat.PendingEchoes.Clear();
            }
            GroupsLoaded = true;
            _groupsRequested = false;
            GroupsChanged?.Invoke(this);
            while (_pendingGroupMessages.TryDequeue(out var message)) ReceiveGroupMessageOnUi(message);
        });
    }

    public string DisplayGroupName(UUID groupId) => RedactText(_groupChats.TryGetValue(groupId, out var chat)
        && !string.IsNullOrWhiteSpace(chat.Name) ? chat.Name : $"Group {groupId.ToString()[..8]}");

    public string DisplayGroupSender(ChatMessage message) => message.Outgoing ? Name :
        Rlv.Enabled && !Rlv.Service.Permissions.CanShowNames(message.SenderId.Guid) ? "Resident" :
        string.IsNullOrWhiteSpace(message.SenderName) ? DisplayFriendName(message.SenderId) : message.SenderName;

    public bool CanOpenGroupChat(UUID groupId) => !_disposed && IsConnected && _groups.ContainsKey(groupId) &&
        (_groupChats[groupId].State is GroupChatState.Joined or GroupChatState.Joining ||
         _groupChats[groupId].Messages.Count > 0 || !Rlv.Enabled || Rlv.Service.Permissions.CanStartIM(groupId.Guid));

    public GroupConversation OpenGroupChat(UUID groupId)
    {
        if (!CanOpenGroupChat(groupId))
            throw new InvalidOperationException(!IsConnected ? "This account is disconnected." :
                !_groups.ContainsKey(groupId) ? "You do not belong to this group." : "Starting this group chat is restricted by RLV.");
        var chat = _groupChats[groupId];
        if (chat.State is not (GroupChatState.Joined or GroupChatState.Joining))
        {
            chat.State = GroupChatState.Joining;
            chat.Error = string.Empty;
            var attempt = ++chat.JoinAttempt;
            Client.Self.RequestJoinGroupChat(chat.SessionId);
            _ = WaitForGroupJoinAsync(chat, attempt);
        }
        GroupConversationChanged?.Invoke(this, chat);
        return chat;
    }

    private async Task WaitForGroupJoinAsync(GroupConversation chat, int attempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _groupChatStop.Token).ConfigureAwait(false);
            _post(() =>
            {
                if (_disposed || chat.State != GroupChatState.Joining || chat.JoinAttempt != attempt) return;
                chat.State = GroupChatState.Failed;
                chat.Error = "Group chat join timed out. Select the group to retry.";
                GroupConversationChanged?.Invoke(this, chat);
            });
        }
        catch (OperationCanceledException) { }
    }

    public bool CanSendGroupMessage(UUID groupId, string message) => !_disposed && IsConnected &&
        Client.Network.CurrentSim != null && !string.IsNullOrWhiteSpace(message) && _groups.ContainsKey(groupId) &&
        _groupChats[groupId].State == GroupChatState.Joined && Client.Self.GroupChatSessions.ContainsKey(_groupChats[groupId].SessionId) &&
        (!Rlv.Enabled || Rlv.Service.Permissions.CanSendIM(message, groupId.Guid, _groups[groupId]));

    public string GroupChatStatus(UUID groupId, string message)
    {
        if (!IsConnected) return "This account is disconnected.";
        if (!_groups.ContainsKey(groupId)) return "You no longer belong to this group.";
        var chat = _groupChats[groupId];
        if (chat.State == GroupChatState.Joining) return string.IsNullOrEmpty(chat.Error) ? "Joining group chat…" : chat.Error;
        if (chat.State == GroupChatState.Failed) return chat.Error;
        if (chat.State != GroupChatState.Joined || !Client.Self.GroupChatSessions.ContainsKey(chat.SessionId))
            return !CanOpenGroupChat(groupId) ? "Starting this group chat is restricted by RLV." : "Select this group to join its chat.";
        if (Rlv.Enabled && !Rlv.Service.Permissions.CanSendIM(message, groupId.Guid, chat.Name))
            return "Sending messages to this group is restricted by RLV.";
        return chat.Error;
    }

    public void SendGroupMessage(UUID groupId, string message)
    {
        if (!CanSendGroupMessage(groupId, message))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Enter a message." : GroupChatStatus(groupId, message));
        var chat = _groupChats[groupId];
        chat.PendingEchoes.RemoveAll(echo => echo.Expires <= DateTime.UtcNow);
        // Split at Unicode character boundaries before calling the library's group API.
        foreach (var part in GroupMessageParts(message))
        {
            Client.Self.InstantMessageGroup(chat.SessionId, part);
            chat.PendingEchoes.Add((part, DateTime.UtcNow.AddMinutes(2)));
        }
        chat.Append(new ChatMessage(DateTime.Now, message, true, Client.Self.AgentID, Name));
        chat.LastSentMessage = message;
        chat.Draft = string.Empty;
        chat.Error = string.Empty;
        GroupConversationChanged?.Invoke(this, chat);
    }

    private static IEnumerable<string> GroupMessageParts(string message)
    {
        var part = new StringBuilder();
        var bytes = 0;
        foreach (var rune in message.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > AgentManager.MaxChatMessageSize)
            {
                yield return part.ToString();
                part.Clear();
                bytes = 0;
            }
            part.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (part.Length > 0) yield return part.ToString();
    }

    public void MarkGroupChatRead(UUID groupId)
    {
        if (!_groupChats.TryGetValue(groupId, out var chat) || chat.UnreadCount == 0) return;
        chat.UnreadCount = 0;
        GroupConversationChanged?.Invoke(this, chat);
    }

    private void ReceiveGroupInstantMessage(InstantMessage message)
    {
        if (message.Dialog is not (InstantMessageDialog.SessionSend or InstantMessageDialog.MessageFromAgent)) return;
        if (message.IMSessionID == UUID.Zero || string.IsNullOrEmpty(message.Message)) return;
        if (message.ToAgentID != UUID.Zero && message.ToAgentID != Client.Self.AgentID && message.ToAgentID != message.IMSessionID) return;
        _post(() =>
        {
            if (_disposed) return;
            if (!GroupsLoaded)
            {
                if (!message.GroupIM && message.Dialog != InstantMessageDialog.SessionSend) return;
                if (_pendingGroupMessages.Count == 100) _pendingGroupMessages.Dequeue();
                _pendingGroupMessages.Enqueue(message);
                RequestGroups();
                return;
            }
            ReceiveGroupMessageOnUi(message);
        });
    }

    private void ReceiveGroupMessageOnUi(InstantMessage message)
    {
        // Conference chats also use SessionSend. Membership, not the library's
        // shared session cache, determines whether this is a group conversation.
        var chat = FindGroupSession(message.IMSessionID);
        if (chat == null || !_groups.ContainsKey(chat.Id)) return;
        var own = message.FromAgentID == Client.Self.AgentID;
        if (!own && Rlv.Enabled && !Rlv.Service.Permissions.CanReceiveIM(message.Message, message.FromAgentID.Guid, chat.Name)) return;
        chat.State = GroupChatState.Joined;
        chat.Error = string.Empty;
        Client.Self.GroupChatSessions.TryAdd(chat.SessionId, new List<ChatSessionMember>());
        if (own)
        {
            chat.PendingEchoes.RemoveAll(echo => echo.Expires <= DateTime.UtcNow);
            var echo = chat.PendingEchoes.FindIndex(echo => echo.Text == message.Message);
            if (echo >= 0)
            {
                chat.PendingEchoes.RemoveAt(echo);
                GroupConversationChanged?.Invoke(this, chat);
                return;
            }
        }
        else if (!string.IsNullOrWhiteSpace(message.FromAgentName))
            lock (_nameLock) _names[message.FromAgentID] = message.FromAgentName;
        chat.Append(new ChatMessage(ChatConversation.MessageTime(message.Timestamp), message.Message, own,
            message.FromAgentID, message.FromAgentName));
        if (!own) chat.UnreadCount++;
        GroupConversationChanged?.Invoke(this, chat);
        if (!own)
        {
            Notify(NotificationCategory.GroupChats, DisplayGroupName(chat.Id), message.Message, chat.Id);
        }
    }

    private GroupConversation? FindGroupSession(UUID sessionId) => _groupChats.GetValueOrDefault(sessionId)
        ?? _groupChats.Values.FirstOrDefault(chat => chat.SessionId == sessionId);

    private void OnGroupChatJoined(object? sender, GroupChatJoinedEventArgs e) => _post(() =>
    {
        if (_disposed || !IsConnected) return;
        var chat = FindGroupSession(e.SessionID) ?? FindGroupSession(e.TmpSessionID);
        if (chat == null || !_groups.ContainsKey(chat.Id)) return;
        if (e.Success && e.SessionID != UUID.Zero) chat.SessionId = e.SessionID;
        chat.State = e.Success ? GroupChatState.Joined : GroupChatState.Failed;
        chat.Error = e.Success ? string.Empty : "Could not join this group chat. Select the group to retry.";
        GroupConversationChanged?.Invoke(this, chat);
    });

    private void OnGroupChatMemberLeft(object? sender, ChatSessionMemberLeftEventArgs e)
    {
        if (e.AgentID != Client.Self.AgentID) return;
        _post(() =>
        {
            if (_disposed || FindGroupSession(e.SessionID) is not { } chat) return;
            Client.Self.GroupChatSessions.TryRemove(chat.SessionId, out _);
            chat.State = GroupChatState.NotJoined;
            chat.JoinAttempt++;
            GroupConversationChanged?.Invoke(this, chat);
        });
    }

    private void OnGroupChatSendReply(string key, IMessage message, Simulator simulator)
    {
        if (message is not ChatterboxSessionEventReplyMessage { Success: false } reply) return;
        _post(() =>
        {
            if (_disposed || FindGroupSession(reply.SessionID) is not { } chat) return;
            chat.State = GroupChatState.Joining;
            chat.Error = "The server rejected the last message. Rejoining group chat; resend it after joining.";
            if (string.IsNullOrEmpty(chat.Draft)) chat.Draft = chat.LastSentMessage;
            _ = WaitForGroupJoinAsync(chat, ++chat.JoinAttempt);
            GroupConversationChanged?.Invoke(this, chat);
        });
    }

    private void OnGroupDropped(object? sender, GroupDroppedEventArgs e) => _post(() =>
    {
        if (_disposed) return;
        _groups.Remove(e.GroupID);
        if (_groupChats.TryGetValue(e.GroupID, out var chat))
        {
            chat.State = GroupChatState.NotJoined;
            chat.JoinAttempt++;
        }
        GroupsChanged?.Invoke(this);
    });

    private void ResetGroupChats()
    {
        _groupsRequested = false;
        GroupsLoaded = false;
        _pendingGroupMessages.Clear();
        foreach (var chat in _groupChats.Values)
        {
            chat.State = GroupChatState.NotJoined;
            chat.JoinAttempt++;
            chat.PendingEchoes.Clear();
        }
        Client.Self.GroupChatSessions.Clear();
        GroupsChanged?.Invoke(this);
    }

    private void StopGroupChats()
    {
        _groupChatStop.Cancel();
        _groupChatStop.Dispose();
        Client.Groups.CurrentGroups -= OnCurrentGroups;
        Client.Groups.GroupDropped -= OnGroupDropped;
        Client.Self.GroupChatJoined -= OnGroupChatJoined;
        Client.Self.ChatSessionMemberLeft -= OnGroupChatMemberLeft;
        Client.Network.EventQueueRunning -= OnGroupEventQueueRunning;
        Client.Network.UnregisterEventCallback("ChatterBoxSessionEventReply", OnGroupChatSendReply);
    }
}
