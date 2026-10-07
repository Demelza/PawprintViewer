using System.Net;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Radegast.Gtk;

internal static class RegionRestartChecks
{
    public static Task PreferencesAndWarnings()
    {
        using var h = new ReconnectHarness();
        var identity = new SavedLogin(h.Name, h.Grid.LoginURI);
        var settings = new AccountSettings
        {
            TeleportOnRegionRestart = true, RestartDestinationRegion = "Safe Region",
            RestartDestinationX = 45.5f, RestartDestinationY = 67, RestartDestinationZ = 901,
            ReturnDelayMinutes = 12
        };
        h.Account.UpdateSettings(settings);
        Check(h.Store.Load(identity, out _) == settings, "Restart destination or return delay was not saved");
        Check(h.Store.Load(new SavedLogin("Other Resident", h.Grid.LoginURI), out _) == new AccountSettings(),
            "Restart preferences were shared with another account");
        File.WriteAllText(h.Store.FilePath(identity), "{\"AutoReconnect\":true,\"ReconnectDelaySeconds\":7}");
        Check(h.Store.Load(identity, out _) == new AccountSettings { AutoReconnect = true, ReconnectDelaySeconds = 7 },
            "Older preference files lost reconnect settings or enabled restart teleports");
        foreach (var json in new[] { "{\"ReturnDelayMinutes\":0}", "{\"ReturnDelayMinutes\":1441}",
            "{\"RestartDestinationX\":-1}", "{\"RestartDestinationRegion\":null}" })
        {
            File.WriteAllText(h.Store.FilePath(identity), json);
            Check(h.Store.Load(identity, out var error) == new AccountSettings() && error != null,
                "Invalid restart settings were accepted");
        }
        foreach (var units in new[] { "Minutes", "Seconds" })
        {
            var warning = Warning("Home", units);
            Check(AccountSession.IsRegionRestartWarning(warning, "home"), "A valid structured restart warning was missed");
            Check(!AccountSession.IsRegionRestartWarning(warning, "Other Region"), "Another region's warning was accepted");
        }
        Check(!AccountSession.IsRegionRestartWarning(new AlertMessagePacket
            { AlertData = { Message = Utils.StringToBytes("Home will restart in 1 minute") } }, "Home"),
            "An unstructured message initiated a teleport");
        var invalid = Warning("Home", "Seconds");
        invalid.AlertInfo[0].ExtraParams = Utils.StringToBytes("not LLSD");
        Check(!AccountSession.IsRegionRestartWarning(invalid, "Home"), "Malformed restart metadata was accepted");
        invalid = Warning("Home", "Seconds", -1);
        Check(!AccountSession.IsRegionRestartWarning(invalid, "Home"), "A cancelled/negative countdown was accepted");
        return Task.CompletedTask;
    }

    public static async Task TimingAndRetries()
    {
        using var h = new RecoveryHarness();
        h.Recovery.ObserveRestart(h.Home.RegionHandle);
        Check(h.Calls.Count == 1 && h.Calls[0].Region.RegionHandle == h.Safe.RegionHandle &&
            h.Calls[0].Position == new Vector3(70, 80, 90) && h.Clock.PendingTimers == 1,
            "Restart did not teleport to the configured position and start one return timer");
        h.Recovery.ObserveRestart(h.Home.RegionHandle);
        h.Recovery.ObserveRestart(h.Safe.RegionHandle);
        h.Current = h.Current! with { Position = new Vector3(200, 210, 220) };
        h.Clock.Advance(119);
        Check(h.Calls.Count == 1, "A duplicate warning or elapsed time caused an early return");
        h.FailReturn = true;
        h.Clock.Advance(1);
        Check(h.Calls.Count == 2 && h.Calls[1].Region.RegionHandle == h.Home.RegionHandle && h.Calls[1].Position == new Vector3(10, 20, 30) &&
            h.Recovery.Status.Contains("Retrying") && h.Clock.PendingTimers == 1,
            "Return did not retain the original position or schedule recovery when the region was unavailable");
        h.Clock.Advance(59);
        Check(h.Calls.Count == 2, "A failed return retried continuously");
        h.FailReturn = false;
        h.Clock.Advance(1);
        h.Clock.Advance(5000);
        Check(h.Calls.Count == 3 && h.Current?.Region.RegionHandle == h.Home.RegionHandle && h.Clock.PendingTimers == 0,
            "Return retries failed to finish or retained a timer after arrival");

        using var pending = new RecoveryHarness();
        pending.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Recovery.ObserveRestart(pending.Home.RegionHandle);
        pending.Recovery.ObserveRestart(pending.Home.RegionHandle);
        pending.Clock.Advance(1000);
        Check(pending.Calls.Count == 1 && pending.Clock.PendingTimers == 0, "The return delay started before arrival or warnings overlapped");
        pending.Gate.SetResult();
        await WaitUntil(() => pending.Clock.PendingTimers == 1);
        pending.Clock.Advance(119);
        Check(pending.Calls.Count == 1, "The configured wait was measured from the warning instead of arrival");
        pending.Clock.Advance(1);
        await WaitUntil(() => pending.Calls.Count == 2);
    }

