using System.Diagnostics;
using System.Reflection;
using Gtk;
using LibreMetaverse;
using Radegast.Gtk;

internal static class NativeMemoryChecks
{
    public static int Run()
    {
        Application.Init();
        using var h = new ReconnectHarness(post: GtkDispatch.Post);
        h.Connect();
        using var main = new MainWindow(new GlobalSettings(Path.Combine(h.DirectoryPath, "global.json")), new Notices());
        Invoke(main, "AddSession", h.Account);
        main.ShowAll();
        var widgets = Field<Dictionary<AccountSession, SessionWidgets>>(main, "_sessions")[h.Account];
        var list = Field<ListBox>(widgets, "_nearbyList");
        var residents = Enumerable.Range(0, 40).Select(i => new NearbyResident(UUID.Random(), $"Resident {i}", i)).ToArray();
        SetNearby(h.Account, residents);
        widgets.RefreshNearby();
        var originals = list.Children.Select(row => row.Handle).ToArray();
        var stage = 0;
        var result = 0;
        long baseline = 0;
        GLib.Timeout.Add(100, () =>
        {
            try
            {
                switch (stage++)
                {
                    case 0:
                        RefreshBatch(20);
                        Collect();
                        break;
                    case 1:
                        baseline = Sample("warm");
                        RefreshBatch(200);
                        Sample("200 updates before GC");
                        Collect();
                        break;
                    case 2:
                        Sample("200 updates after GC");
                        RefreshBatch(200);
                        Sample("400 updates before GC");
                        Collect();
                        break;
                    case 3:
                        var final = Sample("400 updates after GC");
                        Check(list.Children.Select(row => row.Handle).SequenceEqual(originals),
                            "Nearby updates replaced all native rows instead of retaining existing residents");
                        Check(final - baseline < 32 * 1024 * 1024,
                            $"Native list updates retained {(final - baseline) / 1024 / 1024} MiB after collection");
                        CheckRemovedRows();
                        CheckOtherLists();
                        CheckChatHistory();
                        h.LoggedOut();
                        Invoke(main, "RemoveSession", h.Account);
                        Check(widgets.Root.Handle == IntPtr.Zero && widgets.AccountRow.Handle == IntPtr.Zero && widgets.NearbyPane.Handle == IntPtr.Zero,
                            "Logging out retained the account's native widget trees");
                        Collect();
                        Sample("lists/chat trimmed and account removed");
                        Console.WriteLine("PASS native GTK bounded nearby/chat memory, removed row/account cleanup, friend actions and rolling group message rendering");
                        Application.Quit();
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL native GTK memory check: {ex}");
                result = 1;
                h.LoggedOut();
                Invoke(main, "RemoveSession", h.Account);
                Application.Quit();
                return false;
            }
        });
        Application.Run();
        return result;

        void RefreshBatch(int count)
        {
            for (var i = 0; i < count; i++)
            {
                SetNearby(h.Account, residents.Select(person => person with { Distance = (person.Distance + i) % 50 }));
                widgets.RefreshNearby();
            }
        }

        void CheckRemovedRows()
        {
            var rows = list.Children.ToArray();
            var labels = rows.Cast<ListBoxRow>().Select(row => row.Child).ToArray();
            SetNearby(h.Account, Array.Empty<NearbyResident>());
            widgets.RefreshNearby();
            Check(rows.All(row => row.Handle == IntPtr.Zero) && labels.All(label => label.Handle == IntPtr.Zero),
                "A departed resident retained its row or native label");
            var placeholder = list.Children.Single().Handle;
            for (var i = 0; i < 20; i++) widgets.RefreshNearby();
            Check(list.Children.Single().Handle == placeholder, "Empty nearby updates kept replacing their placeholder");
            SetNearby(h.Account, residents.Reverse());
            widgets.RefreshNearby();
            Check(((Label)((ListBoxRow)list.Children[0]).Child).Text.StartsWith("Resident 39"),
                "Row reuse ignored the nearby list's new ordering");
        }

        void CheckOtherLists()
        {
            for (var i = 0; i < 10; i++) h.Fixture.Friend($"Friend {i}");
            var friends = Field<FriendsPanel>(widgets, "_friendsPanel");
            friends.StartLoading();
            var friendsList = Field<ListBox>(friends, "_list");
            for (var i = 0; i < 80; i++)
            {
                var removed = Descendants(friendsList).ToArray();
                Invoke(friends, "Refresh");
                Check(removed.All(widget => widget.Handle == IntPtr.Zero), "Refreshing friends retained rows or child button signal handlers");
            }
            var clicked = UUID.Zero;
            friends.ImRequested += id => clicked = id;
            Descendants(friendsList).OfType<Button>().First(button => button.Label == "IM").Click();
            Check(clicked != UUID.Zero, "Disposing old friend rows broke the remaining IM button");
            var attachments = Field<AttachmentsPanel>(widgets, "_attachmentsPanel");
            attachments.StartLoading();
            var attachmentsList = Field<ListBox>(attachments, "_list");
            var old = attachmentsList.Children;
            Invoke(attachments, "Refresh");
            Check(old.All(widget => widget.Handle == IntPtr.Zero), "Refreshing attachments retained removed rows");
        }

        void CheckChatHistory()
        {
            var history = Field<ChatHistoryView>(widgets, "_chatHistory");
            var avatar = h.Fixture.Friend("Memory Link").UUID;
            history.AppendLine($"Old link: {ChatLinkChecks.Url(avatar)}");
            var oldTag = Field<Dictionary<ProfileTextLink, TextTag>>(history, "_links").Values.Single();
            for (var i = 0; i < ChatMemoryLimits.Messages + 20; i++) history.AppendLine($"Line {i} 😀\nsecond line");
            Check(!history.Buffer.Text.Contains("Old link") && !history.Buffer.Text.Contains("Line 19 ") &&
                history.Buffer.Text.Contains($"Line {ChatMemoryLimits.Messages + 19} 😀") && oldTag.Handle == IntPtr.Zero &&
                history.Buffer.TagTable.Size == 0, "Chat trimming lost the newest line, split multiline/Unicode messages or retained old link tags");
            var longLine = new string('x', 2000);
            for (var i = 0; i < 600; i++) history.AppendLine(longLine);
            Check(history.Buffer.CharCount < ChatMemoryLimits.Characters + ChatMemoryLimits.Messages,
                "Native nearby chat exceeded its character budget");
            var group = new GroupConversation(UUID.Random(), "Memory Group") { State = GroupChatState.Joined };
            Field<Dictionary<UUID, GroupConversation>>(h.Account, "_groupChats").Add(group.Id, group);
            var panel = Field<GroupChatsPanel>(widgets, "_groupPanel");
            for (var i = 0; i < ChatMemoryLimits.Messages + 20; i++)
            {
                group.Append(new(DateTime.Now, $"Group message {i}", false, avatar, "Sender"));
                Invoke(panel, "OnGroupConversationChanged", h.Account, group);
            }
            var view = Field<ChatHistoryView>(panel, "_history");
            Check(view.Buffer.Text.Contains($"Group message {ChatMemoryLimits.Messages + 19}") &&
                !view.Buffer.Text.Contains("Group message 19\n"), "Rolling group chat retention froze the renderer or retained expired messages");
            group.Append(new(DateTime.Now, "Newest message after trimming", false, avatar, "Sender"));
            Invoke(panel, "OnGroupConversationChanged", h.Account, group);
            Check(view.Buffer.Text.Contains("Newest message after trimming"), "A full group history stopped displaying new messages");
        }
    }

    private static long Sample(string stage)
    {
        using var process = Process.GetCurrentProcess();
        var bytes = process.WorkingSet64;
        Console.WriteLine($"MEMORY {stage}: RSS {bytes / 1024 / 1024} MiB, managed {GC.GetTotalMemory(false) / 1024 / 1024} MiB");
        return bytes;
    }
    private static void SetNearby(AccountSession account, IEnumerable<NearbyResident> nearby) => typeof(AccountSession)
        .GetField("_nearby", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(account, nearby.ToList());
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static IEnumerable<Widget> Descendants(Container parent) => parent.Children.SelectMany(child =>
        child is Container container ? new[] { child }.Concat(Descendants(container)) : new[] { child });
    private static void Invoke(object target, string method, params object[] args)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } found)
            { found.Invoke(target, args); return; }
        throw new MissingMethodException(method);
    }
    private static T Field<T>(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } found)
                return (T)found.GetValue(target)!;
        throw new MissingFieldException(name);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Notices : INotificationOutput
    {
        public string? Error => null;
        public void Show(string key, string accountId, NotificationCategory category, string title, string body) { }
        public void Clear(string? accountId = null, NotificationCategory? category = null) { }
        public void Dispose() { }
    }
}
