using LibreMetaverse;
using Radegast.Gtk;

internal static class FriendPresenceChecks
{
    public static Task StartupAndChanges()
    {
        var clock = new Clock();
        var presence = new FriendPresenceTracker(clock);
        var initiallyOnline = UUID.Random();
        var secondPage = UUID.Random();
        var initiallyOffline = UUID.Random();
        presence.SetRoster(new[] { (initiallyOnline, false), (secondPage, false), (initiallyOffline, false) });
        // A slow simulator connection must not consume the settling period.
        clock.Advance(TimeSpan.FromMinutes(1));
        presence.Connected();
        Check(!presence.Update(initiallyOnline, true, true), "The first online reply after login generated a notification");
        clock.Advance(TimeSpan.FromSeconds(3));
        Check(!presence.Update(secondPage, true, true), "A later page of initial online friends generated a notification");
        Check(!presence.Update(initiallyOnline, true, true), "A repeated initial online status generated a notification");
        Check(presence.Update(initiallyOnline, false, true) && !presence.Update(initiallyOnline, false, true) &&
            presence.Update(initiallyOnline, true, true), "Real changes to an observed friend were suppressed or duplicated");
        clock.Advance(FriendPresenceTracker.LoginSettlingTime);
        Check(presence.Update(initiallyOffline, true, true), "An initially offline friend's later login was suppressed");
        Check(presence.Update(initiallyOffline, false, true), "A friend's later logout was suppressed");
        return Task.CompletedTask;
    }

    public static Task OrderingAndReset()
    {
        var clock = new Clock();
        var presence = new FriendPresenceTracker(clock);
        var other = new FriendPresenceTracker(clock);
        var friend = UUID.Random();
        // Presence can reach GTK before the roster callback.
        Check(!presence.Update(friend, true, true), "A presence update before the roster generated a notification");
        presence.SetRoster(new[] { (friend, false) });
        presence.Connected();
        Check(!presence.Update(friend, true, true) && presence.Update(friend, false, true),
            "The roster callback overwrote an earlier real presence update");
        other.SetRoster(new[] { (friend, false) });
        other.Connected();
        Check(!other.Update(friend, true, true), "Accounts shared their presence baselines");
        presence.Reset();
        Check(!presence.Update(friend, true, false), "A disconnected account generated a notification");
        presence.Reset();
        presence.SetRoster(new[] { (friend, false) });
        presence.Connected();
        Check(!presence.Update(friend, true, true) && presence.Update(friend, false, true),
            "A new login reused the old baseline or suppressed subsequent changes");
        return Task.CompletedTask;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