    public static Task CancellationAndIsolation()
    {
        foreach (var reason in new[] { "disabled", "disconnected", "moved", "disposed" })
        {
            using var h = new RecoveryHarness();
            h.Recovery.ObserveRestart(h.Home.RegionHandle);
            h.HoldPosts = true;
            h.Clock.Advance(120); // Its UI callback is queued, not yet executed.
            switch (reason)
            {
                case "disabled": h.Recovery.UpdateSettings(h.Settings with { TeleportOnRegionRestart = false }); break;
                case "disconnected": h.Current = null; h.Recovery.Disconnected(); break;
                case "moved": h.Current = new(new GridRegion { Name = "Elsewhere", RegionHandle = 999 }, new(1, 2, 3)); h.Recovery.LocationChanged(); break;
                case "disposed": h.Recovery.Dispose(); break;
            }
            h.HoldPosts = false; h.DrainPosts(); h.Clock.Advance(5000);
            Check(h.Calls.Count == 1 && h.Clock.PendingTimers == 0, $"{reason} retained a queued return");
        }
        using var delayed = new RecoveryHarness();
        delayed.Recovery.ObserveRestart(delayed.Home.RegionHandle);
        delayed.Clock.Advance(60);
        delayed.Recovery.UpdateSettings(delayed.Settings with { ReturnDelayMinutes = 4 });
        delayed.Clock.Advance(239);
        Check(delayed.Calls.Count == 1, "Changing the delay retained the old deadline");
        delayed.Clock.Advance(1);
        Check(delayed.Calls.Count == 2, "Changing the delay did not schedule the new deadline");
        using var first = new RecoveryHarness();
        using var second = new RecoveryHarness();
        first.Recovery.ObserveRestart(first.Home.RegionHandle);
        Check(second.Calls.Count == 0 && second.Clock.PendingTimers == 0, "Restart recovery affected another account");
        second.Recovery.ObserveRestart(second.Home.RegionHandle);
        first.Recovery.UpdateSettings(first.Settings with { TeleportOnRegionRestart = false });
        second.Clock.Advance(120);
        Check(first.Calls.Count == 1 && second.Calls.Count == 2, "Disabling one account cancelled another account's return");

        using var account = new ReconnectHarness();
        account.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 10 });
        account.Connect(); account.Disconnect(); account.Clock.Advance(5);
        account.Account.UpdateSettings(account.Account.Settings with { RestartDestinationRegion = "Safe", ReturnDelayMinutes = 3 });
        account.Clock.Advance(5);
        Check(account.Attempts == 2, "Editing restart preferences postponed automatic reconnect");
        return Task.CompletedTask;
    }

    public static async Task FailedDepartureAndStaleCompletion()
    {
        using var same = new RecoveryHarness();
        same.Lookup = same.Home;
        same.Recovery.ObserveRestart(same.Home.RegionHandle);
        Check(same.Calls.Count == 0 && same.Clock.PendingTimers == 0 && same.Recovery.Status.Contains("another region"),
            "A destination in the restarting region was accepted");
        using var failed = new RecoveryHarness();
        failed.FailDeparture = true;
        failed.Recovery.ObserveRestart(failed.Home.RegionHandle);
        failed.Clock.Advance(5000);
        Check(failed.Calls.Count == 1 && failed.Clock.PendingTimers == 0 && failed.Current?.Region.RegionHandle == failed.Home.RegionHandle,
            "A failed departure scheduled a return or lost the original location");
        using var queued = new RecoveryHarness();
        queued.HoldPosts = true;
        queued.Recovery.ObserveRestart(queued.Home.RegionHandle);
        queued.Recovery.UpdateSettings(queued.Settings with { TeleportOnRegionRestart = false });
        queued.HoldPosts = false; queued.DrainPosts(); queued.Clock.Advance(5000);
        Check(queued.Calls.Count == 1 && queued.Clock.PendingTimers == 0, "An old arrival callback scheduled a return after disabling");
        using var pending = new RecoveryHarness();
        pending.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Recovery.ObserveRestart(pending.Home.RegionHandle);
        var token = pending.Tokens.Single();
        pending.Recovery.Disconnected();
        Check(token.IsCancellationRequested, "Disconnect did not cancel the active teleport request");
        pending.Gate.SetResult();
        await WaitUntil(() => pending.Recovery.Status.Contains("cancelled"));
        Check(pending.Clock.PendingTimers == 0, "A cancelled teleport started the return timer");
    }

    public static async Task AccountPacketsAndPermissions()
    {
        using var h = new ReconnectHarness();
        using var other = new ReconnectHarness(name: "Other Resident");
        h.Connect(); other.Connect();
        var f = h.Fixture;
        f.Simulator.Name = "Home";
        f.Simulator.Handle = Utils.UIntsToLong(1000 * 256, 1200 * 256);
        h.Account.Client.Self.RelativePosition = new(10, 20, 30);
        var packets = f.CapturePackets();
        var otherPackets = other.Fixture.CapturePackets();
        h.Account.UpdateSettings(new()
        {
            TeleportOnRegionRestart = true, RestartDestinationRegion = "Safe", ReturnDelayMinutes = 2,
            RestartDestinationX = 45, RestartDestinationY = 55, RestartDestinationZ = 65
        });
        using var safe = new Simulator(h.Account.Client, new IPEndPoint(IPAddress.Loopback, 13001),
            Utils.UIntsToLong(1001 * 256, 1200 * 256)) { Name = "Safe" };
        f.Receive(Warning("Home"), safe);
        f.Receive(Warning("Other Region"));
        Check(packets().Count == 0, "A neighbouring simulator or unrelated region triggered recovery");
        await f.Command("@tploc=n");
        f.Receive(Warning("Home"));
        await WaitUntil(() => h.Account.RestartTeleportStatus.StartsWith("Could not leave"));
        Check(packets().Count == 0 && h.Account.RestartTeleportStatus.Contains("RLV"), "RLV location locks were bypassed");
        await f.Command("@clear,showworldmap=n");
        f.Receive(Warning("Home", "Seconds"));
        f.Receive(Warning("Home"));
        MapNameRequestPacket? lookup = null;
        await WaitUntil(() => (lookup ??= packets().OfType<MapNameRequestPacket>().SingleOrDefault()) != null);
        Check(Utils.BytesToString(lookup!.NameData.Name) == "safe", "Restart lookup used a wrong region");
        f.Receive(new MapBlockReplyPacket
        {
            Data = new[] { new MapBlockReplyPacket.DataBlock { Name = Utils.StringToBytes("Safe"), X = 1001, Y = 1200, Access = (byte)SimAccess.PG } }
        });
        await WaitUntil(() => Field<int>(h.Account, "_mapTeleportInProgress") == 1);
        QueueRunning(h.Account, f.Simulator);
        TeleportLocationRequestPacket? request = null;
        await WaitUntil(() => (request ??= packets().OfType<TeleportLocationRequestPacket>().SingleOrDefault()) != null);
        Check(request!.AgentData.AgentID == f.Owner && request.Info.RegionHandle == safe.Handle && request.Info.Position == new Vector3(45, 55, 65),
            "Departure used a wrong account/destination or a hidden map blocked automation");
        Check(h.Clock.PendingTimers == 0, "Return was scheduled without server confirmation");
        h.Account.Client.Network.CurrentSim = safe;
        f.Receive(LocalTeleport(f.Owner, new(45, 55, 65)), safe);
        await WaitUntil(() => h.Clock.PendingTimers == 1);
        h.Clock.Advance(119);
        Check(!packets().OfType<TeleportLocationRequestPacket>().Any(), "Return preceded the configured delay");
        h.Clock.Advance(1);
        QueueRunning(h.Account, safe);
        request = null;
        await WaitUntil(() => (request ??= packets().OfType<TeleportLocationRequestPacket>().SingleOrDefault()) != null);
        Check(request!.Info.RegionHandle == f.Simulator.Handle && request.Info.Position == new Vector3(10, 20, 30),
            "Return forgot the original region or exact coordinates");
        h.Account.Client.Network.CurrentSim = f.Simulator;
        f.Receive(LocalTeleport(f.Owner, new(10, 20, 30)));
        await WaitUntil(() => h.Account.RestartTeleportStatus.StartsWith("Returned"));
        Check(h.Clock.PendingTimers == 0 && !otherPackets().OfType<TeleportLocationRequestPacket>().Any(),
            "Completed recovery left a timer or teleported another account");
    }

    private static TeleportLocalPacket LocalTeleport(UUID agent, Vector3 position) => new()
        { Info = { AgentID = agent, Position = position, LookAt = new(0, 1, 0) } };
    private static void QueueRunning(AccountSession account, Simulator sim) => typeof(NetworkManager)
        .GetMethod("OnEventQueueRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(account.Client.Network, new object[] { new EventQueueRunningEventArgs(sim) });
    internal static AlertMessagePacket Warning(string name, string units = "Minutes", int count = 1) => new()
    {
        AlertData = { Message = Utils.StringToBytes("Region restart warning") },
        AlertInfo = new[] { new AlertMessagePacket.AlertInfoBlock
        {
            Message = Utils.StringToBytes("RegionRestart" + units),
            ExtraParams = OSDParser.SerializeLLSDXmlBytes(new OSDMap
                { ["NAME"] = OSD.FromString(name), [units.ToUpperInvariant()] = OSD.FromInteger(count) })
        } }
    };
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class RecoveryHarness : IDisposable
    {
        public GridRegion Home { get; } = new() { Name = "Home", RegionHandle = 256 };
        public GridRegion Safe { get; } = new() { Name = "Safe", RegionHandle = 512 };
        public GridRegion? Lookup;
        public RestartLocation? Current;
        public ReconnectClock Clock { get; } = new();
        public RegionRestartRecovery Recovery { get; }
        public AccountSettings Settings { get; } = new()
        {
            TeleportOnRegionRestart = true, RestartDestinationRegion = "Safe",
            RestartDestinationX = 70, RestartDestinationY = 80, RestartDestinationZ = 90, ReturnDelayMinutes = 2
        };
        public List<(GridRegion Region, Vector3 Position)> Calls { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        public TaskCompletionSource? Gate;
        public bool FailReturn, FailDeparture, HoldPosts;
        private readonly Queue<Action> _posts = new();
        public RecoveryHarness()
        {
            Current = new(Home, new(10, 20, 30));
            Recovery = new(Clock, action => { if (HoldPosts) _posts.Enqueue(action); else action(); }, () => Current,
                (name, token) => Task.FromResult(Lookup ?? Safe), Teleport);
            Recovery.UpdateSettings(Settings);
        }
        private async Task Teleport(GridRegion region, Vector3 position, CancellationToken token)
        {
            Calls.Add((region, position)); Tokens.Add(token);
            if (Gate != null) await Gate.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if ((region.RegionHandle == Home.RegionHandle && FailReturn) || FailDeparture)
                throw new InvalidOperationException("Region unavailable");
            Current = new(region, position);
            Recovery.LocationChanged();
        }
        public void DrainPosts() { while (_posts.TryDequeue(out var action)) action(); }
        public void Dispose() => Recovery.Dispose();
    }
}
