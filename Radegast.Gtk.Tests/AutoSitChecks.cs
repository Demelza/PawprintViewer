using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class AutoSitChecks
{
    public static Task Preferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-auto-sit-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountSettingsStore(directory);
            var who = new SavedLogin("Alice Resident", "https://grid.test/login");
            var target = UUID.Random().ToString();
            Check(store.Load(who, out _) is { AutoSit: false, AutoSitOnRestartReturn: false, AutoSitObjectId: "" }, "Auto Sit was enabled by default");
            var settings = new AccountSettings { AutoSit = true, AutoSitOnRestartReturn = true, AutoSitObjectId = target, AutoReconnect = true };
            store.Save(who, settings);
            Check(new AccountSettingsStore(directory).Load(new SavedLogin("alice.resident", who.LoginUri), out _) == settings,
                "Auto Sit did not survive an independent load or resident alias");
            Check(store.Load(new SavedLogin("Bob Resident", who.LoginUri), out _) is { AutoSit: false, AutoSitOnRestartReturn: false } &&
                store.Load(new SavedLogin(who.AccountName, "https://other.test/login"), out _) is { AutoSit: false, AutoSitOnRestartReturn: false },
                "Auto Sit preferences leaked between residents or grids");
            File.WriteAllText(store.FilePath(who), "{\"AutoReconnect\":true}");
            Check(store.Load(who, out _) is { AutoSit: false, AutoSitOnRestartReturn: false, AutoSitObjectId: "", AutoReconnect: true },
                "Old settings files enabled Auto Sit or lost their existing preferences");
            store.Save(who, settings with { AutoSitObjectId = "incomplete" });
            Check(store.Load(who, out _).AutoSitObjectId == "incomplete", "Incomplete UUID edits could not be saved safely");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    public static async Task TimingAndConfirmation()
    {
        using var h = new ReconnectHarness();
        var packets = h.Fixture.CapturePackets();
        var chair = h.Fixture.Prim(200);
        // UUID-based sitting can use loaded furniture beyond the Objects tab's list radius.
        chair.Position = new Vector3(80, 0, 0);
        var seat = h.Fixture.Prim(201, chair.LocalID);
        h.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = chair.ID.ToString() });
        h.Connect();
        Check(h.Clock.PendingTimers == 1 && h.Account.GetNearbyObjects().All(item => item.Id != chair.ID),
            "Login did not schedule one timer or the distant-object setup was invalid");
        h.Clock.Advance(59);
        Check(!Requests(packets()).Any(), "Auto Sit ran before one minute");
        h.Connect(); // A duplicate success event must not restart the deadline.
        h.Clock.Advance(1);
        var request = Requests(packets()).Single();
        Check(request.TargetObject.TargetID == chair.ID && request.AgentData.AgentID == h.Fixture.Owner &&
            h.Account.AutoSitStatus.Contains("waiting") && h.Clock.PendingTimers == 0,
            "Auto Sit used another UUID/account, waited another minute or reported premature success");
        h.Fixture.Receive(new AvatarSitResponsePacket { SitObject = { ID = UUID.Random() } });
        await Task.Delay(40);
        Check(!packets().OfType<AgentSitPacket>().Any(), "An unrelated furniture reply seated the avatar");
        h.Fixture.Receive(new AvatarSitResponsePacket { SitObject = { ID = seat.ID } });
        var sent = new List<Packet>();
        await WaitUntil(() => { sent.AddRange(packets()); return sent.OfType<AgentSitPacket>().Any(); });
        Check(!h.Account.AutoSitStatus.Contains("completed"), "Auto Sit succeeded before server seat confirmation");
        h.Fixture.ChangeSeat(seat.LocalID);
        await WaitUntil(() => h.Account.AutoSitStatus == "Auto Sit completed." && !SitBusy(h.Account));
        Check(h.Account.IsSitting, "The confirmed automatic sit did not seat the avatar");
        h.Clock.Advance(3600);
        Check(!Requests(packets()).Any() && h.Clock.PendingTimers == 0, "Auto Sit repeated after completing");
    }

    public static async Task Cancellation()
    {
        foreach (var action in new[] { "disable", "disconnect", "logout", "new-login", "dispose" })
        {
            using var h = new ReconnectHarness();
            var packets = h.Fixture.CapturePackets();
            var chair = h.Fixture.Prim(200);
            h.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = chair.ID.ToString() });
            h.Connect();
            h.HoldPosts = true; h.Clock.Advance(60); h.HoldPosts = false;
            switch (action)
            {
                case "disable": h.Account.UpdateSettings(h.Account.Settings with { AutoSit = false }); break;
                case "disconnect": h.Disconnect(); break;
                case "logout": h.LoggedOut(); break;
                case "new-login": h.Login(); break;
                case "dispose": h.Account.Dispose(); break;
            }
            h.DrainPosts(); h.Clock.Advance(100);
            Check(!Requests(packets()).Any() && h.Clock.PendingTimers == 0, $"{action} left a queued Auto Sit active");
        }
        using var active = new ReconnectHarness();
        var capture = active.Fixture.CapturePackets();
        var objectToSit = active.Fixture.Prim(200);
        active.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = objectToSit.ID.ToString() });
        active.Connect(); active.Clock.Advance(60);
        Check(Requests(capture()).Count() == 1, "The active cancellation setup did not request a sit");
        active.Account.UpdateSettings(active.Account.Settings with { AutoSit = false });
        await WaitUntil(() => !SitBusy(active.Account));
        active.Fixture.Receive(new AvatarSitResponsePacket { SitObject = { ID = objectToSit.ID } });
        await Task.Delay(40);
        Check(!capture().OfType<AgentSitPacket>().Any(), "Disabling Auto Sit accepted a late server response");
    }

    public static async Task EditsAndReconnect()
    {
        using var h = new ReconnectHarness();
        var packets = h.Fixture.CapturePackets();
        var first = h.Fixture.Prim(200);
        var second = h.Fixture.Prim(300);
        h.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = first.ID.ToString(), AutoReconnect = true, ReconnectDelaySeconds = 2 });
        h.Connect(); h.Clock.Advance(20);
        h.Account.UpdateSettings(h.Account.Settings with { AutoSitObjectId = "partial" });
        Check(h.Clock.PendingTimers == 0 && h.Account.AutoSitStatus.Contains("valid furniture UUID"), "An incomplete edit left the old UUID scheduled");
        h.Account.UpdateSettings(h.Account.Settings with { AutoSitObjectId = second.ID.ToString() });
        h.Clock.Advance(39);
        Check(!Requests(packets()).Any(), "Editing the UUID triggered an early sit");
        h.Clock.Advance(1);
        Check(Requests(packets()).Single().TargetObject.TargetID == second.ID, "Editing restarted the minute or used the previous UUID");
        h.Disconnect();
        await WaitUntil(() => !SitBusy(h.Account));
        h.Clock.Advance(2); h.Connect();
        h.Clock.Advance(59);
        Check(!Requests(packets()).Any(), "Reconnect reused the old login's elapsed time");
        h.Clock.Advance(1);
        Check(Requests(packets()).Single().TargetObject.TargetID == second.ID, "Reconnect did not schedule a fresh Auto Sit");
        h.Account.UpdateSettings(h.Account.Settings with { AutoSit = false });
        await WaitUntil(() => !SitBusy(h.Account));
        h.Clock.Advance(60);
        h.Account.UpdateSettings(h.Account.Settings with { AutoSit = true });
        Check(h.Clock.PendingTimers == 0 && h.Account.AutoSitStatus.Contains("next login"), "Enabling after the login minute caused an immediate sit");
    }

    public static async Task PermissionsAndInvalidTargets()
    {
        foreach (var target in new[] { "", "bad-uuid", UUID.Zero.ToString() })
        {
            using var h = new ReconnectHarness();
            var packets = h.Fixture.CapturePackets();
            h.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = target });
            h.Connect(); h.Clock.Advance(200);
            Check(h.Clock.PendingTimers == 0 && !Requests(packets()).Any() && h.Account.AutoSitStatus.Contains("valid furniture UUID"),
                "A missing/invalid/zero UUID scheduled an automatic sit");
        }
        foreach (var state in new[] { "already-seated", "locked", "distance-locked", "missing", "attachment" })
        {
            using var h = new ReconnectHarness();
            var packets = h.Fixture.CapturePackets();
            var chair = h.Fixture.Prim(200);
            chair.Position = new Vector3(10, 0, 0);
            var target = state == "missing" ? UUID.Random() : state == "attachment" ? h.Fixture.Attachment.ID : chair.ID;
            h.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = target.ToString() });
            h.Connect();
            if (state == "already-seated") h.Fixture.ChangeSeat(chair.LocalID);
            if (state == "locked") await h.Fixture.Command("@sit=n");
            if (state == "distance-locked") await h.Fixture.Command("@sittp=n");
            packets(); h.Clock.Advance(60);
            Check(!Requests(packets()).Any() && !SitBusy(h.Account) && h.Clock.PendingTimers == 0,
                $"Auto Sit ignored {state} or retained a worker/timer");
            Check(h.Account.AutoSitStatus.Contains(state == "already-seated" ? "skipped" : "failed"), "A skipped/failed automatic sit lacked feedback");
        }
    }

    public static async Task AccountIsolation()
    {
        var clock = new ReconnectClock();
        using var first = new ReconnectHarness(clock);
        using var second = new ReconnectHarness(clock, "Bob Resident");
        var firstPackets = first.Fixture.CapturePackets();
        var secondPackets = second.Fixture.CapturePackets();
        var firstChair = first.Fixture.Prim(200);
        var secondChair = second.Fixture.Prim(200);
        first.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = firstChair.ID.ToString() });
        second.Account.UpdateSettings(new() { AutoSit = true, AutoSitObjectId = secondChair.ID.ToString() });
        first.Connect(); clock.Advance(20); second.Connect(); clock.Advance(40);
        var sent = Requests(firstPackets()).Single();
        Check(sent.TargetObject.TargetID == firstChair.ID && sent.AgentData.AgentID == first.Fixture.Owner &&
            !Requests(secondPackets()).Any(), "Accounts shared an Auto Sit target, identity or timer");
        first.Account.UpdateSettings(first.Account.Settings with { AutoSit = false });
        await WaitUntil(() => !SitBusy(first.Account));
        clock.Advance(20);
        sent = Requests(secondPackets()).Single();
        Check(sent.TargetObject.TargetID == secondChair.ID && sent.AgentData.AgentID == second.Fixture.Owner,
            "Cancelling one account cancelled another's Auto Sit");
        second.Account.UpdateSettings(second.Account.Settings with { AutoSit = false });
        await WaitUntil(() => !SitBusy(second.Account));
    }

    public static async Task RestartReturnCancellationAndEdits()
    {
        foreach (var action in new[] { "disable", "disable-protection", "disconnect", "logout", "new-login", "leave", "dispose" })
        {
            using var h = new ReconnectHarness();
            var packets = h.Fixture.CapturePackets();
            var chair = h.Fixture.Prim(200);
            h.Account.UpdateSettings(new() { AutoSitOnRestartReturn = true, TeleportOnRegionRestart = true,
                AutoSitObjectId = chair.ID.ToString() });
            h.Connect(); h.Clock.Advance(120);
            Check(h.Clock.PendingTimers == 0 && !Requests(packets()).Any(), "Return-only Auto Sit ran at login");
            RestartReturned(h);
            Check(h.Clock.PendingTimers == 1, "The confirmed return did not schedule Auto Sit independently of login");
            h.HoldPosts = true; h.Clock.Advance(60); h.HoldPosts = false;
            using var elsewhere = new Simulator(h.Account.Client,
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 13002), h.Fixture.Simulator.Handle + 256);
            switch (action)
            {
                case "disable": h.Account.UpdateSettings(h.Account.Settings with { AutoSitOnRestartReturn = false }); break;
                case "disable-protection": h.Account.UpdateSettings(h.Account.Settings with { TeleportOnRegionRestart = false }); break;
                case "disconnect": h.Disconnect(); break;
                case "logout": h.LoggedOut(); break;
                case "new-login": h.Login(); break;
                case "leave": RegionRestartChecks.ChangeSim(h.Account, elsewhere); break;
                case "dispose": h.Account.Dispose(); break;
            }
            h.DrainPosts(); h.Clock.Advance(100);
            Check(!Requests(packets()).Any() && h.Clock.PendingTimers == 0, $"{action} left a queued return Auto Sit alive");
        }
        using var edited = new ReconnectHarness();
        var capture = edited.Fixture.CapturePackets();
        var target = edited.Fixture.Prim(200);
        edited.Account.UpdateSettings(new() { AutoSitOnRestartReturn = true, TeleportOnRegionRestart = true,
            AutoSitObjectId = "incomplete" });
        edited.Connect(); edited.Clock.Advance(120); RestartReturned(edited);
        Check(edited.Clock.PendingTimers == 0 && edited.Account.AutoSitStatus.Contains("valid furniture UUID"),
            "A restart return accepted an incomplete UUID");
        edited.Clock.Advance(20);
        edited.Account.UpdateSettings(edited.Account.Settings with { AutoSitObjectId = target.ID.ToString() });
        edited.Connect(); // A duplicate login event must not disturb the return deadline.
        edited.Clock.Advance(39);
        Check(!Requests(capture()).Any(), "Editing the return UUID seated the avatar early");
        edited.Clock.Advance(1);
        Check(Requests(capture()).Single().TargetObject.TargetID == target.ID, "The edited UUID reset the return's 60-second loading delay");
        edited.Account.UpdateSettings(edited.Account.Settings with { AutoSitOnRestartReturn = false });
        await WaitUntil(() => !SitBusy(edited.Account));
        edited.Fixture.Receive(new AvatarSitResponsePacket { SitObject = { ID = target.ID } });
        await Task.Delay(40);
        Check(!capture().OfType<AgentSitPacket>().Any(), "Disabling return Auto Sit accepted a late server reply");
    }

    private static void RestartReturned(ReconnectHarness h) => typeof(AccountSession)
        .GetMethod("AutoSitOnRestartReturn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(h.Account,
            new object[] { new RestartLocation(new GridRegion { RegionHandle = h.Fixture.Simulator.Handle }, new(10, 20, 30)) });

    private static IEnumerable<AgentRequestSitPacket> Requests(IEnumerable<Packet> packets) => packets.OfType<AgentRequestSitPacket>();
    private static bool SitBusy(AccountSession account) => (int)typeof(AccountSession)
        .GetField("_sitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(account)! != 0;
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(10, cancel.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
