using System.Net;
using System.Reflection;
using System.Threading.Channels;
using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.Packets;
using LibreMetaverse.RLV;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Messages.Linden;
using Radegast.Gtk;

if (args.Contains("--map-scroll-smoke", StringComparer.Ordinal)) return NativeMapChecks.RunScroll();
if (args.Contains("--login-smoke", StringComparer.Ordinal)) return NativeLoginChecks.Run();
if (args.Contains("--chat-links-smoke", StringComparer.Ordinal)) return NativeChatLinkChecks.Run();
if (args.Contains("--profile-text-smoke", StringComparer.Ordinal)) return NativeProfileTextChecks.Run();
if (args.Contains("--profile-links-smoke", StringComparer.Ordinal)) return NativeProfileLinkChecks.Run();
if (args.Contains("--account-settings-smoke", StringComparer.Ordinal)) return NativeAccountSettingsChecks.Run();

// Integration checks for the GTK account adapter; no grid login or display is required.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Reconnect preferences persist per resident/grid, retain defaults and recover from invalid files", ReconnectChecks.Preferences),
    ("Automatic reconnect waits for its delay, preserves login identity/MFA trust and retries without overlap", ReconnectChecks.TimingAndRetries),
    ("Logout, closing, disabling and manual login cancel queued reconnects; delay changes reschedule them", ReconnectChecks.CancellationAndChanges),
    ("Reconnect timers stay account-specific and authentication challenges stop unattended retries", ReconnectChecks.AccountsAndAuthentication),
    ("Reconnect preferences still apply to the session when saving fails", ReconnectChecks.SaveFailure),
    ("Chat profile links retain punctuation and Unicode and ignore unrelated or malformed URLs", ChatLinkChecks.Parsing),
    ("Chat profile links resolve names once per account and refresh on server name replies", ChatLinkChecks.Names),
    ("Chat profile links open the referenced avatar and enforce name/location restrictions", ChatLinkChecks.Restrictions),
    ("Bracketed profile links display Unicode labels and retain exact web/payment/profile targets", ProfileLinkChecks.Parsing),
    ("Malformed profile markup and unsupported URL schemes/actions retain their original text", ProfileLinkChecks.InvalidMarkup),
    ("Profile labels preserve chat behavior and respect live RLV/name/location and connection restrictions", ProfileLinkChecks.FormattingAndRestrictions),
    ("Profile IMs, payments and teleport invites target non-friends on the correct account and enforce validation/RLV", ProfileActionChecks.Contact),
    ("Profile friendship offers await acceptance, allow retries and remove friends immediately", ProfileActionChecks.Friendship),
    ("Profile blocks synchronize with the grid mute list and filter chat, unread counts, notifications and offers per account", ProfileActionChecks.Blocking),
    ("Friend presence discovery is quiet across initial packets and later logins/logouts still notify", FriendPresenceChecks.StartupAndChanges),
    ("Friend presence preserves early replies and resets independently for each account and login", FriendPresenceChecks.OrderingAndReset),
    ("Remembered logins persist and update without plaintext passwords, duplicate aliases or shared grid credentials", SavedLoginChecks.Persistence),
    ("Remembered logins handle unavailable keyrings, corrupt records and cancelled saves", SavedLoginChecks.Failures),
    ("Teleport offers open once for the correct account and notify with the sender and offer label", TeleportOfferChecks.Incoming),
    ("Teleport offer acceptance uses the original lure and refusal reaches the sender exactly once", TeleportOfferChecks.Responses),
    ("Teleport offers recheck RLV restrictions and seated locks before acceptance", TeleportOfferChecks.Restrictions),
    ("Teleport offers preserve RLV automatic responses and clear pending decisions on disconnect", TeleportOfferChecks.AutomaticAndDisconnect),
    ("Map clicks resolve the exact region, reject water and invalid points, and respect cancellation and RLV", MapChecks.ClickedRegions),
    ("Map avatar markers use live account-specific coordinates, exclude self and respect nearby restrictions", MapChecks.AvatarPositions),
    ("Map population queries remain bounded when zooming out and at grid edges", MapChecks.PopulationBounds),
    ("World map stays north-up, pans with the pointer and zooms around the cursor with aligned tiles", MapChecks.Viewport),
    ("World map searches exact region names, keeps accounts separate and cancels pending lookups", MapChecks.RegionLookup),
    ("World map and teleports enforce RLV locks, connectivity and coordinate bounds", MapChecks.Permissions),
    ("Map teleport uses the selected account and destination and waits for server confirmation", MapChecks.Teleport),
    ("Map tiles use each grid's advertised service and zoom filenames", MapChecks.TileServer),
    ("Global notification settings persist, retain defaults for new fields and recover from bad files", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new GlobalSettings(path);
            Check(Enum.GetValues<NotificationCategory>().All(settings.Value.IsEnabled), "Missing settings did not use defaults");
            settings.Update(new NotificationSettings { InstantMessages = false, WornObjects = false, Friends = false });
            var restored = new GlobalSettings(path);
            Check(restored.Value == settings.Value && restored.LoadError == null, "Notification switches did not survive reopening");
            File.WriteAllText(path, "{\"Menus\":false,\"FutureSetting\":42}");
            var partial = new GlobalSettings(path);
            Check(!partial.Value.Menus && partial.Value.InstantMessages && partial.Value.Friends && partial.Value.TeleportOffers,
                "An older settings file disabled categories it did not contain");
            File.WriteAllText(path, "broken json");
            var broken = new GlobalSettings(path);
            Check(broken.LoadError != null && broken.Value == new NotificationSettings(), "A corrupt settings file was not handled");
            var blocked = new GlobalSettings(Path.Combine(directory, "blocked"));
            Directory.CreateDirectory(blocked.FilePath);
            var applied = false;
            blocked.Changed += () => applied = true;
            try { blocked.Update(blocked.Value with { Friends = false }); }
            catch (IOException) { }
            Check(applied && !blocked.Value.Friends && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "A failed save did not apply the setting for this session or left a temporary file");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }),
    ("Notification previews collapse whitespace and truncate without splitting emoji or combining characters", () =>
    {
        int Measure(string text) => new System.Globalization.StringInfo(text).LengthInTextElements;
        Check(NotificationPreview.Fit("  First\r\nsecond\tthird\0  ", 30, Measure) == "First second third",
            "Multiline or whitespace-filled messages produced more than one preview line");
        var preview = NotificationPreview.Fit("👩‍💻e\u0301👩‍💻e\u0301👩‍💻more", 5, Measure);
        Check(preview == "👩‍💻e\u0301👩‍💻e\u0301…" && Measure(preview) == 5,
            "Truncation split an emoji or combining-character sequence");
        Check(NotificationPreview.Fit("Short message", 20, Measure) == "Short message" &&
            NotificationPreview.Fit("Long message", 0, Measure) == string.Empty,
            "Short previews were truncated or ellipsis overflowed the available width");
        return Task.CompletedTask;
    }),
    ("Notification switches apply to every account, suppress visible events and keep names separate from previews", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-notification-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new GlobalSettings(Path.Combine(directory, "settings.json"));
            var output = new RecordingNotificationOutput();
            using var controller = new NotificationController(settings, output);
            using var a = new AccountSession(action => action());
            using var b = new AccountSession(action => action());
            var expected = Enum.GetValues<NotificationCategory>().Length * 2;
            foreach (var category in Enum.GetValues<NotificationCategory>())
            {
                var notice = new AccountNotification(category, "Alice Resident", "<b>x & y</b>", UUID.Random());
                controller.Notify(a, notice, false);
                controller.Notify(b, notice, false);
                controller.Notify(a, notice, true);
            }
            Check(output.Shown.Count == expected && output.Shown.All(notice => notice.Body == "<b>x & y</b>"),
                "Visible events were notified or text was escaped before measuring the preview");
            Check(output.Shown.All(notice => notice.Title == "Alice Resident"),
                "Notification titles included an account prefix instead of the supplied name");
            Check(output.Shown.Select(notice => notice.AccountId).Distinct().Count() == 2 &&
                output.Shown.Select(notice => notice.Key).Distinct().Count() == expected, "Accounts shared notification identities");
            foreach (var category in Enum.GetValues<NotificationCategory>()) settings.Update(settings.Value.WithCategory(category, false));
            foreach (var category in Enum.GetValues<NotificationCategory>())
                controller.Notify(a, new(category, "Disabled", "Message", UUID.Random()), false);
            Check(output.Shown.Count == expected && Enum.GetValues<NotificationCategory>().All(category =>
                output.Cleared.Any(clear => clear.Category == category)), "Disabled categories still notified or old popups were retained");
            controller.CloseAccount(a.Id);
            Check(output.Cleared.Last().AccountId == a.Id, "Logout did not clear only that account's notifications");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }),
    ("Incoming notifications classify private IMs and group chats without outgoing messages or echoes", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var output = new List<AccountNotification>();
        a.NotificationReceived += (_, notice) => output.Add(notice);
        f.CapturePackets();
        SetConnected(a, true);
        try
        {
            var resident = UUID.Random();
            var group = UUID.Random();
            f.Groups((group, "Test Group"));
            await ReceiveIm(f, f.PrivateIm(resident, "Alice Resident", "private"));
            a.SendInstantMessage(resident, "outgoing");
            a.MarkConversationRead(resident);
            await ReceiveIm(f, f.GroupIm(group, resident, "Alice Resident", "group"));
            await ReceiveIm(f, f.GroupIm(group, f.Owner, "Me Resident", "own group"));
            await ReceiveIm(f, f.PrivateIm(resident, "Alice Resident", "typing", InstantMessageDialog.StartTyping));
            Check(output.Count == 2 && output[0].Category == NotificationCategory.InstantMessages && output[0].Title == "Alice Resident" && output[0].TargetId == resident &&
                output[1].Category == NotificationCategory.GroupChats && output[1].Title == "Test Group" && output[1].TargetId == group && output[1].Message == "group",
                "Notifications used transcript changes rather than incoming message events");
            await f.Command("@recvim=n");
            await ReceiveIm(f, f.PrivateIm(resident, "Alice Resident", "blocked"));
            Check(output.Count == 2, "A message blocked by RLV produced a notification");
        }
        finally { SetConnected(a, false); }
    }),
    ("Worn object notifications accept private chat from linked attachments and exclude rezzed objects and commands", () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        a.Rlv.SetEnabled(false);
        var output = new List<AccountNotification>();
        a.NotificationReceived += (_, notice) => output.Add(notice);
        var child = f.Prim(201, f.Attachment.LocalID);
        var rezzed = f.Prim(202);
        Chat(f.Attachment.ID, ChatType.OwnerSay, f.Owner, "attachment");
        Chat(child.ID, ChatType.RegionSayTo, f.Owner, "linked attachment");
        Chat(rezzed.ID, ChatType.OwnerSay, f.Owner, "rezzed object");
        Chat(child.ID, ChatType.Normal, f.Owner, "public object chat");
        Chat(child.ID, ChatType.OwnerSay, UUID.Random(), "another owner's object");
        Chat(child.ID, ChatType.OwnerSay, f.Owner, "@detach=n");
        Check(output.Count == 2 && output.All(notice => notice.Category == NotificationCategory.WornObjects && notice.Title == "Test object") &&
            output[1].Message == "linked attachment", "Worn/private object chat classification was incorrect");
        return Task.CompletedTask;

        void Chat(UUID id, ChatType type, UUID owner, string message) =>
            typeof(AccountSession).GetMethod("OnChatReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(a,
                new object?[] { null, new ChatEventArgs(f.Simulator, message, ChatAudibleLevel.Fully, type,
                    ChatSourceType.Object, "Test object", id, owner, Vector3.Zero) });
    }),
    ("Friend notifications report changes once after the initial roster, without rights updates", async () =>
    {
        // Match GTK's serialized dispatch: FriendsChanged precedes the
        // notification within one callback, so wait for the whole callback.
        var dispatch = new object();
        using var a = new AccountSession(action => { lock (dispatch) action(); });
        using var f = new Fixture(a);
        var friend = f.Friend("Alice Resident");
        var output = new List<AccountNotification>();
        a.NotificationReceived += (_, notice) => output.Add(notice);
        SetConnected(a, true);
        try
        {
            // LibreMetaverse emits roster-ready from the login response, before
            // the simulator's first online-status packets. The buddy list has
            // initialized every friend as offline at this point.
            typeof(FriendsManager).GetMethod("OnFriendsListReady", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(a.Client.Friends, new object[] { new FriendsReadyEventArgs(1) });
            await FriendsEvent(a, () => f.Receive(new OnlineNotificationPacket
                { AgentBlock = new[] { new OnlineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } } }));
            lock (dispatch) Check(output.Count == 0, "Initial online statuses produced notifications");
            await FriendsEvent(a, () => f.Receive(new OfflineNotificationPacket
                { AgentBlock = new[] { new OfflineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } } }));
            await FriendsEvent(a, () => f.Receive(new OfflineNotificationPacket
                { AgentBlock = new[] { new OfflineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } } }));
            await FriendsEvent(a, () => f.Receive(new OnlineNotificationPacket
                { AgentBlock = new[] { new OnlineNotificationPacket.AgentBlockBlock { AgentID = friend.UUID } } }));
            lock (dispatch) Check(output.Count == 2 && output[0].Message == "is offline" && output[1].Message == "is online" &&
                output.All(notice => notice.Category == NotificationCategory.Friends && notice.Title == "Alice Resident"),
                "Presence transitions were duplicated, misclassified, or not split into the friend name and status");
        }
        finally { SetConnected(a, false); }
    }),
    ("Menu and IM notification content follows RLV name and location redaction", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        f.Simulator.Name = "Secret Region";
        var output = new List<AccountNotification>();
        a.NotificationReceived += (_, notice) => output.Add(notice);
        var resident = UUID.Random();
        await ReceiveIm(f, f.PrivateIm(resident, "Alice Resident", "initial"));
        output.Clear();
        await f.Command("@shownames=n,showloc=n");
        await ReceiveIm(f, f.PrivateIm(resident, "Alice Resident", "Alice Resident at secondlife://Secret%20Region/1/2/3"));
        typeof(AccountSession).GetMethod("OnScriptDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(a, new object?[]
        {
            null, new ScriptDialogEventArgs("Secret Region: Alice Resident", "Furniture", UUID.Zero, UUID.Random(), "Alice", "Resident",
                9, new List<string> { "OK" }, resident)
        });
        Check(output.Count == 2 && output[0].Title == "Resident" && output[1].Category == NotificationCategory.Menus && output[1].Title == "Furniture" &&
            output.All(notice => !notice.Message.Contains("Alice Resident") && !notice.Message.Contains("Secret Region") &&
                !notice.Message.Contains("secondlife://")), "Notification content exposed RLV-hidden names or locations");
    }),
    ("Object touch targets a touchable root or linked part and rejects stale or non-touchable objects", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var root = f.Prim(200);
        root.Position = new Vector3(2, 0, 0);
        SetConnected(a, true);
        try
        {
            var item = a.GetNearbyObjects().Single();
            Check(a.ObjectTouchError(item) != null, "A non-touchable object allowed touching");
            await ExpectTouchRejected(a, item);
            Check(packets().Count == 0, "A disabled touch sent packets");
            var child = f.Prim(201, root.LocalID);
            child.Flags |= PrimFlags.Touch;
            var unrelated = f.Prim(202, 999);
            unrelated.Flags |= PrimFlags.Touch;
            item = a.GetNearbyObjects().Single();
            Check(item.TouchId == child.ID && a.ObjectTouchError(item) == null, "A touchable linked part was not detected");
            await a.TouchObjectAsync(item);
            var sent = packets();
            Check(sent.OfType<ObjectGrabPacket>().Single().ObjectData.LocalID == child.LocalID &&
                sent.OfType<ObjectDeGrabPacket>().Single().ObjectData.LocalID == child.LocalID &&
                sent.OfType<ObjectGrabPacket>().Single().AgentData.AgentID == f.Owner,
                "Touch did not send grab and release to the linked part for this account");
            child.Flags &= ~PrimFlags.Touch;
            await ExpectTouchRejected(a, item);
            root.Flags |= PrimFlags.Touch;
            await a.TouchObjectAsync(item);
            Check(packets().OfType<ObjectGrabPacket>().Single().ObjectData.LocalID == root.LocalID,
                "A newly touchable root was not used");
            root.Flags &= ~PrimFlags.Touch;
            f.Prim(child.LocalID, unrelated.LocalID).Flags |= PrimFlags.Touch;
            await ExpectTouchRejected(a, item);
            Check(!packets().OfType<ObjectGrabPacket>().Any(), "A replaced linked part remained touchable through the old row");
        }
        finally { SetConnected(a, false); }
    }),
    ("Object touch enforces world, prim, interaction and default far-touch restrictions", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var root = f.Prim(200);
        root.Position = new Vector3(2, 0, 0);
        var child = f.Prim(201, root.LocalID);
        child.Flags |= PrimFlags.Touch;
        SetConnected(a, true);
        try
        {
            var item = a.GetNearbyObjects().Single();
            await f.Command("@touchworld=n");
            await ExpectTouchRejected(a, item);
            await f.Command($"@touchworld=y,touchthis:{child.ID}=n");
            await ExpectTouchRejected(a, item);
            await f.Command($"@touchthis:{child.ID}=y,touchthis:{root.ID}=n");
            await ExpectTouchRejected(a, item);
            await f.Command($"@touchthis:{root.ID}=y,fartouch=n");
            await ExpectTouchRejected(a, item);
            root.Position = new Vector3(1, 0, 0);
            Check(a.ObjectTouchError(item) == null, "Touch within the default 1.5 m limit was blocked");
            await f.Command("@fartouch=y,interact=n");
            await ExpectTouchRejected(a, item);
            Check(packets().Count == 0, "An RLV-blocked touch sent packets");
            a.Rlv.SetEnabled(false);
            await a.TouchObjectAsync(item);
            Check(packets().OfType<ObjectGrabPacket>().Single().ObjectData.LocalID == child.LocalID,
                "Disabling RLV did not allow touching the object");
        }
        finally { SetConnected(a, false); }
    }),
    ("Nearby objects use a 50 m sphere, exclude attachments and linked children, and stay account-specific", () =>
    {
        using var a = new AccountSession(action => action());
        using var b = new AccountSession(action => action());
        using var f = new Fixture(a);
        using var g = new Fixture(b);
        SetConnected(a, true);
        SetConnected(b, true);
        SetPosition(a, new Vector3(100, 100, 20));
        SetPosition(b, new Vector3(100, 100, 20));
        try
        {
            var near = f.Prim(200);
            near.Position = new Vector3(101, 100, 20);
            near.Properties = new Primitive.ObjectProperties { ObjectID = near.ID, Name = "Chair" };
            var edge = f.Prim(201);
            edge.Position = new Vector3(100, 130, 60); // Exactly 50 m in three dimensions.
            f.Prim(202).Position = new Vector3(100, 100, 70.01f);
            f.Prim(203, near.LocalID).Position = Vector3.Zero;
            var attached = f.Prim(204);
            attached.Position = near.Position;
            attached.IsAttachment = true;
            var avatar = f.Prim(205);
            avatar.Position = near.Position;
            avatar.PrimData.PCode = PCode.Avatar;
            g.Prim(210).Position = near.Position;
            var objects = a.GetNearbyObjects();
            Check(objects.Select(item => item.Id).SequenceEqual(new[] { near.ID, edge.ID }),
                "The radius, root filtering, distance order or account isolation was incorrect");
            Check(objects[0].Name == "Chair" && objects[0].HasName && !objects[1].HasName,
                "Known names or loading placeholders were lost");
            Check(b.GetNearbyObjects().Single().LocalId == 210, "Another account's objects leaked into this list");
            SetPosition(a, new Vector3(200, 100, 20));
            Check(a.GetNearbyObjects().Count == 0, "Moving the avatar did not update the radius");
        }
        finally { SetConnected(a, false); SetConnected(b, false); }
        return Task.CompletedTask;
    }),
    ("Nearby objects include connected neighbours using global region coordinates", () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        f.Simulator.Handle = Utils.UIntsToLong(1024, 1024);
        using var neighbour = new Simulator(a.Client, new IPEndPoint(IPAddress.Loopback, 13001), Utils.UIntsToLong(1280, 1024));
        typeof(Simulator).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(neighbour, true);
        a.Client.Network.Simulators.Add(neighbour);
        var prim = new Primitive { ID = UUID.Random(), LocalID = 200, Position = new Vector3(10, 100, 20) };
        neighbour.ObjectsPrimitives[prim.LocalID] = prim;
        SetPosition(a, new Vector3(250, 100, 20));
        SetConnected(a, true);
        try
        {
            var item = a.GetNearbyObjects().Single();
            Check(item.Id == prim.ID && item.Distance == 16 && item.Simulator == neighbour,
                "Objects across a region boundary used local coordinates or the wrong simulator");
            Check(a.ObjectSitError(item) == null, "A connected neighbouring object could not be selected");
            typeof(Simulator).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(neighbour, false);
            Check(a.GetNearbyObjects().Count == 0 && a.ObjectSitError(item) != null,
                "A disconnected neighbouring region still exposed usable objects");
        }
        finally { SetConnected(a, false); a.Client.Network.Simulators.Remove(neighbour); }
        return Task.CompletedTask;
    }),
    ("Manual sitting and standing enforce live objects, RLV locks and sitting distance", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var prim = f.Prim(200);
        prim.Position = new Vector3(10, 0, 0);
        SetConnected(a, true);
        try
        {
            var item = a.GetNearbyObjects().Single();
            Check(a.ObjectSitError(item) == null && a.StandError != null, "Initial sit/stand state was incorrect");
            await f.Command("@sit=n");
            Check(a.ObjectSitError(item) != null, "A sit restriction was ignored");
            await f.Command("@sit=y,sittp=n");
            Check(a.ObjectSitError(item) != null, "The sit teleport distance restriction was ignored");
            prim.Position = new Vector3(1, 0, 0);
            Check(a.ObjectSitError(item) == null, "Sitting within the permitted distance was blocked");
            await f.Command("@sittp=y");
            var oldSeat = f.Prim(201);
            f.ChangeSeat(oldSeat.LocalID);
            await f.Command("@unsit=n");
            Check(a.ObjectSitError(item) != null && a.StandError != null, "A seat lock allowed sitting elsewhere or standing");
            ExpectRejected(a.StandUp);
            Check(packets().Count == 0, "A blocked sit or stand sent movement packets");
            await f.Command("@unsit=y");
            f.ChangeSeat(0);
            prim.Position = new Vector3(51, 0, 0);
            Check(a.ObjectSitError(item) != null, "A stale nearby row allowed sitting outside the radius");
            prim.Position = new Vector3(1, 0, 0);
            prim.ID = UUID.Random();
            Check(a.ObjectSitError(item) != null, "Reusing a local ID allowed sitting on a different object");
            f.Simulator.ObjectsPrimitives.TryRemove(prim.LocalID, out _);
            Check(a.ObjectSitError(item) != null, "A deleted object remained usable");
        }
        finally { SetConnected(a, false); }
    }),
    ("Manual sit waits for its object's reply, accepts linked seats and confirms the avatar's seat", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var chair = f.Prim(200);
        chair.Position = new Vector3(2, 0, 0);
        var seat = f.Prim(201, chair.LocalID);
        SetConnected(a, true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var item = a.GetNearbyObjects().Single();
            var sit = a.SitOnObjectAsync(item, cancel.Token);
            Check(packets().OfType<AgentRequestSitPacket>().Single().TargetObject.TargetID == chair.ID && !sit.IsCompleted,
                "The sit request used the wrong object or finished before the server replied");
            f.Receive(new AvatarSitResponsePacket { SitObject = { ID = UUID.Random() } });
            await Task.Delay(40);
            Check(!sit.IsCompleted && !packets().OfType<AgentSitPacket>().Any(), "An unrelated object's sit response was accepted");
            f.Receive(new AvatarSitResponsePacket { SitObject = { ID = seat.ID } });
            var sent = new List<Packet>();
            await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<AgentSitPacket>().Any(); });
            Check(!sit.IsCompleted, "Sit success was reported before the avatar's seat update");
            f.ChangeSeat(seat.LocalID);
            await sit;
            Check(a.IsSitting && a.StandError == null && a.ObjectSitError(item) != null,
                "The seated state or stand availability did not update");
        }
        finally { SetConnected(a, false); }
    }),
    ("Canceled manual sit requests cannot seat the avatar on a later reply", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var chair = f.Prim(200);
        chair.Position = new Vector3(2, 0, 0);
        SetConnected(a, true);
        try
        {
            var item = a.GetNearbyObjects().Single();
            using var cancel = new CancellationTokenSource();
            var sit = a.SitOnObjectAsync(item, cancel.Token);
            packets();
            cancel.Cancel();
            try { await sit; throw new InvalidOperationException("Canceled sit request succeeded"); }
            catch (OperationCanceledException) { }
            f.Receive(new AvatarSitResponsePacket { SitObject = { ID = chair.ID } });
            await Task.Delay(40);
            Check(!packets().OfType<AgentSitPacket>().Any(), "A canceled request sent a late sit packet");
            using var retryCancel = new CancellationTokenSource();
            var retry = a.SitOnObjectAsync(item, retryCancel.Token);
            Check(packets().OfType<AgentRequestSitPacket>().Count() == 1, "Cancellation left sitting permanently busy");
            retryCancel.Cancel();
            try { await retry; } catch (OperationCanceledException) { }
        }
        finally { SetConnected(a, false); }
    }),
    ("Manual stand sends the stand control and cleans old furniture animations on confirmation", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var packets = f.CapturePackets();
        var chair = f.Prim(200);
        var seat = f.Prim(201, chair.LocalID);
        var animation = UUID.Random();
        var attachmentAnimation = UUID.Random();
        SetConnected(a, true);
        a.Client.Settings.Agent.SendUpdates = true;
        f.Simulator.AgentMovementComplete = true;
        try
        {
            f.ChangeSeat(seat.LocalID);
            f.Animations((animation, 1, chair.ID), (attachmentAnimation, 1, f.Attachment.ID));
            await Task.Delay(40);
            a.StandUp();
            Check(packets().OfType<AgentUpdatePacket>().Any(packet =>
                (packet.AgentData.ControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP) != 0),
                "Stand did not send the stand control");
            f.ChangeSeat(0);
            var stopped = packets().OfType<AgentAnimationPacket>().SelectMany(packet => packet.AnimationList).ToArray();
            Check(!a.IsSitting && a.StandError != null && stopped.Any(block => block.AnimID == animation && !block.StartAnim) &&
                stopped.All(block => block.AnimID != attachmentAnimation), "Stand did not clean only the furniture's animations");
        }
        finally { SetConnected(a, false); }
    }),
    ("Group rosters load and sort independently for each account", () =>
    {
        using var a = new AccountSession(action => action());
        using var b = new AccountSession(action => action());
        using var f = new Fixture(a);
        using var g = new Fixture(b);
        var id = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(a, true);
        SetConnected(b, true);
        try
        {
            a.RequestGroups();
            a.RequestGroups();
            Check(packets().OfType<AgentDataUpdateRequestPacket>().Single().AgentData.AgentID == f.Owner,
                "Group membership requests used the wrong account or were repeated");
            f.Groups((id, "Zebra Group"), (UUID.Random(), "alpha Group"));
            g.Groups((id, "Other Account Group"));
            Check(a.GroupsLoaded && a.GroupConversations.Select(chat => chat.Name).SequenceEqual(new[] { "alpha Group", "Zebra Group" }) &&
                b.GroupConversations.Single().Name == "Other Account Group", "Group names or ordering leaked across accounts");
            f.Groups((id, "Renamed Group"));
            Check(a.GroupConversations.Single().Name == "Renamed Group" && b.GroupConversations.Single().Name == "Other Account Group",
                "A roster update did not replace the current membership correctly");
        }
        finally { SetConnected(a, false); SetConnected(b, false); }
        return Task.CompletedTask;
    }),
    ("Early group messages wait for membership and conference sessions stay separate", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var group = UUID.Random();
        var sender = UUID.Random();
        f.CapturePackets();
        SetConnected(a, true);
        try
        {
            await ReceiveIm(f, f.GroupIm(group, sender, "Alice Resident", "early group message"));
            await ReceiveIm(f, f.GroupIm(UUID.Random(), sender, "Alice Resident", "conference"));
            Check(a.GroupConversations.Count == 0 && a.Conversations.Count == 0, "Unclassified sessions became conversations");
            f.Groups((group, "Test Group"));
            var chat = a.GroupConversations.Single();
            Check(chat.Messages.Single().Text == "early group message" && chat.Messages[0].SenderName == "Alice Resident" &&
                chat.State == GroupChatState.Joined && chat.UnreadCount == 1, "The first group message was lost while membership loaded");
            await ReceiveIm(f, f.PrivateIm(sender, "Alice Resident", "private"));
            await ReceiveIm(f, f.GroupIm(group, sender, "Alice Resident", "group again"));
            var legacy = f.GroupIm(group, sender, "Alice Resident", "legacy group");
            legacy.MessageBlock.Dialog = (byte)InstantMessageDialog.MessageFromAgent;
            await ReceiveIm(f, legacy);
            Check(chat.Messages.Count == 3 && a.Conversations.Single().Messages.Single().Text == "private",
                "Group and private IM routing was mixed");
        }
        finally { SetConnected(a, false); }
    }),
    ("Group chat sends wait for confirmed joining and use the group session protocol", async () =>
    {
        using var a = new AccountSession(action => action());
        using var b = new AccountSession(action => action());
        using var f = new Fixture(a);
        using var g = new Fixture(b);
        var group = UUID.Random();
        var packets = f.CapturePackets();
        var otherPackets = g.CapturePackets();
        SetConnected(a, true);
        try
        {
            f.Groups((group, "Test Group"));
            var chat = a.OpenGroupChat(group);
            a.OpenGroupChat(group);
            chat.Draft = "/9 group message";
            Check(chat.State == GroupChatState.Joining && !a.CanSendGroupMessage(group, chat.Draft), "Sending was enabled before joining");
            ExpectRejected(() => a.SendGroupMessage(group, chat.Draft));
            var join = packets().OfType<ImprovedInstantMessagePacket>().Single();
            Check(join.MessageBlock.Dialog == (byte)InstantMessageDialog.SessionGroupStart && join.MessageBlock.ToAgentID == group &&
                join.AgentData.AgentID == f.Owner, "Joining used the wrong protocol or account");
            f.GroupJoin(group, true);
            a.SendGroupMessage(group, chat.Draft);
            var sent = packets().OfType<ImprovedInstantMessagePacket>().Single();
            Check(sent.Header.Reliable && sent.AgentData.AgentID == f.Owner && sent.MessageBlock.ToAgentID == group && sent.MessageBlock.ID == group &&
                sent.MessageBlock.Dialog == (byte)InstantMessageDialog.SessionSend && Utils.BytesToString(sent.MessageBlock.Message) == "/9 group message",
                "A group message became a private IM, nearby channel command or wrong session");
            Check(chat.Messages.Single().Outgoing && chat.Draft == "" && !otherPackets().Any(), "The group draft/history or originating account is wrong");
            await ReceiveIm(f, f.GroupIm(group, f.Owner, "Self", "/9 group message"));
            Check(chat.Messages.Count == 1 && chat.UnreadCount == 0, "The server's own-message echo duplicated the sent line");
            await ReceiveIm(f, f.GroupIm(group, UUID.Random(), "Other Resident", "/9 group message"));
            Check(chat.Messages.Count == 2 && chat.UnreadCount == 1, "Another resident's identical message was discarded");
        }
        finally { SetConnected(a, false); }
    }),
    ("Failed joins, lost membership and disconnects preserve group drafts and prevent sends", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var group = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(a, true);
        try
        {
            f.Groups((group, "Test Group"));
            var chat = a.OpenGroupChat(group);
            chat.Draft = "keep this draft";
            f.GroupJoin(group, false);
            Check(chat.State == GroupChatState.Failed && a.GroupChatStatus(group, chat.Draft).Contains("retry"), "A failed join was not reported");
            ExpectRejected(() => a.SendGroupMessage(group, chat.Draft));
            Check(chat.Draft == "keep this draft" && chat.Messages.Count == 0, "A failed send erased a draft or added a sent line");
            a.OpenGroupChat(group);
            Check(packets().OfType<ImprovedInstantMessagePacket>().Count(packet => packet.MessageBlock.Dialog == (byte)InstantMessageDialog.SessionGroupStart) == 2,
                "A failed group join could not be retried");
            f.GroupJoin(group, true);
            await ReceiveIm(f, f.GroupIm(group, UUID.Random(), "Resident", "history"));
            f.Groups();
            Check(a.GroupConversations.Single().Messages.Count == 1 && !a.CanSendGroupMessage(group, chat.Draft), "Lost membership discarded history or allowed sending");
            ExpectRejected(() => a.OpenGroupChat(group));
            ExpectRejected(() => a.SendGroupMessage(group, chat.Draft));
            SetConnected(a, false);
            ExpectRejected(() => a.SendGroupMessage(group, chat.Draft));
            Check(chat.Draft == "keep this draft", "Disconnecting lost the unsent group draft");
            Check(!packets().OfType<ImprovedInstantMessagePacket>().Any(packet => packet.MessageBlock.Dialog == (byte)InstantMessageDialog.SessionSend),
                "A rejected send reached the group");
        }
        finally { SetConnected(a, false); }
    }),
    ("Long Unicode group messages split safely and suppress only their own echoed parts", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var group = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(a, true);
        try
        {
            f.Groups((group, "Test Group"));
            f.GroupJoin(group, true);
            var text = string.Concat(Enumerable.Repeat("日本語😀", 350));
            a.SendGroupMessage(group, text);
            var sent = packets().OfType<ImprovedInstantMessagePacket>().ToArray();
            var parts = sent.Select(packet => Utils.BytesToString(packet.MessageBlock.Message)).ToArray();
            Check(parts.Length > 1 && string.Concat(parts) == text && parts.All(part => System.Text.Encoding.UTF8.GetByteCount(part) <= AgentManager.MaxChatMessageSize),
                "Group packet splitting corrupted or truncated Unicode text");
            foreach (var part in parts) await ReceiveIm(f, f.GroupIm(group, f.Owner, "Self", part));
            Check(a.GroupConversations.Single().Messages.Single().Text == text && a.UnreadGroupMessages == 0,
                "Echoed packet chunks duplicated the locally displayed message");
        }
        finally { SetConnected(a, false); }
    }),
    ("Group unread messages, sender names and drafts remain isolated from private IMs and accounts", async () =>
    {
        using var a = new AccountSession(action => action());
        using var b = new AccountSession(action => action());
        using var f = new Fixture(a);
        using var g = new Fixture(b);
        var first = UUID.Random();
        var second = UUID.Random();
        var sender = UUID.Random();
        SetConnected(a, true);
        SetConnected(b, true);
        try
        {
            f.Groups((first, "First"), (second, "Second"));
            g.Groups((first, "Other First"));
            await ReceiveIm(f, f.GroupIm(first, sender, "Alice Resident", "one"));
            await ReceiveIm(f, f.GroupIm(second, sender, "Alice Resident", "two"));
            await ReceiveIm(g, g.GroupIm(first, sender, "Alice Resident", "other account"));
            await ReceiveIm(f, f.PrivateIm(sender, "Alice Resident", "private"));
            var chat = a.GroupConversations.Single(chat => chat.Id == first);
            chat.Draft = "draft";
            a.MarkGroupChatRead(first);
            Check(a.UnreadGroupMessages == 1 && b.UnreadGroupMessages == 1 && a.UnreadInstantMessages == 1 && chat.Draft == "draft" &&
                chat.Messages.Single().Text == "one", "Reading a group affected other groups, private IMs, drafts or accounts");
            await f.Command("@shownames=n");
            Check(a.DisplayGroupSender(chat.Messages[0]) == "Resident", "The group transcript exposed a restricted sender name");
        }
        finally { SetConnected(a, false); SetConnected(b, false); }
    }),
    ("RLV group sending and receiving use group exceptions and keep private IM restrictions separate", async () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var group = UUID.Random();
        var peer = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(a, true);
        try
        {
            f.Groups((group, "Test Group"));
            f.GroupJoin(group, true);
            var chat = a.GroupConversations.Single();
            chat.Draft = "keep";
            await f.Command("@sendimto:allgroups=n,recvimfrom:allgroups=n");
            ExpectRejected(() => a.SendGroupMessage(group, chat.Draft));
            await ReceiveIm(f, f.GroupIm(group, peer, "Resident", "blocked group"));
            await ReceiveIm(f, f.PrivateIm(peer, "Resident", "allowed private"));
            Check(chat.Messages.Count == 0 && chat.Draft == "keep" && a.Conversations.Single().Messages.Count == 1,
                "Group-only restrictions affected private IMs or admitted blocked group messages");
            Check(!packets().OfType<ImprovedInstantMessagePacket>().Any(), "A restricted group send reached the network");
            await f.Command("@clear,sendim=n,sendim:allgroups=add,recvim=n,recvim:allgroups=add");
            a.SendGroupMessage(group, "allowed group");
            ExpectRejected(() => a.SendInstantMessage(peer, "blocked private"));
            await ReceiveIm(f, f.GroupIm(group, peer, "Resident", "allowed incoming group"));
            await ReceiveIm(f, f.PrivateIm(peer, "Resident", "blocked private"));
            Check(chat.Messages.Count == 2 && a.Conversations.Single().Messages.Count == 1, "Group exceptions did not enforce the correct receive context");
        }
        finally { SetConnected(a, false); }
    }),
    ("Expired group sessions report failed sends and rejoin before sending again", () =>
    {
        using var a = new AccountSession(action => action());
        using var f = new Fixture(a);
        var group = UUID.Random();
        var packets = f.CapturePackets();
        SetConnected(a, true);
        try
        {
            f.Groups((group, "Test Group"));
            f.GroupJoin(group, true);
            a.SendGroupMessage(group, "sent once");
            packets();
            f.Caps("ChatterBoxSessionEventReply", new ChatterboxSessionEventReplyMessage { SessionID = group, Success = false });
            Check(a.GroupConversations.Single().State == GroupChatState.Joining && !a.CanSendGroupMessage(group, "retry") &&
                a.GroupChatStatus(group, "retry").Contains("rejected") && a.GroupConversations.Single().Draft == "sent once",
                "An expired group session stayed sendable, hid the rejection or lost the rejected draft");
            Check(packets().OfType<ImprovedInstantMessagePacket>().Single().MessageBlock.Dialog == (byte)InstantMessageDialog.SessionGroupStart,
                "The library's session recovery did not request a rejoin or resent a message automatically");
            f.GroupJoin(group, true);
            Check(a.CanSendGroupMessage(group, "retry"), "The rejoined session could not send again");
        }
        finally { SetConnected(a, false); }
        return Task.CompletedTask;
    }),
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

