using System.Net;
using System.Reflection;
using System.Threading.Channels;
using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.Packets;
using LibreMetaverse.RLV;
using Radegast.Gtk;

// Integration checks for the GTK account adapter; no grid login or display is required.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Private messages use the selected account, resident session and offline delivery", () =>
    {
        using var session = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var f = new Fixture(session);
        using var g = new Fixture(other);
        var peer = f.Friend("Recipient Resident").UUID;
        var packets = f.CapturePackets();
        var otherPackets = g.CapturePackets();
        SetConnected(session, true);
        try
        {
            var conversation = session.OpenConversation(peer);
            conversation.Draft = "/9 private text 日本語";
            session.SendInstantMessage(peer, conversation.Draft);
            var sent = packets().OfType<ImprovedInstantMessagePacket>().Single();
            Check(sent.Header.Reliable && sent.AgentData.AgentID == f.Owner && sent.MessageBlock.ToAgentID == peer &&
                sent.MessageBlock.ID == (peer ^ f.Owner) && sent.MessageBlock.Dialog == (byte)InstantMessageDialog.MessageFromAgent &&
                sent.MessageBlock.Offline == (byte)InstantMessageOnline.Offline && !sent.MessageBlock.FromGroup &&
                Utils.BytesToString(sent.MessageBlock.Message) == "/9 private text 日本語", "Private IM routing or content was changed");
            Check(conversation.Messages.Single().Outgoing && conversation.Draft == "" && conversation.UnreadCount == 0,
                "Sent message history, draft or unread state is incorrect");
            Check(!otherPackets().Any() && other.Conversations.Count == 0, "An IM leaked into another account");
            Check(ReferenceEquals(conversation, session.OpenConversation(peer)), "Opening an existing conversation created a duplicate");
        }
        finally { SetConnected(session, false); }
        return Task.CompletedTask;
    }),
    ("Incoming private IMs preserve sender, repeated messages and offline timestamps per account", async () =>
    {
        using var session = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var f = new Fixture(session);
        using var g = new Fixture(other);
        var peer = UUID.Random();
        var packet = f.PrivateIm(peer, "Unknown Resident", "hello");
        packet.MessageBlock.Timestamp = (uint)DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        packet.MessageBlock.Offline = (byte)InstantMessageOnline.Offline;
        await ReceiveIm(f, packet);
        await ReceiveIm(f, packet);
        var conversation = session.Conversations.Single();
        Check(conversation.PeerId == peer && conversation.SessionId == (peer ^ f.Owner) && conversation.Messages.Count == 2 &&
            conversation.UnreadCount == 2 && conversation.Messages.All(message => !message.Outgoing), "Incoming IMs were lost or misrouted");
        Check(session.DisplayConversationName(peer) == "Unknown Resident" &&
            (conversation.Messages[0].Timestamp.ToUniversalTime() - DateTimeOffset.FromUnixTimeSeconds(packet.MessageBlock.Timestamp).UtcDateTime).Duration() < TimeSpan.FromSeconds(1),
            "The sender name or offline delivery timestamp is incorrect");
        Check(other.Conversations.Count == 0, "An incoming IM appeared in another account");
    }),
    ("Protocol notices, group messages, conferences and wrong recipients do not become private conversations", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var peer = UUID.Random();
        foreach (var dialog in new[] { InstantMessageDialog.SessionSend, InstantMessageDialog.MessageFromObject,
            InstantMessageDialog.StartTyping, InstantMessageDialog.InventoryOffered, InstantMessageDialog.RequestTeleport })
        {
            await ReceiveIm(f, f.PrivateIm(peer, "Resident", "notice", dialog));
            Check(session.Conversations.Count == 0, $"A {dialog} event became a private conversation");
        }
        var group = f.PrivateIm(peer, "Resident", "group");
        group.MessageBlock.FromGroup = true;
        group.MessageBlock.ID = UUID.Random();
        await ReceiveIm(f, group);
        var conference = f.PrivateIm(peer, "Resident", "conference");
        conference.MessageBlock.BinaryBucket = new byte[16];
        await ReceiveIm(f, conference);
        var wrong = f.PrivateIm(peer, "Resident", "wrong recipient");
        wrong.MessageBlock.ToAgentID = UUID.Random();
        await ReceiveIm(f, wrong);
        await ReceiveIm(f, f.PrivateIm(f.Owner, "Self", "echo"));
        await ReceiveIm(f, f.PrivateIm(UUID.Zero, "System", "notice"));
        Check(session.Conversations.Count == 0, "A group, conference, system message or wrong recipient created a private conversation");
        await ReceiveIm(f, f.PrivateIm(peer, "Resident", "I am busy", InstantMessageDialog.BusyAutoResponse));
        Check(session.Conversations.Single().Messages.Single().Text == "I am busy", "A resident's busy response was not displayed");
    }),
    ("Reading one conversation preserves other unread messages, histories and drafts", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var alice = UUID.Random();
        var bob = UUID.Random();
        await ReceiveIm(f, f.PrivateIm(alice, "Alice Resident", "one"));
        await ReceiveIm(f, f.PrivateIm(bob, "Bob Resident", "two"));
        var conversation = session.Conversations.Single(chat => chat.PeerId == alice);
        conversation.Draft = "unfinished";
        session.MarkConversationRead(alice);
        session.MarkConversationRead(alice);
        Check(session.UnreadInstantMessages == 1 && conversation.UnreadCount == 0 && conversation.Draft == "unfinished" &&
            conversation.Messages.Count == 1, "Reading a conversation lost drafts/history or marked another conversation read");
        SetConnected(session, true);
        try { Check(ReferenceEquals(session.OpenConversation(alice), conversation), "The draft's conversation was replaced"); }
        finally { SetConnected(session, false); }
    }),
    ("Private IMs reject empty messages, invalid peers and disconnected sends", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var peer = UUID.Random();
        var packets = f.CapturePackets();
        ExpectRejected(() => session.SendInstantMessage(peer, "hello"));
        SetConnected(session, true);
        try
        {
            ExpectRejected(() => session.SendInstantMessage(peer, "  "));
            ExpectRejected(() => session.SendInstantMessage(UUID.Zero, "hello"));
            ExpectRejected(() => session.SendInstantMessage(f.Owner, "hello"));
            Check(session.Conversations.Count == 0 && !packets().Any(), "An invalid IM changed history or reached the network");
        }
        finally { SetConnected(session, false); }
        return Task.CompletedTask;
    }),
    ("RLV prevents starting private IMs but permits established and incoming conversations", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var existing = UUID.Random();
        var incoming = UUID.Random();
        var newPeer = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(session, true);
        try
        {
            session.OpenConversation(existing);
            await f.Command("@startim=n");
            Check(!session.CanOpenConversation(newPeer) && session.CanOpenConversation(existing), "The start IM restriction is incorrect");
            ExpectRejected(() => session.OpenConversation(newPeer));
            ExpectRejected(() => session.SendInstantMessage(newPeer, "blocked"));
            Check(!packets().OfType<ImprovedInstantMessagePacket>().Any(), "A new restricted conversation sent a packet");
            session.SendInstantMessage(existing, "allowed");
            await ReceiveIm(f, f.PrivateIm(incoming, "Incoming Resident", "hello"));
            Check(session.CanOpenConversation(incoming), "An incoming conversation was blocked by startim");
            session.SendInstantMessage(incoming, "reply");
            Check(packets().OfType<ImprovedInstantMessagePacket>().Count() == 2, "Existing/incoming conversation replies were blocked");
        }
        finally { SetConnected(session, false); }
    }),
    ("RLV IM sending and receiving restrictions enforce resident exceptions", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var allowed = UUID.Random();
        var blocked = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(session, true);
        try
        {
            var draft = session.OpenConversation(blocked);
            draft.Draft = "keep this draft";
            await f.Command($"@sendim=n,sendim:{allowed}=add,recvim=n,recvim:{allowed}=add");
            Check(!session.CanSendInstantMessage(blocked, "blocked") && session.CanSendInstantMessage(allowed, "allowed"),
                "RLV sending restrictions or exceptions are incorrect");
            ExpectRejected(() => session.SendInstantMessage(blocked, draft.Draft));
            Check(draft.Draft == "keep this draft" && draft.Messages.Count == 0, "A restricted send erased its draft or added a sent line");
            session.SendInstantMessage(allowed, "allowed");
            await ReceiveIm(f, f.PrivateIm(blocked, "Blocked Resident", "hidden message"));
            await ReceiveIm(f, f.PrivateIm(allowed, "Allowed Resident", "visible message"));
            Check(draft.Messages.Count == 0 && session.Conversations.Single(chat => chat.PeerId == allowed).Messages.Count == 2,
                "RLV receive restrictions or exceptions are incorrect");
            Check(packets().OfType<ImprovedInstantMessagePacket>().Single().MessageBlock.ToAgentID == allowed,
                "A blocked IM reached the network");
            await f.Command("@clear");
            await ReceiveIm(f, f.PrivateIm(blocked, "Blocked Resident", "now visible"));
            Check(draft.Messages.Single().Text == "now visible", "An unlocked resident's message was still hidden");
            await f.Command("@shownames=n");
            Check(session.DisplayConversationName(allowed) == "Resident", "A conversation label exposed an RLV-hidden name");
        }
        finally { SetConnected(session, false); }
    }),
    ("Friends sort online first and alphabetically within each group", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        f.Friend("Zelda Resident", true);
        f.Friend("alice Resident", true);
        f.Friend("Aaron Resident");
        f.Friend("Zorro Resident");
        Check(session.Friends.Select(friend => friend.Name).SequenceEqual(new[]
            { "alice Resident", "Zelda Resident", "Aaron Resident", "Zorro Resident" }), "Friend ordering is incorrect");
        Check(session.Friends.Select(friend => friend.IsOnline).SequenceEqual(new[] { true, true, false, false }),
            "Online state was lost in the roster");
        return Task.CompletedTask;
    }),
    ("Name and online notifications update the correct account's friend roster", async () =>
    {
        using var session = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var f = new Fixture(session);
        using var g = new Fixture(other);
        var friend = f.Friend("");
        g.Friend("Other Resident", id: friend.UUID);
        await FriendsEvent(session, () => f.Receive(new UUIDNameReplyPacket
        {
            UUIDNameBlock = new[] { new UUIDNameReplyPacket.UUIDNameBlockBlock
                { ID = friend.UUID, FirstName = Utils.StringToBytes("Alice"), LastName = Utils.StringToBytes("Resident") } }
        }));
        Check(session.Friends.Single().Name == "Alice Resident", "An initially empty friend name did not update");
        await FriendsEvent(session, () => f.Receive(new OnlineNotificationPacket
        {
            AgentBlock = new[] { new OnlineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } }
        }));
        Check(session.Friends.Single().IsOnline, "The online notification was not reflected");
        Check(other.Friends.Single().Name == "Other Resident" && !other.Friends.Single().IsOnline,
            "The name or online state leaked into another account");
        await FriendsEvent(session, () => f.Receive(new OfflineNotificationPacket
        {
            AgentBlock = new[] { new OfflineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } }
        }));
        Check(!session.Friends.Single().IsOnline, "The offline notification was not reflected");
        await FriendsEvent(session, () => f.Receive(new TerminateFriendshipPacket
        {
            ExBlock = { OtherID = friend.UUID }
        }));
        Check(session.Friends.Count == 0 && other.Friends.Count == 1, "A terminated friendship was not removed from its account");
    }),
    ("Friend payments use the originating account, recipient and exact whole amount", () =>
    {
        using var session = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var f = new Fixture(session);
        using var g = new Fixture(other);
        var friend = f.Friend("Recipient Resident");
        var packets = f.CapturePackets();
        var otherPackets = g.CapturePackets();
        SetConnected(session, true);
        try
        {
            session.PayFriend(friend.UUID, 1234);
            var payment = packets().OfType<MoneyTransferRequestPacket>().Single();
            Check(payment.Header.Reliable && payment.AgentData.AgentID == f.Owner && payment.MoneyData.SourceID == f.Owner &&
                payment.MoneyData.DestID == friend.UUID && payment.MoneyData.Amount == 1234 &&
                payment.MoneyData.TransactionType == (int)MoneyTransactionType.Gift && payment.MoneyData.Flags == (byte)TransactionFlags.None,
                "Payment changed the recipient, amount, transaction type or account");
            Check(!otherPackets().OfType<MoneyTransferRequestPacket>().Any(), "A payment was sent from another account");
        }
        finally { SetConnected(session, false); }
        return Task.CompletedTask;
    }),
    ("Invalid payment amounts, stale recipients and disconnected accounts send no money", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var friend = f.Friend("Recipient Resident");
        var packets = f.CapturePackets();
        foreach (var input in new[] { "", "0", "-1", "+1", "1.5", "1e3", "1,000", " 1", "１２", "2147483648", "123456789012345" })
            Check(!AccountSession.TryParsePaymentAmount(input, out _), $"An invalid payment amount was accepted: {input}");
        Check(AccountSession.TryParsePaymentAmount("0012", out var parsed) && parsed == 12, "A valid whole amount was rejected");
        ExpectRejected(() => session.PayFriend(friend.UUID, 10));
        SetConnected(session, true);
        try
        {
            ExpectRejected(() => session.PayFriend(friend.UUID, 0));
            ExpectRejected(() => session.PayFriend(friend.UUID, -1));
            ExpectRejected(() => session.PayFriend(UUID.Zero, 10));
            ExpectRejected(() => session.PayFriend(UUID.Random(), 10));
            typeof(AccountSession).GetProperty(nameof(AccountSession.Balance))!.SetValue(session, 5);
            ExpectRejected(() => session.PayFriend(friend.UUID, 6));
            f.RemoveFriend(friend.UUID);
            ExpectRejected(() => session.PayFriend(friend.UUID, 1));
            Check(!packets().OfType<MoneyTransferRequestPacket>().Any(), "An invalid or stale payment reached the network");
        }
        finally { SetConnected(session, false); }
        return Task.CompletedTask;
    }),
    ("Friend teleport offers respect hidden locations and granted map rights", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var friend = f.Friend("Recipient Resident", true);
        var packets = f.CapturePackets();
        SetConnected(session, true);
        try
        {
            await f.Command("@tplure=n"); // Receiving a lure is a different action from offering one.
            session.OfferFriendTeleport(friend.UUID);
            var offer = packets().OfType<StartLurePacket>().Single();
            Check(offer.AgentData.AgentID == f.Owner && offer.TargetData.Single().TargetID == friend.UUID &&
                Utils.BytesToString(offer.Info.Message) == "Join me!", "The teleport offer used the wrong account or recipient");
            await f.Command("@showloc=n");
            Check(!session.CanOfferFriendTeleport(friend.UUID), "Hidden locations allowed a teleport offer without map rights");
            ExpectRejected(() => session.OfferFriendTeleport(friend.UUID));
            Check(!packets().OfType<StartLurePacket>().Any(), "A blocked offer was sent");
            friend.CanSeeMeOnMap = true;
            Check(session.CanOfferFriendTeleport(friend.UUID), "A friend with granted map rights could not receive an offer");
            session.OfferFriendTeleport(friend.UUID);
            Check(packets().OfType<StartLurePacket>().Single().TargetData.Single().TargetID == friend.UUID, "The permitted offer was not sent");
        }
        finally { SetConnected(session, false); }
        ExpectRejected(() => session.OfferFriendTeleport(friend.UUID));
    }),
    ("Server payment replies report confirmation or failure and update the balance", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var friend = f.Friend("Recipient Resident");
        var lines = new List<string>();
        session.ChatLine += (_, line) => lines.Add(line);
        async Task Reply(bool success)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(AccountSession account) => completion.TrySetResult();
            session.StateChanged += Changed;
            try
            {
                f.Receive(new MoneyBalanceReplyPacket
                {
                    MoneyData = { TransactionID = UUID.Random(), TransactionSuccess = success, MoneyBalance = 100,
                        Description = Utils.StringToBytes(success ? "Payment sent" : "Insufficient funds") },
                    TransactionInfo = { SourceID = f.Owner, DestID = friend.UUID, Amount = 12,
                        TransactionType = (int)MoneyTransactionType.Gift, ItemDescription = Array.Empty<byte>() }
                });
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { session.StateChanged -= Changed; }
        }
        await Reply(true);
        Check(session.Balance == 100 && lines.Any(line => line.Contains("Payment confirmed: 12 L$ to Recipient Resident")),
            "Server confirmation or balance update was missing");
        await Reply(false);
        Check(lines.Any(line => line.Contains("Payment failed: Insufficient funds")), "A server rejection was not displayed");
    }),
    ("Standing stops furniture linkset animations and preserves attachment animations", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var furniture = f.Prim(200);
        var seat = f.Prim(201, furniture.LocalID);
        var script = f.Prim(202, furniture.LocalID);
        var rootAnimation = UUID.Random();
        var childAnimation = UUID.Random();
        var attachmentAnimation = UUID.Random();
        f.ChangeSeat(seat.LocalID);
        f.Animations((rootAnimation, 1, furniture.ID), (childAnimation, 2, script.ID),
            (attachmentAnimation, 3, f.Attachment.ID), (Animations.SIT, 4, UUID.Zero));
        f.ChangeSeat(0);
        var stop = packets().OfType<AgentAnimationPacket>().Single();
        Check(stop.Header.Reliable && stop.AgentData.AgentID == f.Owner, "Cleanup was not sent reliably for the seated account");
        Check(stop.AnimationList.All(animation => !animation.StartAnim) &&
            stop.AnimationList.Select(animation => animation.AnimID).ToHashSet().SetEquals(new[] { rootAnimation, childAnimation }),
            "Cleanup missed a furniture animation or stopped an attachment/default animation");
        f.ChangeSeat(0);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "An unchanged seat caused another cleanup");
        return Task.CompletedTask;
    }),
    ("A direct furniture switch stops only the previous furniture's pose", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var oldSeat = f.Prim(200);
        var newSeat = f.Prim(300);
        var oldPose = UUID.Random();
        var newPose = UUID.Random();
        f.ChangeSeat(oldSeat.LocalID);
        // New animation updates can arrive before the avatar's seat update.
        f.Animations((oldPose, 1, oldSeat.ID), (newPose, 2, newSeat.ID));
        f.ChangeSeat(newSeat.LocalID);
        var stop = packets().OfType<AgentAnimationPacket>().Single();
        Check(stop.AnimationList.Length == 1 && stop.AnimationList[0].AnimID == oldPose && !stop.AnimationList[0].StartAnim,
            "The old pose was not stopped independently of the new furniture");
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == newPose,
            "The new furniture's pose was not cleaned when subsequently standing");
        return Task.CompletedTask;
    }),
    ("A new seat can reuse an animation without a stale stop cancelling it", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var oldSeat = f.Prim(200);
        var newSeat = f.Prim(300);
        var pose = UUID.Random();
        f.ChangeSeat(oldSeat.LocalID);
        f.Animations((pose, 1, oldSeat.ID));
        f.Animations((pose, 2, newSeat.ID));
        f.ChangeSeat(newSeat.LocalID);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "A restarted animation from the new seat was stopped");
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "The animation's new source was not retained");
        return Task.CompletedTask;
    }),
    ("Animation snapshots retain omitted sources only for the same running instance", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var seat = f.Prim(200);
        var pose = UUID.Random();
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 1, seat.ID));
        f.Animations((pose, 1, UUID.Zero));
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "An omitted source lost the previous furniture association");
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 2, seat.ID));
        f.Animations((pose, 3, UUID.Zero));
        f.ChangeSeat(0);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "A restarted animation inherited a stale furniture source");
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 4, seat.ID));
        f.Animations();
        f.ChangeSeat(0);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "An animation already stopped by the script was stopped again");
        return Task.CompletedTask;
    }),
    ("Moving between linked seats preserves the furniture's animation", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var furniture = f.Prim(200);
        var oldSeat = f.Prim(201, furniture.LocalID);
        var newSeat = f.Prim(202, furniture.LocalID);
        var pose = UUID.Random();
        f.ChangeSeat(oldSeat.LocalID);
        f.Animations((pose, 1, furniture.ID));
        f.ChangeSeat(newSeat.LocalID);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "A move within the same furniture stopped its pose");
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "Leaving the linked furniture did not clean its pose");
        return Task.CompletedTask;
    }),
    ("Standing can clean a seat removed from the object cache", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var furniture = f.Prim(200);
        var seat = f.Prim(201, furniture.LocalID);
        var pose = UUID.Random();
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 1, furniture.ID));
        f.Simulator.ObjectsPrimitives.TryRemove(seat.LocalID, out _);
        f.Simulator.ObjectsPrimitives.TryRemove(furniture.LocalID, out _);
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "Removing the old seat from the cache lost its animation sources");
        return Task.CompletedTask;
    }),
    ("Seat cleanup ignores other avatars and regions and remains active with RLV disabled", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var seat = f.Prim(200);
        var pose = UUID.Random();
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 1, seat.ID));
        f.ReceiveAnimations(UUID.Random(), f.Simulator, (pose, 2, f.Attachment.ID));
        f.ChangeSeat(0, avatarId: UUID.Random(), oldSeat: seat.LocalID);
        using var neighbor = new Simulator(f.Client, new IPEndPoint(IPAddress.Loopback, 13001), 2);
        f.ReceiveAnimations(f.Owner, neighbor, (pose, 3, f.Attachment.ID));
        f.ChangeSeat(0, oldSeat: seat.LocalID, simulator: neighbor);
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "Another avatar's stand cleaned this account's animation");
        f.Rlv.SetEnabled(false);
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "Disabling RLV disabled normal furniture cleanup or another avatar replaced the animation source");
        return Task.CompletedTask;
    }),
    ("RLV stand locks do not trigger cleanup before a real stand", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        var seat = f.Prim(200);
        var pose = UUID.Random();
        f.ChangeSeat(seat.LocalID);
        f.Animations((pose, 1, seat.ID));
        await f.Command("@unsit=n,unsit=force");
        Check(!packets().OfType<AgentAnimationPacket>().Any(), "A blocked stand stopped the furniture animation");
        await f.Command("@unsit=y,unsit=force");
        f.ChangeSeat(0);
        Check(packets().OfType<AgentAnimationPacket>().Single().AnimationList.Single().AnimID == pose,
            "An allowed RLV stand did not clean the furniture animation");
    }),
    ("Typed chat channel prefixes route only the payload to the chosen channel", () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        SetConnected(session, true);
        try
        {
            foreach (var (input, channel, payload) in new[]
            {
                ("/9 testcommand on", 9, "testcommand on"),
                ("/-9 testcommand off", -9, "testcommand off"),
                ("/+9   more  words", 9, "more  words"),
                ("/0 public message", 0, "public message"),
                ("ordinary nearby chat", 0, "ordinary nearby chat"),
                ("/me waves", 0, "/me waves")
            })
            {
                Check(session.SendNearbyChat(input), "Chat input was unexpectedly rejected");
                var sent = packets().Single(p => p is ChatFromViewerPacket or ScriptDialogReplyPacket);
                var actual = sent switch
                {
                    ChatFromViewerPacket chat => (chat.ChatData.Channel, Utils.BytesToString(chat.ChatData.Message)),
                    ScriptDialogReplyPacket negative => (negative.Data.ChatChannel, Utils.BytesToString(negative.Data.ButtonLabel)),
                    _ => throw new InvalidOperationException("Unexpected chat packet")
                };
                Check(actual == (channel, payload),
                    $"Chat was sent on the wrong channel or included the prefix: {input}");
            }
            Check(!session.SendNearbyChat("/2147483648 private command"), "An overflowing channel was accepted");
            Check(!session.SendNearbyChat("/9   "), "An empty channel command was accepted");
            Check(!packets().Any(p => p is ChatFromViewerPacket or ScriptDialogReplyPacket), "An invalid channel command was sent");
        }
        finally { SetConnected(session, false); }
        return Task.CompletedTask;
    }),
    ("Typed channel commands respect RLV channel locks and public chat redirection", async () =>
    {
        using var session = new AccountSession(action => action());
        using var f = new Fixture(session);
        var packets = f.CapturePackets();
        SetConnected(session, true);
        try
        {
            await f.Command("@sendchat=n,chatnormal=n,emote=n,redirchat:37=n");
            Check(session.SendNearbyChat("/9 /me testcommand on"), "Public chat restrictions blocked a channel command");
            var command = packets().OfType<ChatFromViewerPacket>().Single();
            Check(command.ChatData.Channel == 9 && command.ChatData.Type == (byte)ChatType.Normal &&
                Utils.BytesToString(command.ChatData.Message) == "/me testcommand on", "A channel command was redirected or altered");
            Check(session.SendNearbyChat("public message"), "Public chat redirection failed");
            Check(packets().OfType<ChatFromViewerPacket>().Single().ChatData.Channel == 37, "Public chat was not redirected");
            await f.Command("@sendchannel_except:9=n");
            Check(!session.SendNearbyChat("/9 testcommand off"), "A channel-specific RLV lock was ignored");
            await f.Command("@sendchannel=n");
            Check(!session.SendNearbyChat("/10 testcommand on"), "The RLV sendchannel restriction was ignored");
            Check(!packets().OfType<ChatFromViewerPacket>().Any(), "Blocked channel commands were sent or leaked to public chat");
        }
        finally { SetConnected(session, false); }
    }),
    ("Version replies use the script's channel", async () =>
    {
        using var f = new Fixture();
        await f.Command("@versionnum=123,versionnew=456");
        Check(f.Replies.Any(r => r == (123, RlvService.RLVVersionNum)), "Version number reply missing");
        Check(f.Replies.Any(r => r.Channel == 456 && r.Text.Contains("RestrainedLove")), "Version text reply missing");
        await f.Command("@versionnum=0");
        Check(f.Replies.All(r => r.Channel != 0), "An automatic reply escaped to public chat");
    }),
    ("Commands and restrictions stay within their account", async () =>
    {
        using var a = new Fixture();
        using var b = new Fixture();
        await a.Command("@sendchat=n,showloc=n,shownames=n,shownearby=n,showinv=n");
        Check(!a.Rlv.Service.Permissions.CanSendChat() && !a.Rlv.Service.Permissions.CanShowLoc(), "Restrictions missing");
        Check(!a.Rlv.Service.Permissions.CanShowNames(Guid.NewGuid()) && !a.Rlv.Service.Permissions.CanShowNearby(), "Privacy restrictions missing");
        Check(!a.Rlv.Service.Permissions.CanShowInv(), "Inventory restriction missing");
        Check(b.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Restrictions leaked to another account");
        await a.Command("@clear");
        Check(a.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Clear did not remove restrictions");
    }),
    ("Only owned object owner-say commands are intercepted", async () =>
    {
        using var f = new Fixture();
        ChatEventArgs Chat(ChatSourceType source, ChatType type, UUID owner) => new(f.Simulator,
            "@sendchat=n", ChatAudibleLevel.Fully, type, source, "Object", f.Attachment.ID, owner, Vector3.Zero);
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Agent, ChatType.OwnerSay, f.Owner)), "Avatar command accepted");
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.Normal, f.Owner)), "Public object chat accepted");
        Check(!f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.OwnerSay, UUID.Random())), "Another owner's command accepted");
        Check(f.Rlv.TryHandleChat(Chat(ChatSourceType.Object, ChatType.OwnerSay, f.Owner)), "Owner-say command ignored");
        await f.Command("@versionnum=789");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "Queued owner-say restriction missing");
    }),
    ("Actual attachments and inventory links share the detach lock", async () =>
    {
        using var f = new Fixture();
        await f.Command("@detach=n", f.Attachment.ID.Guid);
        Check(!f.Rlv.CanDetach(f.Object) && !f.Rlv.CanDetach(f.ObjectLink), "Locked attachment could be detached");
        Check(!f.Rlv.CanModifyInventory(f.ObjectLink), "Locked attachment link could be modified");
        await f.Command("@clear", f.Attachment.ID.Guid);
        Check(f.Rlv.CanDetach(f.Object) && f.Rlv.CanDetach(f.ObjectLink), "Attachment remained locked after clear");
    }),
    ("Attachment point locks cover explicit Add To actions", async () =>
    {
        using var f = new Fixture();
        await f.Command("@addattach:chest=n,remattach:chest=n");
        Check(!f.Rlv.CanAdd(f.Object, AttachmentPoint.Chest), "Locked Add To point allowed");
        Check(f.Rlv.CanAdd(f.Object, AttachmentPoint.LeftHand), "Unrestricted Add To point blocked");
        Check(!f.Rlv.CanDetach(f.Object), "Attachment point removal lock ignored");
        await f.Command("@addoutfit:shirt=n,remoutfit:shirt=n");
        Check(!f.Rlv.CanAdd(f.Shirt, null) && !f.Rlv.CanDetach(f.Shirt), "Wearable locks ignored");
    }),
    ("Replacement cannot remove a locked attachment", async () =>
    {
        using var f = new Fixture();
        var replacement = new InventoryObject(UUID.Random()) { Name = "Replacement", ParentUUID = f.Object.ParentUUID,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        f.Client.Inventory.Store!.UpdateNodeFor(replacement);
        await f.Command("@detach=n", f.Attachment.ID.Guid);
        try
        {
            await f.Rlv.AddAsync(replacement, AttachmentPoint.Chest, true, CancellationToken.None);
            throw new Exception("Replacement was allowed to remove a locked object");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("locked by RLV")) { }
    }),
    ("A command from a linked child applies to the whole attachment", async () =>
    {
        using var f = new Fixture();
        var child = new Primitive { ID = UUID.Random(), LocalID = 101, ParentID = f.Attachment.LocalID };
        f.Simulator.ObjectsPrimitives[child.LocalID] = child;
        var chat = new ChatEventArgs(f.Simulator, "@detach=n", ChatAudibleLevel.Fully, ChatType.OwnerSay,
            ChatSourceType.Object, "Child", child.ID, f.Owner, Vector3.Zero);
        Check(f.Rlv.TryHandleChat(chat), "Child command was not accepted");
        await f.Command("@versionnum=123");
        Check(!f.Rlv.CanDetach(f.Object), "Child restriction did not lock its parent attachment");
    }),
    ("Shared inventory queries resolve links and physical attachment points", async () =>
    {
        using var f = new Fixture();
        var map = f.Rlv.BuildInventoryMap();
        Check(map.TryGetFolderFromPath("Cuffs", false, out var folder), "Shared folder missing");
        var attached = folder!.Items.Single(i => i.Id == f.Object.UUID.Guid);
        Check(attached.IsLink && attached.AttachedPrimId == f.Attachment.ID.Guid && attached.AttachedTo == RlvAttachmentPoint.Chest,
            "Shared link lost its target or live attachment state");
        Check(folder.Items.Single(i => i.Id == f.Unworn.UUID.Guid).WornOn == null, "Unworn wearable reported as worn");
        await f.Command("@getinv=1001,getattach:chest=1002,getoutfit:shirt=1003,getinvworn:Cuffs=1004");
        Check(f.Replies.Any(r => r == (1001, "Cuffs")), "Shared folder query returned the wrong folders");
        Check(f.Replies.Any(r => r == (1002, "1")) && f.Replies.Any(r => r == (1003, "1")), "Worn-state query replies incorrect");
        Check(f.Replies.Any(r => r.Channel == 1004 && r.Text.Length > 0), "Shared worn-state reply missing");
    }),
    ("Shared folder paths accept leading and trailing separators", async () =>
    {
        using var f = new Fixture();
        await f.Command("@getinvworn:Cuffs/=1051,getinvworn:/Cuffs/=1052");
        Check(f.Replies.Contains((1051, "|22")) && f.Replies.Contains((1052, "|22")),
            "A trailing separator made an existing outfit folder disappear");
    }),
    ("An unloaded inventory discovers the real #RLV root and resolves outfit links", async () =>
    {
        using var f = new Fixture();
        var oldStore = f.Client.Inventory.Store!;
        var outfits = new InventoryFolder(UUID.Random()) { Name = "Midori_Outfits", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        var outfit = new InventoryFolder(UUID.Random()) { Name = "Casual", ParentUUID = outfits.UUID, OwnerID = f.Owner };
        oldStore.UpdateNodeFor(outfits);
        oldStore.UpdateNodeFor(outfit);
        f.SharedFolder.Name = "Accessories";
        f.SharedFolder.ParentUUID = outfit.UUID;
        oldStore.UpdateNodeFor(f.SharedFolder);
        using var server = new InventoryServer(f.Client, f.Simulator);
        var store = new LibreMetaverse.Inventory(f.Client, f.Owner) { RootFolder = oldStore.RootFolder };
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client.Inventory, store);
        await f.Command("@getinv=1101,getinv:Midori_Outfits/Casual/=1104,getinvworn:Midori_Outfits/Casual/Accessories/=1102,getpathnew:chest=1103");
        Check(f.Replies.Contains((1101, "Midori_Outfits")), "#RLV was not discovered from the server's root inventory response");
        Check(f.Replies.Contains((1104, "Accessories")), "A nested outfit path was not loaded from the partial skeleton");
        Check(f.Replies.Contains((1102, "|22")), "Shared links or their worn state were missing from the unloaded inventory");
        Check(f.Replies.Contains((1103, "Midori_Outfits/Casual/Accessories")), "Reported outfit path was not relative to #RLV");
        Check(server.FolderRequests.Contains(f.SharedFolder.UUID), "Shared outfit contents were never fetched");
    }),
    ("Folder listing replies do not depend on an unrelated outfit loading", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        server.FailedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getinv=1201");
        Check(f.Replies.Contains((1201, "Cuffs")), "An unrelated folder's failed contents fetch prevented the root folder listing");
        Check(!server.FolderRequests.Contains(f.SharedFolder.UUID), "Listing root folder names fetched an outfit's contents");
    }),
    ("Attachover and detach resolve actual item IDs without loading unrelated outfits", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        f.Shirt.ParentUUID = f.Object.ParentUUID;
        f.Unworn.ParentUUID = f.Object.ParentUUID;
        store.UpdateNodeFor(f.Shirt);
        store.UpdateNodeFor(f.Unworn);
        var other = new InventoryFolder(UUID.Random()) { Name = "Other outfit", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        store.UpdateNodeFor(other);
        using var server = new InventoryServer(f.Client, f.Simulator);
        server.FailedFolders.Add(other.UUID);
        // Simulate login's folder skeleton and an outfit link whose object is not rezzed yet.
        store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        store.RemoveNodeFor(f.Object);
        store.RemoveNodeFor(f.ObjectLink);
        f.Simulator.ObjectsPrimitives.TryRemove(f.Attachment.LocalID, out _);
        var packets = f.CapturePackets();
        await f.Command("@attachover:/Cuffs/=force");
        var added = packets().OfType<RezSingleAttachmentFromInvPacket>().SingleOrDefault();
        Check(added != null && added.ObjectData.ItemID == f.Object.UUID && added.ObjectData.AttachmentPt == (128 | (byte)AttachmentPoint.Chest),
            "Attachover did not request the actual linked object at its attachment point with add semantics");
        await f.Command("@detach:Cuffs/=force");
        var removed = packets().OfType<DetachAttachmentIntoInvPacket>().SingleOrDefault();
        Check(removed?.ObjectData.ItemID == f.Object.UUID, "Detach did not request removal of the actual linked object");
        Check(!server.FolderRequests.Contains(other.UUID), "An outfit action depended on another outfit's unavailable contents");
    }),
    ("A cached root missing #RLV is refreshed before reporting an empty inventory", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        var root = f.Client.Inventory.Store!.RootFolder!;
        var store = new LibreMetaverse.Inventory(f.Client, f.Owner) { RootFolder = root };
        store.GetNodeFor(root.UUID).NeedsUpdate = false;
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client.Inventory, store);
        await f.Command("@getinv=1301");
        Check(f.Replies.Contains((1301, "Cuffs")), "An outdated cached root hid #RLV");
        Check(server.FolderRequests.Contains(root.UUID), "The missing shared root was never checked with the server");
    }),
    ("Current outfit queries fetch unresolved links outside #RLV", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        f.Shirt.ParentUUID = f.Object.ParentUUID;
        store.UpdateNodeFor(f.Shirt);
        using var server = new InventoryServer(f.Client, f.Simulator);
        store.RemoveNodeFor(f.Shirt);
        store.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        server.FailedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getoutfit:shirt=1401");
        Check(f.Replies.Contains((1401, "1")), "An unresolved Current Outfit link hid a worn wearable outside #RLV");
        Check(server.FolderRequests.Count == 0, "A wearable query fetched shared folders unnecessarily");
    }),
    ("Failed folder parsing remains retryable", async () =>
    {
        using var f = new Fixture();
        using var server = new InventoryServer(f.Client, f.Simulator);
        var node = f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID);
        node.NeedsUpdate = true;
        server.MalformedFolders.Add(f.SharedFolder.UUID);
        await f.Command("@getinvworn:Cuffs=1501");
        Check(node.NeedsUpdate && f.Replies.All(r => r.Channel != 1501), "A failed server response was accepted as a loaded outfit");
        server.MalformedFolders.Clear();
        await f.Command("@getinvworn:Cuffs=1502");
        Check(f.Replies.Contains((1502, "|22")), "The failed folder was not retried successfully");
    }),
    ("An action through one link respects another folder's lock after a refresh", async () =>
    {
        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        var other = new InventoryFolder(UUID.Random()) { Name = "Locked outfit", ParentUUID = f.SharedFolder.ParentUUID, OwnerID = f.Owner };
        store.UpdateNodeFor(other);
        store.GetNodeFor(other.UUID).NeedsUpdate = false;
        var link = new InventoryItem(UUID.Random()) { Name = f.Object.Name, ParentUUID = other.UUID, OwnerID = f.Owner,
            AssetType = AssetType.Link, InventoryType = InventoryType.Object, AssetUUID = f.Object.UUID };
        store.UpdateNodeFor(link);
        await f.Command("@detachallthis:Locked outfit=n");
        using var server = new InventoryServer(f.Client, f.Simulator);
        store.RemoveNodeFor(link);
        store.GetNodeFor(other.UUID).NeedsUpdate = true;
        var packets = f.CapturePackets();
        await f.Command("@detach:Cuffs=force");
        Check(server.FolderRequests.Contains(other.UUID), "The locked folder's link was not refreshed");
        Check(!packets().OfType<DetachAttachmentIntoInvPacket>().Any(), "A folder lock was bypassed through another inventory link");
    }),
    ("Neighboring simulators cannot replace this avatar's attachment state", () =>
    {
        using var f = new Fixture();
        using var neighbor = new Simulator(f.Client, new IPEndPoint(IPAddress.Loopback, 13001), 2);
        var other = new Primitive { ID = UUID.Random(), LocalID = 200, ParentID = f.Client.Self.LocalID,
            IsAttachment = true, NameValues = f.Attachment.NameValues };
        other.PrimData.AttachmentPoint = AttachmentPoint.LeftHand;
        neighbor.ObjectsPrimitives[other.LocalID] = other;
        f.Client.Network.Simulators.Insert(0, neighbor);
        var item = f.Rlv.BuildInventoryMap().GetItemsById(f.Object.UUID.Guid).Single();
        Check(item.AttachedPrimId == f.Attachment.ID.Guid && item.AttachedTo == RlvAttachmentPoint.Chest,
            "A simulator-local ID collision changed the current avatar's attachment state");
        f.Client.Network.Simulators.Remove(neighbor);
        return Task.CompletedTask;
    }),
    ("Folder locks prevent removal through links and folder mutations", async () =>
    {
        using var f = new Fixture();
        await f.Command("@detachallthis:Cuffs=n");
        Check(!f.Rlv.CanDetach(f.ObjectLink), "Shared folder removal lock ignored");
        Check(!f.Rlv.CanModifyInventory(f.SharedFolder), "Locked folder could be renamed or trashed");
        await f.Command("@clear");
        Check(f.Rlv.CanDetach(f.ObjectLink), "Shared folder lock survived clear");
    }),
    ("Disable clears locks and cancels queued commands", async () =>
    {
        using var f = new Fixture();
        var commands = Enumerable.Range(0, 50).Select(_ => f.Command("@sendchat=n")).ToArray();
        f.Rlv.SetEnabled(false);
        try { await Task.WhenAll(commands); } catch (OperationCanceledException) { }
        Check(!f.Rlv.Enabled && f.Rlv.Service.Restrictions.FindRestrictions().Count == 0, "Disable left restrictions behind");
        Check(f.Rlv.CanAdd(f.Object, AttachmentPoint.Chest) && f.Rlv.CanDetach(f.ObjectLink), "Disabled RLV still blocked inventory");
        f.Rlv.SetEnabled(true);
        await f.Command("@versionnum=123");
        Check(f.Rlv.Service.Permissions.CanSendChat(), "Old queued command reached the new engine");
    }),
    ("Unsupported visual commands are advertised and valid batch commands still run", async () =>
    {
        using var f = new Fixture();
        await f.Command("@setenv_sky:blue=force,unknown=n,sendchat=n,getblacklist=301");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "Unsupported command prevented the valid command from running");
        Check(f.Replies.Any(r => r.Channel == 301 && r.Text.Contains("setenv")), "Unavailable visual features missing from blacklist");
        var first = f.Command("@sendchat=n");
        var second = f.Command("@sendchat=y");
        await Task.WhenAll(first, second);
        Check(f.Rlv.Service.Permissions.CanSendChat(), "Commands were processed out of arrival order");
    }),
    ("A failed inventory query does not discard later restrictions in the same message", async () =>
    {
        using var f = new Fixture();
        f.Client.Inventory.Store!.GetNodeFor(f.SharedFolder.UUID).NeedsUpdate = true;
        await f.Command("@getinvworn:Cuffs=321,sendchat=n");
        Check(!f.Rlv.Service.Permissions.CanSendChat(), "A failed query discarded the restriction after it");
    }),
    ("Touch restrictions distinguish body attachments and HUDs", async () =>
    {
        using var f = new Fixture();
        await f.Command("@touchattachself=n");
        Check(!f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.Chest), "Body attachment touch lock ignored");
        Check(f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.HUDCenter), "Body touch lock incorrectly blocked HUD");
        await f.Command("@interact=n");
        Check(!f.Rlv.CanTouch(f.Attachment.ID, AttachmentPoint.HUDCenter), "Interaction lock ignored");
    })
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void SetConnected(AccountSession session, bool connected) =>
    typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(session.Net, connected);

