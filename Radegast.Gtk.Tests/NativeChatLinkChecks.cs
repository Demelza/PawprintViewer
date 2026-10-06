using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Gdk;
using Gtk;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class NativeChatLinkChecks
{
    public static int Run()
    {
        Application.Init();
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-chat-links-" + Guid.NewGuid().ToString("N"));
        using var account = new AccountSession(GtkDispatch.Post);
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        Connected(account, true);
        using var main = new MainWindow(new GlobalSettings(Path.Combine(directory, "settings.json")), new Notices());
        Invoke(main, "AddSession", account);
        main.ShowAll();
        var widgets = Field<Dictionary<AccountSession, SessionWidgets>>(main, "_sessions")[account];
        var nearby = Field<ChatHistoryView>(widgets, "_chatHistory");
        var imPanel = Field<InstantMessagesPanel>(widgets, "_imPanel");
        var groupPanel = Field<GroupChatsPanel>(widgets, "_groupPanel");
        var im = Field<ChatHistoryView>(imPanel, "_history");
        var group = Field<ChatHistoryView>(groupPanel, "_history");
        var views = new[] { nearby, im, group };
        var avatar = UUID.Random();
        var profilePeer = UUID.Random();
        var friend = fixture.Friend("Alice Resident").UUID;
        var peer = fixture.Friend("Sender Resident").UUID;
        var groupId = UUID.Random();
        var message = $"😀 [{ChatLinkChecks.Url(avatar)}], {ChatLinkChecks.Url(friend)} tail";
        nearby.AppendLine("[22:54] HUD: /me " + message);
        account.SendInstantMessage(peer, message);
        fixture.Groups((groupId, "Test Group"));
        fixture.Receive(fixture.GroupIm(groupId, peer, "Sender Resident", message));
        Field<Entry>(imPanel, "_input").Text = "unsent draft";
        var stage = 0;
        var result = 0;
        GLib.Timeout.Add(300, () =>
        {
            try
            {
                switch (stage++)
                {
                    case 0:
                        foreach (var view in views)
                            Check(view.Buffer.Text.Contains("Loading name…") && !view.Buffer.Text.Contains("secondlife:"),
                                "A chat view showed the raw profile URL or missed its message");
                        var requests = packets().OfType<UUIDNameRequestPacket>().SelectMany(packet => packet.UUIDNameBlock).ToArray();
                        Check(requests.Count(block => block.ID == avatar) == 1, "The three chat views duplicated the name lookup");
                        fixture.Receive(new UUIDNameReplyPacket
                        {
                            UUIDNameBlock = new[] { new UUIDNameReplyPacket.UUIDNameBlockBlock
                                { ID = avatar, FirstName = Utils.StringToBytes("Momoi"), LastName = Utils.StringToBytes("Pawprint") } }
                        });
                        break;
                    case 1:
                        foreach (var view in views)
                        {
                            Check(view.Buffer.Text.Contains("😀 [Momoi Pawprint], Alice Resident tail"), "Delayed name resolution damaged a chat line");
                            Check(TagAt(view, "Momoi Pawprint").Name == "avatar-" + avatar &&
                                TagAt(view, "Alice Resident").Name == "avatar-" + friend, "A link pointed at another avatar");
                            Check(!view.Buffer.GetIterAtOffset(view.Buffer.CharCount - 1).Tags.Any() &&
                                !view.Buffer.GetIterAtOffset(Offset(view.Buffer.Text, "tail")).Tags.Any(), "Link tags leaked into plain text or the next line");
                        }
                        Check(Field<Entry>(imPanel, "_input").Text == "unsent draft", "Resolving names discarded an IM draft");
                        Click(nearby, "Momoi Pawprint");
                        break;
                    case 2:
                        var profile = Profiles(main, account)[avatar];
                        Check(packets().OfType<AvatarPropertiesRequestPacket>().Single().AgentData.AvatarID == avatar,
                            "Clicking the name requested a different profile");
                        Properties(account, friend, "Wrong resident");
                        Properties(account, avatar, $"Correct profile\n😀 [{ChatLinkChecks.Url(profilePeer)}], {ChatLinkChecks.Url(friend)} tail",
                            $"First life: {ChatLinkChecks.Url(friend)}.");
                        break;
                    case 3:
                        var shown = Profiles(main, account)[avatar];
                        Check(Field<Label>(shown, "_name").Text == "Momoi Pawprint" &&
                            Field<ChatHistoryView>(shown, "_about").Buffer.Text == "Correct profile\n😀 [Loading name…], Alice Resident tail",
                            "The profile used another resident's reply or retained a raw profile URL");
                        Check(Field<ChatHistoryView>(shown, "_firstLife").Buffer.Text == "First life: Alice Resident.",
                            "First Life text did not resolve a profile link or gained a trailing newline");
                        Check(packets().OfType<UUIDNameRequestPacket>().SelectMany(packet => packet.UUIDNameBlock)
                            .Count(block => block.ID == profilePeer) == 1, "Profile links did not request their own missing names once");
                        var about = Field<ChatHistoryView>(shown, "_about");
                        about.Buffer.SelectRange(about.Buffer.StartIter, about.Buffer.GetIterAtOffset("Correct profile".Length));
                        fixture.Receive(new UUIDNameReplyPacket
                        {
                            UUIDNameBlock = new[] { new UUIDNameReplyPacket.UUIDNameBlockBlock
                                { ID = profilePeer, FirstName = Utils.StringToBytes("Profile"), LastName = Utils.StringToBytes("Resident") } }
                        });
                        shown.ShowAll();
                        ProfileTabs(shown).CurrentPage = 1;
                        break;
                    case 4:
                        var updated = Profiles(main, account)[avatar];
                        var updatedAbout = Field<ChatHistoryView>(updated, "_about");
                        Check(updatedAbout.Buffer.Text == "Correct profile\n😀 [Profile Resident], Alice Resident tail" &&
                            updatedAbout.Buffer.HasSelection && TagAt(updatedAbout, "Profile Resident").Name == "avatar-" + profilePeer &&
                            !updatedAbout.Buffer.GetIterAtOffset(Offset(updatedAbout.Buffer.Text, "tail")).Tags.Any(),
                            "Delayed profile name resolution damaged text, selection or link targets");
                        Click(Field<ChatHistoryView>(updated, "_firstLife"), "Alice Resident");
                        break;
                    case 5:
                        Check(Profiles(main, account).ContainsKey(friend), "A First Life profile link did not open its resident");
                        Profiles(main, account)[friend].CloseProfile();
                        ProfileTabs(Profiles(main, account)[avatar]).CurrentPage = 0;
                        break;
                    case 6:
                        var profileAbout = Field<ChatHistoryView>(Profiles(main, account)[avatar], "_about");
                        profileAbout.Buffer.PlaceCursor(profileAbout.Buffer.StartIter);
                        Click(profileAbout, "Profile Resident");
                        break;
                    case 7:
                        Check(Profiles(main, account).ContainsKey(profilePeer), "A Second Life profile link did not open its resident");
                        Profiles(main, account)[profilePeer].CloseProfile();
                        var closing = Profiles(main, account)[avatar];
                        // Queue a late reply and dismiss before GTK applies it.
                        Properties(account, avatar, "Late reply");
                        closing.CloseProfile(); closing.CloseProfile();
                        Check(Profiles(main, account).Count == 0, "Closing the profile retained the window");
                        Collect();
                        widgets.Tabs.CurrentPage = widgets.Tabs.PageNum(imPanel);
                        break;
                    case 8:
                        Click(im, "Alice Resident");
                        break;
                    case 9:
                        Check(Profiles(main, account).ContainsKey(friend), "An IM profile link opened the previous link's avatar");
                        Profiles(main, account)[friend].CloseProfile();
                        widgets.Tabs.CurrentPage = widgets.Tabs.PageNum(groupPanel);
                        break;
                    case 10:
                        Click(group, "Momoi Pawprint");
                        break;
                    case 11:
                        Check(Profiles(main, account).ContainsKey(avatar), "A group-chat link did not open its avatar's profile");
                        Profiles(main, account)[avatar].CloseProfile();
                        Click(group, "Momoi Pawprint", drag: true);
                        Check(Profiles(main, account).Count == 0, "Dragging across a name activated a profile");
                        foreach (var view in views)
                            for (var i = 0; i < 10; i++) { view.Clear(); view.AppendLine(message); }
                        Collect();
                        break;
                    case 12:
                        Click(group, "Momoi Pawprint");
                        break;
                    case 13:
                        var actions = Profiles(main, account)[avatar];
                        actions.ShowAll();
                        var imAction = Field<global::Gtk.Button>(actions, "_im");
                        var payAction = Field<global::Gtk.Button>(actions, "_pay");
                        var tpAction = Field<global::Gtk.Button>(actions, "_teleport");
                        var friendAction = Field<global::Gtk.Button>(actions, "_friend");
                        var blockAction = Field<global::Gtk.Button>(actions, "_block");
                        Check(imAction.Parent == payAction.Parent && imAction.Parent == tpAction.Parent &&
                            friendAction.Parent == blockAction.Parent && friendAction.Parent != imAction.Parent &&
                            friendAction.Label == "Add Friend" && friendAction.Sensitive && blockAction.Label == "Block",
                            "Profile actions did not use the requested rows or non-friend state");
                        imAction.Click();
                        Check(widgets.Tabs.CurrentPage == widgets.Tabs.PageNum(imPanel) &&
                            Field<ChatConversation>(imPanel, "_selected").Id == avatar,
                            "The profile IM button did not select its resident's conversation in the IM tab");
                        packets();
                        payAction.Click();
                        var payment = Field<ResidentPaymentWindow>(actions, "_payment");
                        payment.ShowAll();
                        var amount = Field<Entry>(payment, "_amount");
                        var submit = Field<global::Gtk.Button>(payment, "_pay");
                        Check(!submit.Sensitive && Field<Label>(payment, "_recipientLabel").Text == "Pay Momoi Pawprint",
                            "The payment prompt used the wrong resident or accepted an empty amount");
                        amount.Text = "1.5";
                        Check(amount.Text == "" && !submit.Sensitive, "A fractional paste became a different payment");
                        amount.Text = "1234";
                        Check(submit.Sensitive, "A valid non-friend payment was disabled");
                        submit.Click();
                        var paid = packets().OfType<MoneyTransferRequestPacket>().Single();
                        Check(paid.MoneyData.DestID == avatar && paid.MoneyData.Amount == 1234 &&
                            Field<ResidentPaymentWindow?>(actions, "_payment") == null,
                            "The payment button submitted the wrong amount/recipient or retained the popup");
                        tpAction.Click();
                        Check(packets().OfType<StartLurePacket>().Single().TargetData.Single().TargetID == avatar,
                            "The profile teleport button sent an invitation to another resident");
                        friendAction.Click();
                        Check(packets().OfType<ImprovedInstantMessagePacket>().Single().MessageBlock.Dialog ==
                            (byte)InstantMessageDialog.FriendshipOffered && !friendAction.Sensitive && friendAction.Label == "Add Friend",
                            "The friendship button did not send an offer or stay pending until acceptance");
                        fixture.Receive(fixture.PrivateIm(avatar, "Momoi Pawprint", "accepted", InstantMessageDialog.FriendshipAccepted));
                        break;
                    case 14:
                        var accepted = Profiles(main, account)[avatar];
                        var remove = Field<global::Gtk.Button>(accepted, "_friend");
                        Check(remove.Label == "Remove Friend" && remove.Sensitive, "Accepted friendship did not update the profile button");
                        remove.Click();
                        Check(packets().OfType<TerminateFriendshipPacket>().Single().ExBlock.OtherID == avatar &&
                            remove.Label == "Add Friend", "The remove-friend button failed to update immediately");
                        Field<global::Gtk.Button>(accepted, "_block").Click();
                        Check(packets().OfType<UpdateMuteListEntryPacket>().Single().MuteData.MuteID == avatar && account.IsResidentBlocked(avatar),
                            "The block button did not blacklist the profile's resident");
                        break;
                    case 15:
                        var blocked = Profiles(main, account)[avatar];
                        Check(Field<global::Gtk.Button>(blocked, "_block").Label == "Unblock", "Blocking did not refresh the profile button");
                        Field<global::Gtk.Button>(blocked, "_block").Click();
                        Check(!account.IsResidentBlocked(avatar) && packets().OfType<RemoveMuteListEntryPacket>().Single().MuteData.MuteID == avatar,
                            "The unblock button retained the resident's mute entry");
                        Field<global::Gtk.Button>(blocked, "_pay").Click();
                        var pendingPayment = Field<ResidentPaymentWindow>(blocked, "_payment");
                        blocked.CloseProfile();
                        Check(pendingPayment.Handle == IntPtr.Zero && Profiles(main, account).Count == 0,
                            "Dismissing a profile retained its pending payment window");
                        Collect();
                        break;
                    case 16:
                        Console.WriteLine("PASS native GTK profile links and action rows: IM tab, non-friend payment, teleport, add/remove friend, block/unblock and cleanup");
                        Application.Quit();
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                result = 1;
                Console.Error.WriteLine($"FAIL native GTK chat links (stage {stage - 1}): {ex}");
                Application.Quit();
                return false;
            }
        });
        try { Application.Run(); }
        finally
        {
            Connected(account, false);
            Invoke(main, "RemoveSession", account);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        return result;
    }

    private static Dictionary<UUID, AvatarProfileWindow> Profiles(MainWindow main, AccountSession account) =>
        Field<Dictionary<AccountSession, Dictionary<UUID, AvatarProfileWindow>>>(main, "_profiles")[account];
    private static Notebook ProfileTabs(AvatarProfileWindow profile) =>
        (Notebook)Field<ChatHistoryView>(profile, "_about").Parent.Parent;
    private static TextTag TagAt(ChatHistoryView view, string name) =>
        view.Buffer.GetIterAtOffset(Offset(view.Buffer.Text, name)).Tags.Single();
    private static int Offset(string text, string name) => text[..text.LastIndexOf(name, StringComparison.Ordinal)].EnumerateRunes().Count();
    internal static void Click(ChatHistoryView view, string name, bool drag = false)
    {
        var iter = view.Buffer.GetIterAtOffset(Offset(view.Buffer.Text, name));
        var rectangle = view.GetIterLocation(iter);
        view.BufferToWindowCoords(TextWindowType.Text, rectangle.X + 2, rectangle.Y + rectangle.Height / 2, out var x, out var y);
        Button(view, EventType.ButtonPress, x, y);
        Button(view, EventType.ButtonRelease, drag ? x + 80 : x, y);
    }
    private static void Button(ChatHistoryView view, EventType type, int x, int y)
    {
        var evnt = (EventButton)EventHelper.New(type);
        try
        {
            var window = view.GetWindow(TextWindowType.Text);
            g_object_ref(window.Handle); // Event.Window's setter does not add the ref released by EventHelper.Free.
            evnt.Window = window;
            evnt.Button = 1; evnt.X = x; evnt.Y = y; evnt.Time = 0;
            gdk_event_set_device(evnt.Handle, view.Display.DefaultSeat.Pointer.Handle);
            g_signal_emit_by_name(view.Handle, type == EventType.ButtonPress ? "button-press-event" : "button-release-event", evnt.Handle, out _);
        }
        finally { EventHelper.Free(evnt); }
    }
    private static void Properties(AccountSession account, UUID id, string about, string firstLife = "First life") =>
        Invoke(account.Client.Avatars, "OnAvatarPropertiesReply", new AvatarPropertiesReplyEventArgs(id,
            new Avatar.AvatarProperties { AboutText = about, FirstLifeText = firstLife, BornOn = "1/1/2020" }));
    private static void Connected(AccountSession account, bool value) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, value);
    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string field)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) is { } found) return (T)found.GetValue(target)!;
        throw new MissingFieldException(field);
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Notices : INotificationOutput
    {
        public string? Error => null;
        public void Show(string key, string accountId, NotificationCategory category, string title, string body) { }
        public void Clear(string? accountId = null, NotificationCategory? category = null) { }
        public void Dispose() { }
    }
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_object_ref(IntPtr instance);
    [DllImport("libgdk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gdk_event_set_device(IntPtr evnt, IntPtr device);
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_signal_emit_by_name(IntPtr instance, string signal, IntPtr evnt, out int handled);
}