static void SetPosition(AccountSession session, Vector3 position) =>
    typeof(AgentManager).GetField("relativePosition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session.Client.Self, position);

static async Task ExpectTouchRejected(AccountSession session, NearbyObject item)
{
    try { await session.TouchObjectAsync(item); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("An unavailable or restricted touch was accepted");
}

static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(10, timeout.Token);
}

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

sealed class RecordingNotificationOutput : INotificationOutput
{
    public sealed record Notice(string Key, string AccountId, NotificationCategory Category, string Title, string Body);
    public List<Notice> Shown { get; } = new();
    public List<(string? AccountId, NotificationCategory? Category)> Cleared { get; } = new();
    public string? Error => null;
    public void Show(string key, string accountId, NotificationCategory category, string title, string body) =>
        Shown.Add(new(key, accountId, category, title, body));
    public void Clear(string? accountId = null, NotificationCategory? category = null) => Cleared.Add((accountId, category));
    public void Dispose() { }
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

    public ImprovedInstantMessagePacket GroupIm(UUID group, UUID sender, string name, string text)
    {
        var packet = PrivateIm(sender, name, text, InstantMessageDialog.SessionSend);
        packet.MessageBlock.ID = group;
        packet.MessageBlock.BinaryBucket = Utils.StringToBytes("Group session");
        return packet;
    }

    public void Caps(string name, IMessage message)
    {
        var events = typeof(NetworkManager).GetField("CapsEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client.Network)!;
        events.GetType().GetMethod("RaiseEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(events, new object[] { name, message, Simulator });
    }

    public void Groups(params (UUID Id, string Name)[] groups) => Caps("AgentGroupDataUpdate", new AgentGroupDataUpdateMessage
    {
        AgentID = Owner, AvatarID = Owner,
        GroupDataBlock = groups.Select(group => new AgentGroupDataUpdateMessage.GroupData
            { GroupID = group.Id, GroupName = group.Name }).ToArray(),
        NewGroupDataBlock = groups.Select(_ => new AgentGroupDataUpdateMessage.NewGroupData()).ToArray()
    });

    public void GroupJoin(UUID group, bool success, UUID? sessionId = null) => Caps("ChatterBoxSessionStartReply",
        new ChatterBoxSessionStartReplyMessage { SessionID = sessionId ?? group, TempSessionID = group, SessionName = "Group session", Success = success });

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
