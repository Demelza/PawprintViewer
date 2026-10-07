using System.Reflection;
using LibreMetaverse;
using Radegast;
using Radegast.Gtk;
using GridDefinition = Radegast.Grid;

internal static class ReconnectChecks
{
    public static async Task ForcedDisconnect()
    {
        using var first = new ReconnectHarness();
        using var second = new ReconnectHarness(name: "Bob Resident");
        first.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 3 });
        first.Connect(); second.Connect();
        Check(first.Account.CanTestDisconnect, "The test action was unavailable to a connected account");
        await first.Account.DisconnectForReconnectTestAsync();
        Check(!first.Account.IsConnected && !first.Account.Client.Network.Connected &&
            first.Account.Client.Network.Simulators.Count == 0 && second.Account.IsConnected &&
            first.Clock.PendingTimers == 1 && !first.Account.CanTestDisconnect,
            "Test disconnect did not shut down the real network, affected another account or skipped reconnect");
        await first.Account.DisconnectForReconnectTestAsync();
        first.Clock.Advance(2);
        Check(first.Attempts == 1, "Test disconnect skipped the configured delay");
        first.Clock.Advance(1);
        Check(first.Attempts == 2, "Test disconnect was treated as intentional logout");
        await second.Account.DisconnectForReconnectTestAsync();
        second.Clock.Advance(50);
        Check(!second.Account.IsConnected && second.Clock.PendingTimers == 0 && second.Attempts == 1,
            "Test disconnect enabled reconnect for an account whose setting was disabled");
    }

    public static Task Preferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-reconnect-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountSettingsStore(directory);
            var who = new SavedLogin("Alice Resident", "https://grid.test/login");
            var other = new SavedLogin("Alice Resident", "https://other.test/login");
            Check(store.Load(who, out _).AutoReconnect == false, "Reconnect was enabled by default");
            store.Save(who, new() { AutoReconnect = true, ReconnectDelaySeconds = 12 });
            // Independently constructed stores must not overwrite another account's settings.
            new AccountSettingsStore(directory).Save(other, new() { AutoReconnect = true, ReconnectDelaySeconds = 45 });
            Check(store.Load(new SavedLogin("alice.resident", who.LoginUri), out _) ==
                new AccountSettings { AutoReconnect = true, ReconnectDelaySeconds = 12 } &&
                store.Load(other, out _).ReconnectDelaySeconds == 45 &&
                store.Load(new SavedLogin("Bob Resident", who.LoginUri), out _).AutoReconnect == false,
                "Account aliases did not share settings or unrelated accounts/grids did");
            File.WriteAllText(store.FilePath(who), "{\"AutoReconnect\":true}");
            Check(store.Load(who, out _).ReconnectDelaySeconds == 30, "An older preference file lost new-field defaults");
            foreach (var broken in new[] { "broken json", "{\"AutoReconnect\":true,\"ReconnectDelaySeconds\":0}" })
            {
                File.WriteAllText(store.FilePath(who), broken);
                Check(store.Load(who, out var error) == new AccountSettings() && error != null,
                    "Invalid settings scheduled an unbounded retry or were silently accepted");
            }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Saving preferences left temporary files");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    public static Task TimingAndRetries()
    {
        using var h = new ReconnectHarness();
        h.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 7 });
        h.Disconnect(); h.Clock.Advance(10);
        Check(h.Attempts == 1, "An account that never logged in began reconnecting");
        h.Connect();
        h.Account.Net.LoginOptions.MfaHash = "synthetic-trust-hash";
        h.Disconnect(); h.Clock.Advance(6);
        Check(h.Attempts == 1, "Reconnect ran before its configured delay");
        h.Disconnect(); // Duplicate network events must not restart the delay.
        h.Clock.Advance(1);
        Check(h.Attempts == 2 && h.ObservedLast == (h.Name, h.Grid.LoginURI, "synthetic-password", StartLocationType.Last, "", "synthetic-trust-hash"),
            "Reconnect lost its account, grid, password, last location or MFA trust state, or replayed a one-time code");
        h.Clock.Advance(40);
        Check(h.Attempts == 2, "Reconnect overlapped an unfinished login");
        h.Fail("timeout"); h.Clock.Advance(6);
        Check(h.Attempts == 2, "A failed retry skipped the configured delay");
        h.Clock.Advance(1);
        Check(h.Attempts == 3, "A transient failure did not retry");
        h.Connect(); h.Clock.Advance(50);
        Check(h.Attempts == 3, "Successful reconnection retained a timer");
        h.Disconnect(NetworkManager.DisconnectType.ServerInitiated); h.Clock.Advance(7);
        Check(h.Attempts == 4, "A later unexpected server disconnect could not reconnect");
        return Task.CompletedTask;
    }

    public static Task CancellationAndChanges()
    {
        foreach (var cancel in new[] { "disable", "dispose", "logout", "client disconnect", "manual login" })
        {
            using var h = new ReconnectHarness();
            h.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 4 });
            h.Connect(); h.Disconnect();
            // The timer fired on a worker thread, but its GTK callback has not run yet.
            h.HoldPosts = true; h.Clock.Advance(4); h.HoldPosts = false;
            switch (cancel)
            {
                case "disable": h.Account.UpdateSettings(h.Account.Settings with { AutoReconnect = false }); break;
                case "dispose": h.Account.Dispose(); break;
                case "logout": h.LoggedOut(); break;
                case "client disconnect": h.Disconnect(NetworkManager.DisconnectType.ClientInitiated); break;
                case "manual login": h.Login(); break;
            }
            var expected = h.Attempts;
            h.DrainPosts(); h.Clock.Advance(50);
            Check(h.Attempts == expected && h.Clock.PendingTimers == 0, $"{cancel} left a pending/queued reconnect alive");
        }
        using var changed = new ReconnectHarness();
        changed.Connect(); changed.Disconnect(); changed.Clock.Advance(30);
        Check(changed.Attempts == 1, "Disabled reconnect scheduled a retry");
        changed.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 4 });
        changed.Clock.Advance(2);
        changed.Account.UpdateSettings(changed.Account.Settings with { ReconnectDelaySeconds = 9 });
        changed.Clock.Advance(8);
        Check(changed.Attempts == 1, "Changing the delay retained its previous deadline");
        changed.Clock.Advance(1);
        Check(changed.Attempts == 2, "Enabling reconnect on a disconnected account or changing the delay did not reschedule it");
        changed.Account.UpdateSettings(changed.Account.Settings with { AutoReconnect = false });
        changed.Fail("timeout"); changed.Clock.Advance(30);
        Check(changed.Attempts == 2, "Disabling reconnect during login allowed more retries");
        return Task.CompletedTask;
    }

    public static Task AccountsAndAuthentication()
    {
        var clock = new ReconnectClock();
        using var first = new ReconnectHarness(clock, "Alice Resident", "https://first.test/login");
        using var second = new ReconnectHarness(clock, "Bob Resident", "https://second.test/login");
        first.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 3 });
        second.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 8 });
        first.Connect(); second.Connect(); first.Disconnect(); second.Disconnect();
        clock.Advance(3);
        Check(first.Attempts == 2 && second.Attempts == 1, "Accounts shared their reconnect timer");
        first.Fail("mfa_challenge"); clock.Advance(5);
        Check(second.Attempts == 2 && second.ObservedLast.Name == "Bob Resident" &&
            second.ObservedLast.Uri == second.Grid.LoginURI, "One account reconnected using another account's login");
        first.Clock.Advance(100);
        Check(first.Attempts == 2 && first.Account.Status.Contains("Log in again") && second.Attempts == 2,
            "MFA required an unattended retry or another account's pending login overlapped");
        second.Fail("key"); clock.Advance(100);
        Check(second.Attempts == 2 && clock.PendingTimers == 0, "An invalid password was retried indefinitely");
        return Task.CompletedTask;
    }

    public static Task SaveFailure()
    {
        using var h = new ReconnectHarness();
        Directory.CreateDirectory(h.Store.FilePath(new SavedLogin(h.Name, h.Grid.LoginURI)));
        h.Account.UpdateSettings(new() { AutoReconnect = true, ReconnectDelaySeconds = 2 });
        Check(h.Account.Settings.AutoReconnect && h.Account.SettingsError != null &&
            !Directory.EnumerateFiles(h.DirectoryPath, "*.tmp").Any(), "Failed saving did not retain this session's settings or leaked a temporary file");
        h.Connect(); h.Disconnect(); h.Clock.Advance(2);
        Check(h.Attempts == 2, "A save failure prevented this session's reconnect setting from working");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed class ReconnectHarness : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "pawprint-reconnect-" + Guid.NewGuid().ToString("N"));
    public AccountSettingsStore Store { get; }
    public AccountSession Account { get; }
    public Fixture Fixture { get; }
    public ReconnectClock Clock { get; }
    public GridDefinition Grid { get; }
    public string Name { get; }
    public int Attempts { get; private set; }
    public bool HoldPosts { get; set; }
    public (string Name, string Uri, string Password, StartLocationType Location, string Token, string Hash) ObservedLast { get; private set; }
    private readonly Queue<Action> _posts = new();

    public ReconnectHarness(ReconnectClock? clock = null, string name = "Alice Resident", string uri = "https://grid.test/login",
        Action<Action>? post = null)
    {
        Clock = clock ?? new();
        Name = name;
        Grid = new("test", "Test Grid", uri);
        Store = new(DirectoryPath);
        Account = new(action => { if (HoldPosts) _posts.Enqueue(action); else if (post != null) post(action); else action(); }, Store, Clock);
        Fixture = new(Account);
        Fixture.CapturePackets();
        Account.Net.ClientLoggingIn += (_, e) =>
        {
            var options = Account.Net.LoginOptions;
            Attempts++;
            ObservedLast = (options.FullName, options.Grid!.LoginURI, options.Password!, options.StartLocation,
                options.MfaToken ?? "", options.MfaHash ?? "");
            e.Cancel = true; // Capture the request without connecting to a real grid.
        };
        Login();
    }

    public void Login() => Account.Login(Name, "synthetic-password", Grid, StartLocationType.Home, "synthetic-OTP");
    public void Connect()
    {
        Connected(true);
        Progress(LoginStatus.Success, "");
    }
    public void Fail(string reason) => Progress(LoginStatus.Failed, reason);
    public void Disconnect(NetworkManager.DisconnectType reason = NetworkManager.DisconnectType.NetworkTimeout)
    {
        Connected(false);
        Raise("OnClientDisconnected", new DisconnectedEventArgs(reason, "Synthetic disconnect"));
    }
    public void LoggedOut() { Connected(false); Raise("OnClientLoggedOut", EventArgs.Empty); }
    public void DrainPosts() { while (_posts.TryDequeue(out var action)) action(); }
    private void Progress(LoginStatus status, string reason) => Raise("OnClientLoginStatus", new LoginProgressEventArgs(status, "Synthetic login result", reason));
    private void Raise(string method, object args) => typeof(NetCom).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Account.Net, new[] { args });
    private void Connected(bool value) => typeof(NetCom).GetProperty(nameof(NetCom.IsLoggedIn))!.SetValue(Account.Net, value);
    public void Dispose()
    {
        Connected(false);
        Account.Dispose();
        Fixture.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

/// <summary>Runs one-shot reconnect timers deterministically without sleeping.</summary>
internal sealed class ReconnectClock : TimeProvider
{
    private TimeSpan _elapsed;
    private readonly List<Timer> _timers = new();
    public int PendingTimers => _timers.Count(timer => timer.Due != null);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(int seconds)
    {
        _elapsed += TimeSpan.FromSeconds(seconds);
        foreach (var timer in _timers.ToArray().Where(timer => timer.Due <= _elapsed)) timer.Fire();
    }
    private sealed class Timer(ReconnectClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? Due { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime; return true; }
        public void Fire() { Due = null; callback(state); }
        public void Dispose() => Due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
