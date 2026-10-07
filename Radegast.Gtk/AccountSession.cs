using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.Packets;
using Radegast;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Radegast.Gtk;

internal sealed record NearbyResident(UUID Id, string Name, int Distance);
internal sealed record ScriptMenu(UUID ObjectId, string ObjectName, string OwnerName,
    string Message, int Channel, IReadOnlyList<string> Buttons)
{
    public UUID OwnerId { get; init; }
}

/// <summary>A single grid connection. All public events are delivered on the GTK thread.</summary>
internal sealed partial class AccountSession : IDisposable
{
    private static readonly Regex ChatChannelPrefix = new(@"^/(?<channel>[+-]?[0-9]+)\s*(?<message>[\s\S]*)$", RegexOptions.CultureInvariant);
    private readonly Dictionary<UUID, string> _names = new();
    private readonly HashSet<UUID> _requestedNames = new();
    private readonly object _nameLock = new();
    private readonly SeatAnimationController _seatAnimations;
    private readonly Action<Action> _post;
    private List<NearbyResident> _nearby = new();
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public GridClient Client { get; } = new();
    public CurrentOutfitFolder Outfit { get; }
    public RlvSession Rlv { get; }
    public NetCom Net { get; }
    public string Name { get; private set; } = "Connecting…";
    public string Status { get; private set; } = "Connecting…";
    public int? Balance { get; private set; }
    public bool IsConnected => Net.IsLoggedIn;
    public IReadOnlyList<NearbyResident> Nearby => _nearby;

    public event Action<AccountSession, LoginStatus, string, string>? LoginProgress;
    public event Action<AccountSession>? StateChanged;
    public event Action<AccountSession, string>? ChatLine;
    public event Action<AccountSession>? NearbyChanged;
    public event Action<AccountSession, ScriptMenu>? ScriptDialogReceived;
    public event Action<AccountSession, ScriptQuestionEventArgs>? PermissionRequested;

    public AccountSession(Action<Action>? post = null, AccountSettingsStore? settingsStore = null, TimeProvider? clock = null)
    {
        // This client has no scene textures; map tiles use their own HTTP source.
        // LibreMetaverse 3.1.5 restarts its UDP texture loop with a cancelled
        // delay token after reconnect, spinning at full CPU even with no requests.
        // Disable that unused worker before the first SDK login event.
        Client.Settings.TexturePipeline.Enabled = false;
        _post = post ?? GtkDispatch.Post;
        _settingsStore = settingsStore ?? new AccountSettingsStore();
        _clock = clock ?? TimeProvider.System;
        _restartRecovery = new RegionRestartRecovery(_clock, _post, CurrentRestartLocation, FindRestartDestinationAsync,
            (region, position, token) => TeleportLocationAsync(region, position, false, token));
        _restartRecovery.Changed += OnRegionRestartChanged;
        _seatAnimations = new SeatAnimationController(Client);
        Outfit = new CurrentOutfitFolder(Client);
        Rlv = new RlvSession(Client, Outfit, _post);
        Rlv.Message += OnRlvMessage;
        Rlv.Changed += OnRlvChanged;
        Net = new NetCom(Client);
        Net.ClientLoginStatus += OnLoginProgress;
        Net.ClientDisconnected += OnDisconnected;
        Net.ClientLoggedOut += OnLoggedOut;
        Net.ChatReceived += OnChatReceived;
        Net.MoneyBalanceUpdated += OnMoneyBalanceUpdated;
        Client.Self.ScriptDialog += OnScriptDialog;
        Client.Self.ScriptQuestion += OnScriptQuestion;
        Client.Self.IM += OnInstantMessage;
        Client.Self.TeleportProgress += OnRestartTeleportProgress;
        Client.Self.MuteListUpdated += OnBlockListUpdated;
        Client.Grid.CoarseLocationUpdate += OnCoarseLocationUpdate;
        Client.Avatars.UUIDNameReply += OnNameReply;
        Client.Network.RegisterLoginResponseCallback(OnLoginResponse);
        Client.Network.RegisterCallback(PacketType.AlertMessage, OnRegionRestartAlert);
        Client.Network.SimChanged += OnRestartSimChanged;
        InitializeFriends();
        InitializeGroupChats();
    }

