using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class ChatLinkChecks
{
    public static Task Parsing()
    {
        var first = UUID.Random(); var second = UUID.Random();
        var text = $"[22:54] HUD: /me [ 😀 Momoi Pawprint ({Url(first)}) ]\nThen <{Url(second).ToUpperInvariant()}>.";
        var spans = AvatarProfileLinks.Parse(text);
        Check(string.Concat(spans.Select(span => span.Text)) == text, "Parsing damaged surrounding text, punctuation or Unicode");
        Check(spans.Where(span => span.AvatarId != UUID.Zero).Select(span => span.AvatarId).SequenceEqual(new[] { first, second }),
            "Multiple profile links or uppercase UUIDs were missed");
        var other = $"https://example.org/{first} secondlife://Region/1/2/3 " +
            $"secondlife:///app/agent/not-a-uuid/about {Url(UUID.Zero)} {Url(first)}ness {Url(first)}/extra {Url(first)}?query";
        Check(AvatarProfileLinks.Parse(other).All(span => span.AvatarId == UUID.Zero) &&
            string.Concat(AvatarProfileLinks.Parse(other).Select(span => span.Text)) == other,
            "Unrelated URLs or invalid/incomplete profile links were changed");
        Check(AvatarProfileLinks.Parse("").Count == 0 && AvatarProfileLinks.Parse("ordinary chat").Single().Text == "ordinary chat",
            "Plain chat was altered");
        return Task.CompletedTask;
    }

    public static async Task Names()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        var otherPackets = otherFixture.CapturePackets();
        Connected(account, true); Connected(other, true);
        try
        {
            var known = fixture.Friend("Momoi Pawprint").UUID;
            otherFixture.Friend("Another Name", id: known);
            var text = $"You feel attracted to Momoi Pawprint ({Url(known)})";
            Check(Render(account, text) == "You feel attracted to Momoi Pawprint (Momoi Pawprint)" &&
                other.FormatChatText(Url(known)).Single().Text == "Another Name", "A cached name was lost or leaked across accounts");
            Check(!packets().Any() && !otherPackets().Any(), "Known friend names caused network lookups");
            var unknown = UUID.Random();
            for (var i = 0; i < 3; i++) Check(Render(account, Url(unknown)) == "Loading name…", "An unresolved link exposed its raw UUID");
            var request = packets().OfType<UUIDNameRequestPacket>().Single();
            Check(request.UUIDNameBlock.Single().ID == unknown, "Repeated rendering caused duplicate lookups or targeted the wrong avatar");
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            account.AvatarNamesChanged += _ => changed.TrySetResult();
            fixture.Receive(new UUIDNameReplyPacket
            {
                UUIDNameBlock = new[] { new UUIDNameReplyPacket.UUIDNameBlockBlock
                    { ID = unknown, FirstName = Utils.StringToBytes("Alice"), LastName = Utils.StringToBytes("Resident") } }
            });
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Render(account, Url(unknown)) == "Alice Resident" && !packets().Any(), "A name reply did not update the profile label/cache");
            Check(Render(other, Url(unknown)) == "Loading name…", "A reply was shared with another grid account");
            Connected(account, false);
            var offline = account.FormatChatText(Url(UUID.Random())).Single();
            Check(offline.Text == "Resident" && !packets().Any(), "A disconnected account requested names or showed UUIDs");
        }
        finally { Connected(account, false); Connected(other, false); }
    }

    public static async Task Restrictions()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        Connected(account, true);
        try
        {
            var id = fixture.Friend("Momoi Pawprint").UUID;
            var opened = new List<UUID>();
            account.AvatarProfileRequested += (source, avatar) =>
            {
                Check(source == account, "Profile click used another account");
                opened.Add(avatar);
            };
            await fixture.Command("@showloc=n");
            Check(Render(account, Url(id)) == "Momoi Pawprint" &&
                !account.RedactText(Url(id)).Contains("location hidden", StringComparison.Ordinal) &&
                account.RedactText("secondlife://Test/128/128/20").Contains("location hidden", StringComparison.Ordinal),
                "Location hiding incorrectly hid a profile or revealed a location");
            account.OpenAvatarProfile(id);
            Check(opened.SequenceEqual(new[] { id }), "A profile link did not open the referenced avatar");
            await fixture.Command("@shownames=n");
            var hidden = account.FormatChatText($"Momoi Pawprint ({Url(id)})");
            Check(string.Concat(hidden.Select(span => span.Text)) == "Resident (Resident)" &&
                hidden.All(span => span.AvatarId == UUID.Zero), "A profile exposed a hidden name or remained clickable");
            account.OpenAvatarProfile(id);
            account.OpenAvatarProfile(UUID.Zero);
            Check(opened.Count == 1 && !packets().Any(), "A hidden/invalid profile opened or sent a name request");
            Check(!account.RedactText(Url(UUID.Random())).Contains("secondlife:", StringComparison.Ordinal),
                "An unknown profile UUID bypassed name hiding");
            await fixture.Command("@shownames=y");
            Check(account.FormatChatText(Url(id)).Single().AvatarId == id, "Unlocking names did not restore profile links");
        }
        finally { Connected(account, false); }
    }

    internal static string Url(UUID id) => $"secondlife:///app/agent/{id}/about";

    public static async Task LabeledLinks()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        Connected(account, true); Connected(other, true);
        try
        {
            var peer = fixture.Friend("Momoi Pawprint", id: ProfileLinkChecks.Payee).UUID;
            var text = $"[{ProfileLinkChecks.Wishlist} Wishlist ♥] [{Url(peer)} My partner] " +
                $"[secondlife:///app/agent/{peer}/pay Spoil me]";
            Check(Render(account, text) == "Wishlist ♥ My partner Spoil me", "Chat labels retained brackets or raw URLs");
            var links = account.FormatChatText(text).Where(span => span.Link != null).Select(span => span.Link!).ToArray();
            var opened = new List<(AccountSession Account, ProfileTextLink Link)>();
            var profiles = new List<(AccountSession Account, UUID Id)>();
            account.ChatLinkRequested += (source, link) => opened.Add((source, link));
            other.ChatLinkRequested += (source, link) => opened.Add((source, link));
            account.AvatarProfileRequested += (source, id) => profiles.Add((source, id));
            foreach (var link in links) account.OpenChatLink(link);
            other.OpenChatLink(links[0]);
            Check(opened.SequenceEqual(new[] { (account, links[0]), (account, links[2]), (other, links[0]) }) &&
                profiles.SequenceEqual(new[] { (account, peer) }) && !packets().OfType<MoneyTransferRequestPacket>().Any(),
                "A labeled link used another account, wrong action or paid without confirmation");
            account.SendNearbyChat(text);
            account.SendInstantMessage(peer, text);
            var group = UUID.Random();
            fixture.Groups((group, "Test Group"));
            fixture.GroupJoin(group, true);
            account.SendGroupMessage(group, text);
            var sent = packets();
            Check(Utils.BytesToString(sent.OfType<ChatFromViewerPacket>().Single().ChatData.Message) == text &&
                sent.OfType<ImprovedInstantMessagePacket>().Where(packet => packet.MessageBlock.Dialog is
                    (byte)InstantMessageDialog.MessageFromAgent or (byte)InstantMessageDialog.SessionSend)
                    .Select(packet => Utils.BytesToString(packet.MessageBlock.Message)).SequenceEqual(new[] { text, text }) &&
                account.Conversations.Single().Messages.Single().Text == text &&
                account.GroupConversations.Single().Messages.Single().Text == text,
                "Display formatting changed an outgoing chat, IM, group packet or stored message");
            await fixture.Command("@shownames=n,showloc=n");
            account.OpenChatLink(links[1]); account.OpenChatLink(links[2]);
            account.OpenChatLink(new ProfileTextLink(ProfileLinkChecks.Location, ProfileLinkAction.Web));
            Check(opened.Count == 3 && profiles.Count == 1, "A stale chat label bypassed RLV permissions");
            await fixture.Command("@shownames=y,showloc=y");
            Connected(account, false);
            account.OpenChatLink(links[2]);
            Check(opened.Count == 3, "A disconnected account opened a payment link");
        }
        finally { Connected(account, false); Connected(other, false); }
    }
    private static string Render(AccountSession account, string text) => string.Concat(account.FormatChatText(text).Select(span => span.Text));
    private static void Connected(AccountSession account, bool value) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
