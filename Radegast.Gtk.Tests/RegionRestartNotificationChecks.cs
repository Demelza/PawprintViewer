using System.Net;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class RegionRestartNotificationChecks
{
    public static Task TimingAndUpdates()
    {
        using var h = new Harness();
        h.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(5));
        Check(h.Messages.SequenceEqual(new[] { "5 minutes remaining" }) && h.Clock.PendingTimers == 1,
            "The first restart warning did not notify immediately");
        h.Clock.Advance(59);
        h.Notifications.ObserveRestart(256, TimeSpan.FromSeconds(241));
        Check(h.Messages.Count == 1, "An updated warning produced a second popup within one minute");
        h.Clock.Advance(1);
        h.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(4));
        Check(h.Messages.Count == 2 && h.Messages.Last() == "4 minutes remaining" && h.Clock.PendingTimers == 1,
            "A timer and server warning duplicated the minute reminder");
        for (var i = 0; i < 4; i++) h.Clock.Advance(60);
        Check(h.Messages.SequenceEqual(new[] { "5 minutes remaining", "4 minutes remaining", "3 minutes remaining",
            "2 minutes remaining", "1 minute remaining" }) && h.Clock.PendingTimers == 0,
            "Reminders missed a minute, continued after expiry or retained a timer");

        using var postponed = new Harness();
        postponed.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(3));
        postponed.HoldPosts = true; postponed.Clock.Advance(60);
        postponed.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(5));
        postponed.HoldPosts = false; postponed.DrainPosts();
        Check(postponed.Messages.Count == 2 && postponed.Messages.Last() == "5 minutes remaining" && postponed.Clock.PendingTimers == 1,
            "A queued timer duplicated or ignored a postponed countdown");
        postponed.Clock.Advance(60);
        Check(postponed.Messages.Last() == "4 minutes remaining", "A postponed countdown retained its old deadline");
        postponed.Notifications.ObserveRestart(256, TimeSpan.FromSeconds(70));
        postponed.Clock.Advance(60);
        Check(postponed.Messages.Last() == "10 seconds remaining", "A refined countdown displayed an outdated remaining time");
        postponed.Clock.Advance(10);
        Check(postponed.Clock.PendingTimers == 0, "A short countdown kept notifying after expiry");

        using var delayed = new Harness();
        delayed.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(5));
        delayed.HoldPosts = true; delayed.Clock.Advance(180);
        delayed.HoldPosts = false; delayed.DrainPosts();
        Check(delayed.Messages.SequenceEqual(new[] { "5 minutes remaining", "2 minutes remaining" }),
            "A delayed callback sent a burst of stale reminders");
        return Task.CompletedTask;
    }

    public static Task Cancellation()
    {
        foreach (var reason in new[] { "moved", "disconnected", "cancelled", "disposed" })
        {
            using var h = new Harness();
            h.Notifications.ObserveRestart(256, TimeSpan.FromMinutes(5));
            h.HoldPosts = true; h.Clock.Advance(60);
            switch (reason)
            {
                case "moved": h.Region = 512; h.Notifications.LocationChanged(); break;
                case "disconnected": h.Region = null; h.Notifications.LocationChanged(); break;
                case "cancelled": h.Notifications.Cancel(); break;
                case "disposed": h.Notifications.Dispose(); break;
            }
            h.HoldPosts = false; h.DrainPosts(); h.Clock.Advance(1000);
            Check(h.Messages.Count == 1 && h.Clock.PendingTimers == 0, $"{reason} retained a queued restart reminder");
        }
        return Task.CompletedTask;
    }

    public static async Task PacketsAndPreferences()
    {
        using var h = new ReconnectHarness();
        using var other = new ReconnectHarness(name: "Other Resident");
        h.Connect(); other.Connect();
        h.Fixture.Simulator.Name = "Home";
        var notices = new List<AccountNotification>();
        var otherNotices = new List<AccountNotification>();
        var settings = new GlobalSettings(Path.Combine(h.DirectoryPath, "global.json"));
        var output = new RecordingNotificationOutput();
        using var controller = new NotificationController(settings, output);
        h.Account.NotificationReceived += (account, notice) => { notices.Add(notice); controller.Notify(account, notice, false); };
        other.Account.NotificationReceived += (_, notice) => otherNotices.Add(notice);
        using var elsewhere = new Simulator(h.Account.Client, new IPEndPoint(IPAddress.Loopback, 13001), 512) { Name = "Elsewhere" };
        await Receive(h, RegionRestartChecks.Warning("Home"), elsewhere);
        await Receive(h, RegionRestartChecks.Warning("Other Region"));
        var malformed = RegionRestartChecks.Warning("Home");
        malformed.AlertInfo[0].ExtraParams = Utils.StringToBytes("not LLSD");
        await Receive(h, malformed);
        await Receive(h, new AlertMessagePacket { AlertData = { Message = Utils.StringToBytes("Region will restart in 5 minutes") } });
        Check(notices.Count == 0 && h.Clock.PendingTimers == 0, "An unrelated or malformed alert started restart reminders");
        await Receive(h, RegionRestartChecks.Warning("Home", "Minutes", 5));
        Check(!h.Account.Settings.TeleportOnRegionRestart && notices.Count == 1 && notices[0].Category == NotificationCategory.SimRestarts &&
            notices[0].Title == "Sim restart" && notices[0].Message == "5 minutes remaining" && output.Shown.Count == 1,
            "Restart notification content was wrong or required automatic restart teleports");
        h.HoldPosts = true;
        await Receive(h, RegionRestartChecks.Warning("Home", "Seconds", 121));
        h.Clock.Advance(30);
        h.HoldPosts = false; h.DrainPosts();
        h.Clock.Advance(30);
        await Receive(h, RegionRestartChecks.Warning("Home", "Seconds", 61));
        Check(notices.Count == 2 && notices.Last().Message == "1 minute 1 second remaining",
            "Queued warnings ignored elapsed time or duplicate warnings produced extra popups");
        settings.Update(settings.Value.WithCategory(NotificationCategory.SimRestarts, false));
        Check(!new GlobalSettings(settings.FilePath).Value.SimRestarts && output.Cleared.Last().Category == NotificationCategory.SimRestarts,
            "Disabling restart notifications was not saved or retained an open popup");
        h.Clock.Advance(60);
        Check(notices.Count == 3 && notices.Last().Message == "1 second remaining" && output.Shown.Count == 2,
            "The disabled category still displayed a restart popup");
        await Receive(h, RegionRestartChecks.Warning("Home", "Minutes", 5));
        settings.Update(settings.Value.WithCategory(NotificationCategory.SimRestarts, true));
        h.Clock.Advance(60);
        Check(notices.Count == 4 && output.Shown.Count == 3 && output.Shown.Last().Title == "Sim restart" &&
            output.Shown.Last().Body == "4 minutes remaining" && otherNotices.Count == 0 && other.Clock.PendingTimers == 0,
            "Re-enabling notifications failed or a restart affected another account");
        RegionRestartChecks.ChangeSim(h.Account, elsewhere);
        h.Clock.Advance(600);
        Check(notices.Count == 4 && h.Clock.PendingTimers == 0, "Leaving the region did not stop restart reminders");
        h.HoldPosts = true;
        await Receive(h, RegionRestartChecks.Warning("Elsewhere", "Minutes", 5), elsewhere);
        h.Disconnect();
        h.HoldPosts = false; h.DrainPosts(); h.Clock.Advance(600);
        Check(notices.Count == 4 && h.Clock.PendingTimers == 0, "A queued warning recreated reminders after disconnect");
    }

    private static async Task Receive(ReconnectHarness h, AlertMessagePacket alert, Simulator? sim = null)
    {
        // This callback runs after the account callback in the same SDK work item.
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void AfterAccount(object? sender, PacketReceivedEventArgs e) { if (ReferenceEquals(e.Packet, alert)) received.TrySetResult(); }
        h.Account.Client.Network.RegisterCallback(PacketType.AlertMessage, AfterAccount);
        try { h.Fixture.Receive(alert, sim); await received.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { h.Account.Client.Network.UnregisterCallback(PacketType.AlertMessage, AfterAccount); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Harness : IDisposable
    {
        public ReconnectClock Clock { get; } = new();
        public ulong? Region = 256;
        public List<string> Messages { get; } = new();
        public RegionRestartNotifications Notifications { get; }
        public bool HoldPosts;
        private readonly Queue<Action> _posts = new();
        public Harness() => Notifications = new(Clock, action => { if (HoldPosts) _posts.Enqueue(action); else action(); },
            () => Region, message => Messages.Add(message));
        public void DrainPosts() { while (_posts.TryDequeue(out var action)) action(); }
        public void Dispose() => Notifications.Dispose();
    }
}
