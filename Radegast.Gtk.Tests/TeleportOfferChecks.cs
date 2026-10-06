using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class TeleportOfferChecks
{
    public static Task Incoming()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        var otherPackets = otherFixture.CapturePackets();
        var offers = new List<TeleportOffer>();
        var notices = new List<AccountNotification>();
        account.TeleportOfferReceived += (_, offer) => offers.Add(offer);
        account.NotificationReceived += (_, notice) => notices.Add(notice);
        other.TeleportOfferReceived += (_, _) => throw new Exception("Offer leaked into another account");
        Connected(account, true); Connected(other, true);
        try
        {
            foreach (var rlv in new[] { false, true })
            {
                account.Rlv.SetEnabled(rlv);
                var message = Offer(fixture.Owner);
                Receive(account, message);
                Receive(account, message);
                Check(offers.Count == (rlv ? 2 : 1) && notices.Count == offers.Count, "An offer was lost or duplicated");
                var notice = notices.Last();
                Check(notice.Category == NotificationCategory.TeleportOffers && notice.Title == "Alice Resident" &&
                    notice.Message == "Teleport offer" && notice.TargetId == message.FromAgentID, "Teleport notification content was incorrect");
            }
            Check(account.Conversations.Count == 0 && !packets().Any() && !otherPackets().Any(), "An unanswered offer created an IM or sent a reply");
            var invalid = Offer(UUID.Random()); Receive(account, invalid);
            invalid = Offer(fixture.Owner); invalid.GroupIM = true; Receive(account, invalid);
            invalid = Offer(fixture.Owner); invalid.FromAgentID = UUID.Zero; Receive(account, invalid);
            invalid = Offer(fixture.Owner); invalid.FromAgentID = fixture.Owner; Receive(account, invalid);
            invalid = Offer(fixture.Owner); invalid.IMSessionID = UUID.Zero; Receive(account, invalid);
            invalid = Offer(fixture.Owner); invalid.Dialog = InstantMessageDialog.RequestLure; Receive(account, invalid);
            Connected(account, false); Receive(account, Offer(fixture.Owner));
            Check(offers.Count == 2 && notices.Count == 2 && !packets().Any(), "Invalid or disconnected offers produced prompts, notices or replies");
        }
        finally { Connected(account, false); Connected(other, false); }
        return Task.CompletedTask;
    }

    public static Task Responses()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        otherFixture.CapturePackets();
        TeleportOffer? offer = null;
        account.TeleportOfferReceived += (_, incoming) => offer = incoming;
        Connected(account, true); Connected(other, true);
        try
        {
            var incoming = Offer(fixture.Owner);
            Receive(account, incoming);
            Rejected(() => other.RespondToTeleportOffer(offer!, true));
            Rejected(() => account.RespondToTeleportOffer(new(offer!.SenderId, offer.LureId), true));
            account.RespondToTeleportOffer(offer!, true);
            var sent = packets().OfType<TeleportLureRequestPacket>().Single();
            Check(sent.Info.AgentID == fixture.Owner && sent.Info.LureID == incoming.IMSessionID &&
                sent.Info.TeleportFlags == (uint)TeleportFlags.ViaLure, "Acceptance used the wrong account or lure ID");
            Rejected(() => account.RespondToTeleportOffer(offer!, true));
            Receive(account, incoming);
            Check(!account.IsTeleportOfferPending(offer!) && !packets().Any(), "An answered offer was reused or accepted twice");
            incoming = Offer(fixture.Owner); Receive(account, incoming);
            account.RespondToTeleportOffer(offer!, false);
            var refused = packets().OfType<ImprovedInstantMessagePacket>().Single();
            Check(refused.AgentData.AgentID == fixture.Owner && refused.MessageBlock.ToAgentID == incoming.FromAgentID &&
                refused.MessageBlock.ID == incoming.IMSessionID && refused.MessageBlock.Dialog == (byte)InstantMessageDialog.DenyTeleport,
                "Refusal did not reach the original sender with the lure ID");
            Rejected(() => account.RespondToTeleportOffer(offer!, false));
            Check(!packets().Any(), "Refusal was sent twice");
        }
        finally { Connected(account, false); Connected(other, false); }
        return Task.CompletedTask;
    }

    public static async Task Restrictions()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        TeleportOffer? offer = null;
        var notices = new List<AccountNotification>();
        account.TeleportOfferReceived += (_, incoming) => offer = incoming;
        account.NotificationReceived += (_, notice) => notices.Add(notice);
        Connected(account, true);
        try
        {
            await fixture.Command("@shownames=n");
            Receive(account, Offer(fixture.Owner));
            Check(notices.Single().Title == "Resident" && account.DisplayFriendName(offer!.SenderId) == "Resident", "An offer revealed a hidden name");
            await fixture.Command("@tplure=n");
            Rejected(() => account.RespondToTeleportOffer(offer!, true));
            Check(!packets().Any(), "A newly applied lure lock permitted acceptance");
            account.RespondToTeleportOffer(offer!, false);
            Check(packets().OfType<ImprovedInstantMessagePacket>().Single().MessageBlock.Dialog == (byte)InstantMessageDialog.DenyTeleport,
                "A lure lock prevented refusal");
            Receive(account, Offer(fixture.Owner));
            Check(notices.Count == 1 && packets().OfType<ImprovedInstantMessagePacket>().Single().MessageBlock.Dialog == (byte)InstantMessageDialog.DenyTeleport,
                "An already blocked lure was not declined silently");
            await fixture.Command("@tplure=y,unsit=n");
            Receive(account, Offer(fixture.Owner));
            typeof(AgentManager).GetField("sittingOn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(account.Client.Self, 100u);
            Rejected(() => account.RespondToTeleportOffer(offer!, true));
            Check(!packets().Any(), "Accepting a lure bypassed the seated unsit lock");
            typeof(AgentManager).GetField("sittingOn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(account.Client.Self, 0u);
            account.RespondToTeleportOffer(offer!, true);
            Check(packets().OfType<TeleportLureRequestPacket>().Count() == 1, "An unsit lock blocked a standing avatar's teleport");
        }
        finally { Connected(account, false); }
    }

    public static async Task AutomaticAndDisconnect()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        var offers = new List<TeleportOffer>();
        account.TeleportOfferReceived += (_, incoming) => offers.Add(incoming);
        Connected(account, true);
        try
        {
            await fixture.Command("@accepttp=n");
            Receive(account, Offer(fixture.Owner));
            Check(offers.Count == 0 && packets().OfType<TeleportLureRequestPacket>().Count() == 1, "RLV automatic acceptance was lost");
            await fixture.Command("@accepttp=y");
            Receive(account, Offer(fixture.Owner));
            Check(offers.Count == 1 && account.IsTeleportOfferPending(offers[0]), "Manual offer was not retained");
            Connected(account, false);
            typeof(AccountSession).GetMethod("SetDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(account, new object[] { "Test disconnect" });
            Check(!account.IsTeleportOfferPending(offers[0]), "Disconnect left a usable teleport offer");
            Rejected(() => account.RespondToTeleportOffer(offers[0], true));
            Connected(account, true);
            Rejected(() => account.RespondToTeleportOffer(offers[0], true));
            Check(!packets().Any(), "An offer survived disconnect or reconnection");
        }
        finally { Connected(account, false); }
    }

    private static InstantMessage Offer(UUID recipient) => new()
    {
        FromAgentID = UUID.Random(), FromAgentName = "Alice Resident", ToAgentID = recipient,
        IMSessionID = UUID.Random(), Dialog = InstantMessageDialog.RequestTeleport, Message = "Join me!"
    };

    private static void Receive(AccountSession account, InstantMessage message) =>
        typeof(AccountSession).GetMethod("OnInstantMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(account,
            new object?[] { null, new InstantMessageEventArgs(message, account.Client.Network.CurrentSim) });
    private static void Connected(AccountSession account, bool connected) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, connected);
    private static void Rejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("An invalid teleport decision was accepted");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