static void ExpectRejected(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { return; }
    throw new InvalidOperationException("An invalid friend action was accepted");
}

static async Task FriendsEvent(AccountSession session, Action change)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Changed(AccountSession account) => completion.TrySetResult();
    session.FriendsChanged += Changed;
    try { change(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    finally { session.FriendsChanged -= Changed; }
}

static async Task ReceiveIm(Fixture fixture, ImprovedInstantMessagePacket packet)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Received(object? sender, InstantMessageEventArgs e) => completion.TrySetResult();
    fixture.Client.Self.IM += Received;
    try { fixture.Receive(packet); await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    finally { fixture.Client.Self.IM -= Received; }
}

sealed class Fixture : IDisposable
{
    public UUID Owner { get; } = UUID.Random();
    public GridClient Client { get; }
    public CurrentOutfitFolder Outfit { get; }
    public RlvSession Rlv { get; }
    public Simulator Simulator { get; }
    public Primitive Attachment { get; }
    public InventoryObject Object { get; }
    public InventoryItem ObjectLink { get; }
    public InventoryWearable Shirt { get; }
    public InventoryWearable Unworn { get; }
    public InventoryFolder SharedFolder { get; }
    public List<(int Channel, string Text)> Replies { get; } = new();
    private readonly Guid _issuer = Guid.NewGuid();
    private readonly bool _ownsSession;

    public Fixture(AccountSession? session = null)
    {
        Client = session?.Client ?? new();
        _ownsSession = session == null;
        typeof(AgentManager).GetProperty(nameof(AgentManager.AgentID))!.SetValue(Client.Self, Owner);
        typeof(AgentManager).GetField("localID", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Self, 42u);
        var store = new LibreMetaverse.Inventory(Client, Owner);
        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Inventory, store);
        var root = Folder("My Inventory", UUID.Zero, FolderType.Root);
        store.RootFolder = root;
        var shared = Folder("#RLV", root.UUID);
        SharedFolder = Folder("Cuffs", shared.UUID);
        var objects = Folder("Objects", root.UUID);
        var cof = Folder("Current Outfit", root.UUID, FolderType.CurrentOutfit);
        Outfit = session?.Outfit ?? new CurrentOutfitFolder(Client);
        typeof(CurrentOutfitFolder).GetProperty(nameof(CurrentOutfitFolder.COF))!.SetValue(Outfit, cof);
        Object = new InventoryObject(UUID.Random()) { Name = "Cuffs (chest)", ParentUUID = objects.UUID, OwnerID = Owner,
            AssetType = AssetType.Object, InventoryType = InventoryType.Object, AttachPoint = AttachmentPoint.Chest };
        store.UpdateNodeFor(Object);
        ObjectLink = Link(Object, SharedFolder.UUID);
        Link(Object, cof.UUID);
        Shirt = new InventoryWearable(UUID.Random()) { Name = "Shirt", ParentUUID = SharedFolder.UUID, OwnerID = Owner,
            AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable, WearableType = WearableType.Shirt };
        store.UpdateNodeFor(Shirt);
        Link(Shirt, cof.UUID);
        Unworn = new InventoryWearable(UUID.Random()) { Name = "Pants", ParentUUID = SharedFolder.UUID, OwnerID = Owner,
            AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable, WearableType = WearableType.Pants };
        store.UpdateNodeFor(Unworn);
        foreach (var id in new[] { root.UUID, shared.UUID, SharedFolder.UUID, objects.UUID, cof.UUID }) store.GetNodeFor(id).NeedsUpdate = false;
        Simulator = new Simulator(Client, new IPEndPoint(IPAddress.Loopback, 13000), 1);
        Client.Network.Simulators.Add(Simulator);
        Client.Network.CurrentSim = Simulator;
        Attachment = new Primitive { ID = UUID.Random(), LocalID = 100, ParentID = Client.Self.LocalID, IsAttachment = true,
            NameValues = new[] { new NameValue($"AttachItemID STRING RW SV {Object.UUID}") } };
        Attachment.PrimData.AttachmentPoint = AttachmentPoint.Chest;
        Simulator.ObjectsPrimitives[Attachment.LocalID] = Attachment;
        Rlv = session?.Rlv ?? new RlvSession(Client, Outfit, action => action(), (channel, text) => Replies.Add((channel, text)));

        InventoryFolder Folder(string name, UUID parent, FolderType type = FolderType.None)
        {
            var folder = new InventoryFolder(UUID.Random()) { Name = name, ParentUUID = parent, OwnerID = Owner, PreferredType = type };
            store.UpdateNodeFor(folder);
            return folder;
        }
        InventoryItem Link(InventoryItem target, UUID parent)
        {
            var link = new InventoryItem(UUID.Random()) { Name = target.Name, AssetType = AssetType.Link,
                InventoryType = target.InventoryType, AssetUUID = target.UUID, ParentUUID = parent, OwnerID = Owner };
            store.UpdateNodeFor(link);
            return link;
        }
    }

    public Task Command(string text, Guid? issuer = null) => Rlv.ProcessCommandAsync(text, issuer ?? _issuer, "Test object");

    private ConcurrentDictionary<UUID, FriendInfo> FriendStore => (ConcurrentDictionary<UUID, FriendInfo>)
        typeof(FriendsManager).GetField("m_FriendList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client.Friends)!;

    public FriendInfo Friend(string name, bool online = false, UUID? id = null)
    {
        var friend = (FriendInfo)Activator.CreateInstance(typeof(FriendInfo), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { id ?? UUID.Random(), FriendRights.CanSeeOnline, FriendRights.CanSeeOnline }, null)!;
        friend.Name = name;
        friend.IsOnline = online;
        FriendStore[friend.UUID] = friend;
        return friend;
    }

    public void RemoveFriend(UUID id) => FriendStore.TryRemove(id, out _);

    public Primitive Prim(uint localId, uint parent = 0)
    {
        var primitive = new Primitive { ID = UUID.Random(), LocalID = localId, ParentID = parent };
        Simulator.ObjectsPrimitives[localId] = primitive;
        return primitive;
    }

    public void ChangeSeat(uint seat, UUID? avatarId = null, uint? oldSeat = null, Simulator? simulator = null)
    {
        var id = avatarId ?? Owner;
        var avatar = new Avatar { ID = id, LocalID = id == Owner ? Client.Self.LocalID : 999 };
        typeof(ObjectManager).GetMethod("SetAvatarSittingOn", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Client.Objects, new object[] { simulator ?? Simulator, avatar, seat, oldSeat ?? Client.Self.SittingOn });
    }

    public void Animations(params (UUID Id, int Sequence, UUID Source)[] animations) => ReceiveAnimations(Owner, Simulator, animations);

    public void ReceiveAnimations(UUID avatar, Simulator simulator, params (UUID Id, int Sequence, UUID Source)[] animations)
    {
        var packet = new AvatarAnimationPacket();
        packet.Sender.ID = avatar;
        packet.AnimationList = animations.Select(animation => new AvatarAnimationPacket.AnimationListBlock
            { AnimID = animation.Id, AnimSequenceID = animation.Sequence }).ToArray();
        packet.AnimationSourceList = animations.Select(animation => new AvatarAnimationPacket.AnimationSourceListBlock
            { ObjectID = animation.Source }).ToArray();
        Receive(packet, simulator);
    }

    public void Receive(Packet packet, Simulator? simulator = null)
    {
        var events = (PacketEventDictionary)typeof(NetworkManager).GetField("PacketEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Client.Network)!;
        events.InvokeRaiseEvent(packet.Type, packet, simulator ?? Simulator);
    }

    public ImprovedInstantMessagePacket PrivateIm(UUID sender, string name, string text,
        InstantMessageDialog dialog = InstantMessageDialog.MessageFromAgent) => new()
    {
        AgentData = { AgentID = sender },
        MessageBlock = { FromAgentName = Utils.StringToBytes(name), Message = Utils.StringToBytes(text),
            ToAgentID = Owner, ID = sender ^ Owner, Dialog = (byte)dialog, BinaryBucket = Array.Empty<byte>() }
    };

    public Func<List<Packet>> CapturePackets()
    {
        var outbox = Channel.CreateUnbounded<NetworkManager.OutgoingPacket>();
        typeof(NetworkManager).GetField("_packetOutbox", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client.Network, outbox);
        return () =>
        {
            var packets = new List<Packet>();
            while (outbox.Reader.TryRead(out var sent))
            {
                var end = sent.Buffer.DataLength - 1;
                packets.Add(Packet.BuildPacket(sent.Buffer.Data, ref end, new byte[65536]));
            }
            return packets;
        };
    }
    public void Dispose()
    {
        if (_ownsSession)
        {
            Rlv.Dispose();
            Outfit.Dispose();
        }
        Simulator.Dispose();
    }
}
