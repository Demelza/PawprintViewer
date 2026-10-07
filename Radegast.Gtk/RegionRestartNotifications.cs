namespace Radegast.Gtk;

/// <summary>One account's restart reminders. State and timer callbacks run on the UI thread.</summary>
internal sealed class RegionRestartNotifications(TimeProvider clock, Action<Action> post,
    Func<ulong?> currentRegion, Action<string> notify) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private Countdown? _countdown;
    private bool _disposed;

    public void ObserveRestart(ulong regionHandle, TimeSpan remaining)
    {
        if (_disposed || currentRegion() != regionHandle) return;
        if (_countdown?.RegionHandle != regionHandle) Cancel();
        if (remaining <= TimeSpan.Zero) { Cancel(); return; }
        var countdown = _countdown ??= new(regionHandle);
        countdown.ReportedAt = clock.GetTimestamp();
        countdown.Remaining = remaining;
        Pulse(countdown);
    }

    private void Pulse(Countdown countdown)
    {
        if (_disposed || _countdown != countdown) return;
        countdown.Timer?.Dispose(); countdown.Timer = null;
        var generation = ++countdown.Generation;
        var remaining = countdown.Remaining - clock.GetElapsedTime(countdown.ReportedAt);
        if (currentRegion() != countdown.RegionHandle || remaining <= TimeSpan.Zero) { Cancel(); return; }
        var due = countdown.LastNoticeAt is { } last ? Interval - clock.GetElapsedTime(last) : TimeSpan.Zero;
        if (due <= TimeSpan.Zero)
        {
            countdown.LastNoticeAt = clock.GetTimestamp();
            notify(FormatRemaining(remaining));
            if (_disposed || _countdown != countdown) return;
            due = Interval;
        }
        // Wake at expiry too, so the completed countdown retains no timer.
        due = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(Math.Min(due.TotalMilliseconds, remaining.TotalMilliseconds))));
        countdown.Timer = clock.CreateTimer(_ => post(() =>
        {
            if (_countdown == countdown && generation == countdown.Generation) Pulse(countdown);
        }), null, due, Timeout.InfiniteTimeSpan);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        var minutes = seconds / 60;
        seconds %= 60;
        var minuteText = $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        var secondText = $"{seconds} second{(seconds == 1 ? "" : "s")}";
        return (minutes == 0 ? secondText : seconds == 0 ? minuteText : minuteText + " " + secondText) + " remaining";
    }

    public void LocationChanged()
    {
        if (_countdown is { } countdown && currentRegion() != countdown.RegionHandle) Cancel();
    }

    public void Cancel()
    {
        _countdown?.Timer?.Dispose();
        _countdown = null;
    }

    public void Dispose() { _disposed = true; Cancel(); }

    private sealed class Countdown(ulong regionHandle)
    {
        public ulong RegionHandle { get; } = regionHandle;
        public TimeSpan Remaining;
        public long ReportedAt;
        public long? LastNoticeAt;
        public int Generation;
        public ITimer? Timer;
    }
}
