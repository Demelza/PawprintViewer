using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class ProfileActionChecks
{
    public static async Task Contact()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        var otherPackets = otherFixture.CapturePackets();
        var resident = UUID.Random(); // The profile actions must also work for non-friends.
        var opened = new List<UUID>();
        account.InstantMessagesRequested += (source, id) =>
        {
            Check(source == account, "The IM action switched to another account");
            opened.Add(id);
        };
        Connected(account, true);
        try
        {
            account.RequestInstantMessages(resident);
            account.PayResident(resident, 1234);
            account.OfferTeleport(resident);
            var sent = packets();
            var payment = sent.OfType<MoneyTransferRequestPacket>().Single();
            Check(payment.AgentData.AgentID == fixture.Owner && payment.MoneyData.SourceID == fixture.Owner &&
                payment.MoneyData.DestID == resident && payment.MoneyData.Amount == 1234 &&
                payment.MoneyData.TransactionType == (int)MoneyTransactionType.Gift &&
                payment.MoneyData.Flags == (byte)TransactionFlags.None, "A profile payment changed its account, amount or recipient");
            var teleport = sent.OfType<StartLurePacket>().Single();
            Check(teleport.AgentData.AgentID == fixture.Owner && teleport.TargetData.Single().TargetID == resident &&
                Utils.BytesToString(teleport.Info.Message) == "Join me!" && opened.SequenceEqual(new[] { resident }),
                "A profile contact action targeted the wrong account or resident");
            Check(!otherPackets().Any(), "A profile action sent packets from another account");
            Reject(() => account.PayResident(UUID.Zero, 1));
            Reject(() => account.PayResident(fixture.Owner, 1));
            Reject(() => account.PayResident(resident, 0));
            Reject(() => account.PayResident(resident, -1));
            typeof(AccountSession).GetProperty(nameof(AccountSession.Balance))!.SetValue(account, 5);
            Reject(() => account.PayResident(resident, 6));
            await fixture.Command("@showloc=n,startim=n");
            Check(!account.CanOfferTeleport(resident) && !account.CanOpenConversation(resident) &&
                !account.CanOfferFriendship(resident), "Profile actions bypassed RLV contact or location restrictions");
            Reject(() => account.OfferTeleport(resident));
            Reject(() => account.RequestInstantMessages(resident));
            Reject(() => account.OfferFriendship(resident));
            Check(!packets().OfType<MoneyTransferRequestPacket>().Any() && opened.Count == 1,
                "An invalid payment or restricted IM request was accepted");
            Connected(account, false);
            Reject(() => account.PayResident(resident, 1));
            Reject(() => account.OfferTeleport(resident));
            Reject(() => account.OfferFriendship(resident));
            Reject(() => account.BlockResident(resident));
        }
        finally { Connected(account, false); }
    }

    public static async Task Friendship()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        var resident = UUID.Random();
        Connected(account, true);
        try
        {
            account.OfferFriendship(resident);
            var offer = packets().OfType<ImprovedInstantMessagePacket>().Single();
            Check(offer.AgentData.AgentID == fixture.Owner && offer.MessageBlock.ToAgentID == resident &&
                offer.MessageBlock.Dialog == (byte)InstantMessageDialog.FriendshipOffered &&
                offer.MessageBlock.Offline == (byte)InstantMessageOnline.Offline &&
                account.IsFriendshipOfferPending(resident) && !account.IsFriend(resident),
                "The friend action added a friend before acceptance or sent an incorrect offer");
            Reject(() => account.OfferFriendship(resident));
            Check(!packets().OfType<ImprovedInstantMessagePacket>().Any(), "A pending friendship offer was sent twice");
            await FriendResponse(account, fixture, resident, InstantMessageDialog.FriendshipDeclined);
            Check(!account.IsFriendshipOfferPending(resident) && account.CanOfferFriendship(resident),
                "A declined friendship could not be offered again");
            account.OfferFriendship(resident);
            await FriendResponse(account, fixture, resident, InstantMessageDialog.FriendshipAccepted);
            Check(account.IsFriend(resident) && !account.IsFriendshipOfferPending(resident) && !account.CanOfferFriendship(resident),
                "Acceptance failed to change the profile's friendship state");
            packets();
            var changes = 0;
            account.FriendsChanged += _ => changes++;
            account.RemoveFriend(resident);
            var removal = packets().OfType<TerminateFriendshipPacket>().Single();
            Check(removal.AgentData.AgentID == fixture.Owner && removal.ExBlock.OtherID == resident &&
                !account.IsFriend(resident) && changes == 1, "Removing a friend did not update its account immediately");
            Reject(() => account.RemoveFriend(resident));
            Reject(() => account.OfferFriendship(fixture.Owner));
            Reject(() => account.BlockResident(fixture.Owner));
        }
        finally { Connected(account, false); }
    }

    public static async Task Blocking()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        var otherPackets = otherFixture.CapturePackets();
        var resident = fixture.Friend("Blocked Resident").UUID;
        var group = UUID.Random();
        var notices = new List<AccountNotification>();
        var nearby = new List<string>();
        var offers = 0;
        account.NotificationReceived += (_, notice) => notices.Add(notice);
        account.ChatLine += (_, text) => nearby.Add(text);
        account.TeleportOfferReceived += (_, _) => offers++;
        Connected(account, true); Connected(other, true);
        try
        {
            Receive(account, Message(fixture.Owner, resident, "previous message"));
            account.MarkConversationRead(resident);
            notices.Clear();
            fixture.Groups((group, "Test Group"));
            await Until(() => account.GroupsLoaded);
            account.BlockResident(resident);
            account.BlockResident(resident);
            var block = packets().OfType<UpdateMuteListEntryPacket>().Single();
            Check(block.AgentData.AgentID == fixture.Owner && block.MuteData.MuteID == resident &&
                block.MuteData.MuteType == (int)MuteType.Resident && block.MuteData.MuteFlags == (uint)MuteFlags.Default &&
                Utils.BytesToString(block.MuteData.MuteName) == "Blocked Resident" &&
                account.IsResidentBlocked(resident) && !other.IsResidentBlocked(resident),
                "Blocking failed to update the grid mute list or leaked across accounts");
            // Simulate the login snapshot arriving after the Block click.
            account.Client.Self.MuteList.Clear();
            Invoke(account.Client.Self, "OnMuteListUpdated", EventArgs.Empty);
            Check(account.IsResidentBlocked(resident) && account.Client.Self.MuteList.Values.Any(entry => entry.ID == resident),
                "A late login mute-list download overwrote a new Block");
            Receive(account, Message(fixture.Owner, resident, "blocked private message"));
            Receive(account, Message(fixture.Owner, resident, "blocked group message", InstantMessageDialog.SessionSend, group));
            Receive(account, Message(fixture.Owner, resident, "blocked offer", InstantMessageDialog.RequestTeleport, UUID.Random()));
            Invoke(account, "OnChatReceived", null!, new ChatEventArgs(fixture.Simulator, "blocked nearby message", ChatAudibleLevel.Fully,
                ChatType.Normal, ChatSourceType.Agent, "Blocked Resident", resident, resident, Vector3.Zero));
            Check(account.Conversations.Single().Messages.Single().Text == "previous message" &&
                account.UnreadInstantMessages == 0 && account.UnreadGroupMessages == 0 &&
                account.GroupConversations.All(chat => chat.Messages.Count == 0) && nearby.Count == 0 && notices.Count == 0 && offers == 0,
                "A blocked message reached chat, unread counters, teleport prompts or desktop notifications");
            Receive(other, Message(otherFixture.Owner, resident, "another account"));
            Check(other.Conversations.Single().Messages.Count == 1 && !otherPackets().Any(),
                "One account's blacklist blocked another account's messages");
            account.UnblockResident(resident);
            var unblock = packets().OfType<RemoveMuteListEntryPacket>().Single();
            Check(unblock.AgentData.AgentID == fixture.Owner && unblock.MuteData.MuteID == resident &&
                !account.IsResidentBlocked(resident) && !account.Client.Self.MuteList.Values.Any(entry => entry.ID == resident),
                "Unblocking did not remove the grid mute entry");
            Receive(account, Message(fixture.Owner, resident, "unblocked"));
            Check(account.Conversations.Single().Messages.Count == 2 && notices.Count == 1,
                "Unblocking did not restore incoming messages");
            account.Client.Self.MuteList[resident + "|Blocked Resident"] = new MuteEntry
                { ID = resident, Name = "Blocked Resident", Type = MuteType.Resident };
            Invoke(account.Client.Self, "OnMuteListUpdated", EventArgs.Empty);
            Check(!account.IsResidentBlocked(resident) && !account.Client.Self.MuteList.Values.Any(entry => entry.ID == resident),
                "A late mute-list snapshot undid a new Unblock");
            // Imported resident mute entries also apply; object/voice-only mutes do not hide resident text.
            var imported = UUID.Random();
            var key = imported + "|Imported Resident";
            account.Client.Self.MuteList[key] = new MuteEntry { ID = imported, Name = "Imported Resident", Type = MuteType.Object };
            Check(!account.IsResidentBlocked(imported), "An object mute hid a resident's messages");
            account.Client.Self.MuteList[key].Type = MuteType.Resident;
            account.Client.Self.MuteList[key].Flags = MuteFlags.TextChat;
            Check(!account.IsResidentBlocked(imported), "A mute with text explicitly allowed hid messages");
            account.Client.Self.MuteList[key].Flags = MuteFlags.Default;
            Receive(account, Message(fixture.Owner, imported, "imported block"));
            Check(account.Conversations.Count == 1 && account.Conversations.Single().Messages.Count == 2,
                "An imported grid block did not suppress IMs");
        }
        finally { Connected(account, false); Connected(other, false); }
    }

    private static async Task FriendResponse(AccountSession account, Fixture fixture, UUID resident, InstantMessageDialog dialog)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(AccountSession _) { if (!account.IsFriendshipOfferPending(resident)) ready.TrySetResult(); }
        account.FriendsChanged += Changed;
        try
        {
            fixture.Receive(fixture.PrivateIm(resident, "Profile Resident", "response", dialog));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { account.FriendsChanged -= Changed; }
    }

    private static InstantMessage Message(UUID owner, UUID resident, string text,
        InstantMessageDialog dialog = InstantMessageDialog.MessageFromAgent, UUID? session = null) => new()
    {
        FromAgentID = resident, FromAgentName = "Blocked Resident", ToAgentID = owner, Message = text,
        Dialog = dialog, IMSessionID = session ?? (resident ^ owner), BinaryBucket = Array.Empty<byte>()
    };
    private static void Receive(AccountSession account, InstantMessage message) =>
        Invoke(account, "OnInstantMessage", null!, new InstantMessageEventArgs(message, account.Client.Network.CurrentSim));
    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Connected(AccountSession account, bool connected) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, connected);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("An unavailable profile action was accepted");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