    public void Login(string username, string password, Grid grid, StartLocationType startLocation, string mfaToken = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var parts = username.Trim().Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Enter an account name.", nameof(username));

        Net.LoginOptions.FirstName = parts[0];
        Net.LoginOptions.LastName = parts.Length > 1 ? parts[1] : "Resident";
        Net.LoginOptions.Password = password;
        Net.LoginOptions.Grid = grid;
        ResetReconnect();
        _restartRecovery.Cancel("Pending return cancelled by a new login.");
        LoadAccountSettings();
        Net.LoginOptions.Channel = Program.ViewerName;
        Net.LoginOptions.Version = Program.ViewerVersion;
        Net.LoginOptions.StartLocation = startLocation;
        Net.LoginOptions.MfaToken = mfaToken;
        Net.LoginOptions.MfaHash ??= string.Empty;
        Net.AgreeToTos = true;
        Name = Net.LoginOptions.FullName;
        Status = "Connecting…";
        _friendPresence.Reset();
        _pendingFriendshipOffers.Clear();
        _residentBlockChanges.Clear();
        StateChanged?.Invoke(this);
        Net.Login();
    }

    public bool SendNearbyChat(string message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(message)) return false;
        var requestedChannel = 0;
        var prefix = ChatChannelPrefix.Match(message);
        if (prefix.Success)
        {
            if (!int.TryParse(prefix.Groups["channel"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out requestedChannel))
            {
                ChatLine?.Invoke(this, $"[{DateTime.Now:HH:mm}] Invalid chat channel number.");
                return false;
            }
            message = prefix.Groups["message"].Value;
            if (string.IsNullOrWhiteSpace(message)) return false;
        }
        if (requestedChannel != 0)
        {
            if (Rlv.Enabled && !Rlv.Service.Permissions.CanChat(requestedChannel, message))
            {
                OnRlvMessage($"[RLV] Sending chat on channel {requestedChannel} is restricted.");
                return false;
            }
            Net.ChatOut(message, ChatType.Normal, requestedChannel);
            return true;
        }
        var type = ChatType.Normal;
        if (Rlv.Enabled)
        {
            var permissions = Rlv.Service.Permissions;
            var emote = message.StartsWith("/me ", StringComparison.OrdinalIgnoreCase);
            if (emote && !permissions.CanEmote()) { OnRlvMessage("[RLV] Emotes are restricted."); return false; }
            var redirected = emote ? permissions.TryGetRedirEmoteChannels(out var channels) : permissions.TryGetRedirChatChannels(out channels);
            if (redirected)
            {
                var sent = false;
                foreach (var channel in channels.Where(channel => permissions.CanChat(channel, message)))
                {
                    Client.Self.Chat(message, channel, ChatType.Normal);
                    sent = true;
                }
                if (!sent) OnRlvMessage("[RLV] Sending chat on the redirected channels is restricted.");
                return sent;
            }
            if (!permissions.CanChat(0, message)) { OnRlvMessage("[RLV] Sending nearby chat is restricted."); return false; }
            if (!permissions.CanSendChat())
            {
                var period = message.IndexOf('.');
                if (period >= 0) message = message[..period];
                var limit = emote ? 30 : 15;
                if (message.Length > limit) message = message[..limit];
            }
            if (!permissions.CanChatNormal())
            {
                if (permissions.CanChatWhisper()) type = ChatType.Whisper;
                else if (permissions.CanChatShout()) type = ChatType.Shout;
                else { OnRlvMessage("[RLV] All chat volumes are restricted."); return false; }
            }
        }
        Net.ChatOut(message, type, 0);
        return true;
    }

    private void OnRlvMessage(string message)
    {
        if (!_disposed) ChatLine?.Invoke(this, $"[{DateTime.Now:HH:mm}] {message}");
    }

    private void OnRlvChanged()
    {
        if (_disposed) return;
        StateChanged?.Invoke(this);
    }

    public void ReplyToScriptDialog(ScriptMenu menu, int buttonIndex, string label)
    {
        if (!IsConnected) throw new InvalidOperationException("This account is disconnected.");
        if (Rlv.Enabled && !Rlv.Service.Permissions.CanChat(menu.Channel, label))
            throw new InvalidOperationException("Sending replies on this channel is restricted by RLV.");
        Client.Self.ReplyToScriptDialog(menu.Channel, buttonIndex, label, menu.ObjectId);
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        var owner = string.Join(" ", new[] { e.FirstName, e.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var menu = new ScriptMenu(e.ObjectID, e.ObjectName, owner, e.Message,
            e.Channel, e.ButtonLabels.ToArray()) { OwnerId = e.OwnerID };
        _post(() =>
        {
            if (_disposed) return;
            ScriptDialogReceived?.Invoke(this, menu);
            Notify(NotificationCategory.Menus, string.IsNullOrWhiteSpace(menu.ObjectName) ? "Scripted object" : menu.ObjectName,
                menu.Message, menu.ObjectId);
        });
    }

    private void OnScriptQuestion(object? sender, ScriptQuestionEventArgs e)
    {
        if (_disposed) return;
        if (Rlv.Enabled && Rlv.Service.Permissions.IsAutoDenyPermissions())
        {
            Client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID, ScriptPermission.None);
            return;
        }
        var automatic = ScriptPermission.TriggerAnimation | ScriptPermission.Attach | ScriptPermission.TakeControls;
        if (Rlv.Enabled && Rlv.Service.Permissions.IsAutoAcceptPermissions() && (e.Questions & ~automatic) == 0)
        {
            Client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID, e.Questions);
            return;
        }
        _post(() => { if (!_disposed) PermissionRequested?.Invoke(this, e); });
    }

    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        if (_disposed) return;
        ReceivePrivateInstantMessage(e.IM);
        ReceiveGroupInstantMessage(e.IM);
        ReceiveTeleportOffer(e.IM);
    }

    private void OnLoginProgress(object? sender, LoginProgressEventArgs e)
    {
        var status = e.Status;
        var message = e.Message;
        var reason = e.FailReason;
        _post(() =>
        {
            if (_disposed) return;
            Status = status == LoginStatus.Success ? "Connected" :
                status == LoginStatus.Failed ? "Login failed" : message;
            ReconnectOnLoginProgress(status, message, reason);
            if (status == LoginStatus.Success)
            {
                _friendPresence.Connected();
                Name = Client.Self.Name;
                Client.Self.RequestMuteList();
                RequestGroups();
                _ = RetrieveOfflineInstantMessagesAsync();
            }
            StateChanged?.Invoke(this);
            LoginProgress?.Invoke(this, status, message, reason);
        });
    }

    private void OnLoginResponse(bool success, bool redirect, string message, string reason, LoginResponseData? reply)
    {
        if ((success || reason == "mfa_challenge") && reply != null)
            Net.LoginOptions.MfaHash = reply.MfaHash;
        if (success && reply != null) MapTileServer = WorldMapTileSource.ServerUri(reply.MapServerUrl);
    }

    private void OnMoneyBalanceUpdated(object? sender, BalanceEventArgs e)
    {
        var balance = e.Balance;
        _post(() =>
        {
            if (_disposed) return;
            Balance = balance;
            StateChanged?.Invoke(this);
        });
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e) =>
        _post(() =>
        {
            SetDisconnected("Disconnected");
            if (!_disposed) ReconnectOnDisconnect(e.Reason != NetworkManager.DisconnectType.ClientInitiated);
        });

    private void OnLoggedOut(object? sender, EventArgs e) =>
        _post(() => { ResetReconnect(); SetDisconnected("Logged out"); });

    private void SetDisconnected(string status)
    {
        if (_disposed) return;
        Status = status;
        Interlocked.Exchange(ref _serverTeleportBusyUntil, 0);
        _restartRecovery.Disconnected();
        var enabled = Rlv.Enabled;
        Rlv.SetEnabled(false);
        if (enabled) Rlv.SetEnabled(true);
        _nearby.Clear();
        _mapAvatarPositions.Clear();
        ResetTeleportOffers();
        ResetGroupChats();
        _friendPresence.Reset();
        _pendingFriendshipOffers.Clear();
        lock (_nameLock)
        {
            _requestedFriendNames.Clear();
            _requestedNames.Clear();
        }
        StateChanged?.Invoke(this);
        NearbyChanged?.Invoke(this);
    }

    private void OnChatReceived(object? sender, ChatEventArgs e)
    {
        if (e.Message == null || e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping)
            return;
        if (Rlv.TryHandleChat(e)) return;
        _post(() =>
        {
            if (_disposed) return;
            if (e.SourceType == ChatSourceType.Agent && IsResidentBlocked(e.SourceID)) return;
            var permissions = Rlv.Service.Permissions;
            if (Rlv.Enabled && e.SourceType == ChatSourceType.Agent && e.SourceID != Client.Self.AgentID &&
                !permissions.CanReceiveChat(e.Message, e.SourceID.Guid)) return;
            var from = string.IsNullOrWhiteSpace(e.FromName) ? "System" : e.FromName;
            if (Rlv.Enabled && e.SourceType == ChatSourceType.Agent && e.SourceID != Client.Self.AgentID &&
                !permissions.CanShowNames(e.SourceID.Guid)) from = "Resident";
            if (e.SourceType == ChatSourceType.Agent && !string.IsNullOrWhiteSpace(e.FromName))
                RememberAvatarName(e.SourceID, e.FromName);
            ChatLine?.Invoke(this, RedactText($"[{DateTime.Now:HH:mm}] {from}: {e.Message}"));
            if (IsWornObjectMessage(e)) Notify(NotificationCategory.WornObjects, from, e.Message, e.SourceID);
        });
    }

    public string RedactText(string text)
    {
        if (!Rlv.Enabled) return text;
        var permissions = Rlv.Service.Permissions;
        text = AvatarProfileLinks.HideNames(text, CanShowAvatarName);
        lock (_nameLock)
            foreach (var (id, name) in _names)
                if (id != Client.Self.AgentID && !string.IsNullOrWhiteSpace(name) && !permissions.CanShowNames(id.Guid))
                    text = text.Replace(name, "Resident", StringComparison.OrdinalIgnoreCase);
        if (!permissions.CanShowLoc())
        {
            text = Regex.Replace(text, @"(?:secondlife://(?!/?app/agent/)|https?://maps\.secondlife\.com/secondlife/)[^\s]+", "[location hidden]", RegexOptions.IgnoreCase);
            var region = Client.Network.CurrentSim?.Name;
            if (!string.IsNullOrEmpty(region)) text = text.Replace(region, "[region hidden]", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private void OnCoarseLocationUpdate(object? sender, CoarseLocationUpdateEventArgs e)
    {
        // Share one immutable position snapshot between the map and nearby list.
        var positions = e.Positions.ToArray();
        RecordMapAvatarPositions(e.Simulator, positions);
        if (e.Simulator != Client.Network.CurrentSim) return;

        var selfId = Client.Self.AgentID;
        var selfPosition = e.Positions.TryGetValue(selfId, out var coarseSelf)
            ? coarseSelf : Client.Self.SimPosition;
        var missing = new List<UUID>();
        List<NearbyResident> nearby;

        lock (_nameLock)
        {
            nearby = new List<NearbyResident>(positions.Length);
            foreach (var (id, position) in positions)
            {
                if (id == selfId) continue;
                if (!_names.TryGetValue(id, out var name))
                {
                    name = id.ToString()[..8];
                    if (_requestedNames.Add(id)) missing.Add(id);
                }
                var dx = position.X - selfPosition.X;
                var dy = position.Y - selfPosition.Y;
                var dz = position.Z - selfPosition.Z;
                var distance = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
                nearby.Add(new NearbyResident(id, name, distance));
            }
        }

        nearby.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        _post(() =>
        {
            if (_disposed) return;
            _nearby = nearby;
            NearbyChanged?.Invoke(this);
        });

        if (missing.Count > 0)
            Client.Avatars.RequestAvatarNames(missing);
    }

    private void OnNameReply(object? sender, UUIDNameReplyEventArgs e)
    {
        var resolved = e.Names.ToArray();
        _post(() =>
        {
            if (_disposed) return;
            lock (_nameLock)
            {
                foreach (var (id, name) in resolved)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _names[id] = name;
                        _requestedNames.Remove(id);
                    }
                    _requestedFriendNames.Remove(id);
                }
                _nearby = _nearby.Select(person =>
                    _names.TryGetValue(person.Id, out var name)
                        ? person with { Name = name } : person).ToList();
            }
            NearbyChanged?.Invoke(this);
            AvatarNamesChanged?.Invoke(this);
            if (resolved.Any(name => Client.Friends.FriendList.ContainsKey(name.Key))) FriendsChanged?.Invoke(this);
            foreach (var (id, _) in resolved)
                if (_conversations.TryGetValue(id, out var conversation)) ConversationChanged?.Invoke(this, conversation);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetReconnect();
        _restartRecovery.Changed -= OnRegionRestartChanged;
        _restartRecovery.Dispose();
        ResetTeleportOffers();
        StopFriends();
        StopGroupChats();
        Net.ClientLoginStatus -= OnLoginProgress;
        Net.ClientDisconnected -= OnDisconnected;
        Net.ClientLoggedOut -= OnLoggedOut;
        Net.ChatReceived -= OnChatReceived;
        Net.MoneyBalanceUpdated -= OnMoneyBalanceUpdated;
        Rlv.Message -= OnRlvMessage;
        Rlv.Changed -= OnRlvChanged;
        Client.Self.ScriptDialog -= OnScriptDialog;
        Client.Self.ScriptQuestion -= OnScriptQuestion;
        Client.Self.IM -= OnInstantMessage;
        Client.Self.TeleportProgress -= OnRestartTeleportProgress;
        Client.Self.MuteListUpdated -= OnBlockListUpdated;
        Client.Grid.CoarseLocationUpdate -= OnCoarseLocationUpdate;
        Client.Avatars.UUIDNameReply -= OnNameReply;
        Client.Network.UnregisterLoginResponseCallback(OnLoginResponse);
        Client.Network.UnregisterCallback(PacketType.AlertMessage, OnRegionRestartAlert);
        Client.Network.SimChanged -= OnRestartSimChanged;
        Rlv.Dispose();
        _seatAnimations.Dispose();
        Outfit.Dispose();
        if (Net.IsLoggingIn) Net.CancelLogin();
        if (Net.IsLoggedIn) Net.Logout();
        Net.Dispose();
    }
}
