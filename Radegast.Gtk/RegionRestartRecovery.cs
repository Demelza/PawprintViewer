using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed record RestartLocation(GridRegion Region, Vector3 Position);

/// <summary>One account's temporary relocation. State and timer callbacks run on the UI thread.</summary>
internal sealed class RegionRestartRecovery : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private readonly Func<RestartLocation?> _location;
    private readonly Func<string, CancellationToken, Task<GridRegion>> _findRegion;
    private readonly Func<GridRegion, Vector3, CancellationToken, Task> _teleport;
    private AccountSettings _settings = new();
    private Trip? _trip;
    private bool _disposed;

    public string Status { get; private set; } = "Restart protection is off.";
    public event Action? Changed;
    public event Action<RestartLocation>? Returned;

    public RegionRestartRecovery(TimeProvider clock, Action<Action> post, Func<RestartLocation?> location,
        Func<string, CancellationToken, Task<GridRegion>> findRegion,
        Func<GridRegion, Vector3, CancellationToken, Task> teleport)
    {
        _clock = clock; _post = post; _location = location; _findRegion = findRegion; _teleport = teleport;
    }

    public void UpdateSettings(AccountSettings settings)
    {
        if (_disposed) return;
        var previous = _settings;
        _settings = settings;
        if (!settings.TeleportOnRegionRestart) Cancel("Restart protection is off.");
        else if (_trip is { Phase: Phase.Waiting } trip && previous.ReturnDelayMinutes != settings.ReturnDelayMinutes)
            ScheduleReturn(trip, TimeSpan.FromMinutes(settings.ReturnDelayMinutes));
        else if (_trip == null) SetStatus(string.IsNullOrWhiteSpace(settings.RestartDestinationRegion)
            ? "Enter a temporary destination region." : "Waiting for a region restart warning.");
    }

    public void ObserveRestart(ulong regionHandle, TimeSpan remaining)
    {
        if (_disposed || !_settings.TeleportOnRegionRestart ||
            _location() is not { } origin || origin.Region.RegionHandle != regionHandle) return;
        if (_trip is { } active && (active.Phase != Phase.Scheduled || active.Origin.Region.RegionHandle != regionHandle)) return;
        if (string.IsNullOrWhiteSpace(_settings.RestartDestinationRegion))
        {
            Cancel("Cannot leave: enter a temporary destination region.");
            return;
        }
        var trip = _trip ?? new Trip(origin);
        _trip = trip;
        // Each server warning refines the countdown, including a postponed restart.
        trip.DepartureReportedAt = _clock.GetTimestamp();
        trip.DepartureDelay = remaining - TimeSpan.FromSeconds(60);
        ScheduleDeparture(trip);
    }

    private void ScheduleDeparture(Trip trip)
    {
        trip.Timer?.Dispose(); trip.Timer = null;
        var generation = ++trip.TimerGeneration;
        trip.Phase = Phase.Scheduled;
        var delay = trip.DepartureDelay - _clock.GetElapsedTime(trip.DepartureReportedAt);
        if (delay <= TimeSpan.Zero) { BeginDeparture(trip, generation); return; }
        // Bound unusually long server countdowns to the native timer's supported range.
        // The callback checks the remaining time again before permitting departure.
        var due = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(Math.Min(delay.TotalMilliseconds, TimeSpan.FromDays(1).TotalMilliseconds))));
        trip.Timer = _clock.CreateTimer(_ => _post(() => BeginDeparture(trip, generation)), null,
            due, Timeout.InfiniteTimeSpan);
        SetStatus("Restart detected. Waiting until 60 seconds remain before teleporting.");
    }

    private void BeginDeparture(Trip trip, int generation)
    {
        if (!IsCurrent(trip) || generation != trip.TimerGeneration || trip.Phase != Phase.Scheduled) return;
        LocationChanged();
        if (!IsCurrent(trip)) return;
        if (trip.DepartureDelay > _clock.GetElapsedTime(trip.DepartureReportedAt))
        {
            ScheduleDeparture(trip);
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.RestartDestinationRegion))
        {
            Cancel("Cannot leave: enter a temporary destination region.");
            return;
        }
        trip.Timer?.Dispose(); trip.Timer = null;
        // Capture the position when leaving, so walking during the countdown does
        // not send the avatar back to where the first warning was received.
        if (_location() is not { } origin) { Disconnected(); return; }
        trip.Origin = origin;
        trip.Phase = Phase.Leaving;
        SetStatus("Restart detected. Teleporting to the temporary destination…");
        _ = LeaveAsync(trip, _settings);
    }

    private async Task LeaveAsync(Trip trip, AccountSettings settings)
    {
        GridRegion? destination = null;
        Exception? error = null;
        try
        {
            destination = await _findRegion(settings.RestartDestinationRegion.Trim(), trip.Token).ConfigureAwait(false);
            trip.Token.ThrowIfCancellationRequested();
            if (destination.Value.RegionHandle == trip.Origin.Region.RegionHandle)
                throw new InvalidOperationException("The temporary destination must be in another region.");
            if (_location()?.Region.RegionHandle != trip.Origin.Region.RegionHandle)
                throw new InvalidOperationException("The avatar has already left the restarting region.");
            await _teleport(destination.Value, new(settings.RestartDestinationX, settings.RestartDestinationY,
                settings.RestartDestinationZ), trip.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex; }
        _post(() =>
        {
            if (!IsCurrent(trip)) return;
            if (error != null) { Cancel($"Could not leave the region: {error.Message}"); return; }
            if (_location()?.Region.RegionHandle != destination!.Value.RegionHandle)
            {
                Cancel("The avatar did not arrive in the temporary region. Return cancelled.");
                return;
            }
            trip.TemporaryRegion = destination.Value.RegionHandle;
            trip.Phase = Phase.Waiting;
            ScheduleReturn(trip, TimeSpan.FromMinutes(_settings.ReturnDelayMinutes));
        });
    }

    private void ScheduleReturn(Trip trip, TimeSpan delay, string? failure = null)
    {
        trip.Timer?.Dispose();
        var generation = ++trip.TimerGeneration;
        trip.Phase = Phase.Waiting;
        trip.Timer = _clock.CreateTimer(_ => _post(() => BeginReturn(trip, generation)), null,
            delay, Timeout.InfiniteTimeSpan);
        SetStatus(failure == null ? $"At the temporary destination. Return in {delay.TotalMinutes:0} minute(s)."
            : $"Return failed: {failure} Retrying in 1 minute.");
    }

    private void BeginReturn(Trip trip, int generation)
    {
        if (!IsCurrent(trip) || generation != trip.TimerGeneration || trip.Phase != Phase.Waiting) return;
        LocationChanged();
        if (!IsCurrent(trip)) return;
        trip.Timer?.Dispose(); trip.Timer = null;
        trip.Phase = Phase.Returning;
        SetStatus("Teleporting back to the original location…");
        _ = ReturnAsync(trip);
    }

    private async Task ReturnAsync(Trip trip)
    {
        Exception? error = null;
        try { await _teleport(trip.Origin.Region, trip.Origin.Position, trip.Token).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        _post(() =>
        {
            if (!IsCurrent(trip)) return;
            var current = _location();
            if (current?.Region.RegionHandle == trip.Origin.Region.RegionHandle)
            {
                Cancel("Returned to the original region.");
                // Only a completed automatic return emits this event. A manual
                // return while waiting, or a stale teleport callback, does not.
                Returned?.Invoke(trip.Origin);
            }
            else if (current?.Region.RegionHandle != trip.TemporaryRegion)
                Cancel("The avatar left the temporary region. Return cancelled.");
            else ScheduleReturn(trip, TimeSpan.FromMinutes(1), error?.Message ?? "The original region was not reached.");
        });
    }

    public void LocationChanged()
    {
        if (_disposed || _trip is not { } trip) return;
        var current = _location();
        if (trip.Phase == Phase.Scheduled && current?.Region.RegionHandle != trip.Origin.Region.RegionHandle)
            Cancel("The avatar left the restarting region. Scheduled teleport cancelled.");
        else if (trip.Phase == Phase.Waiting)
        {
            if (current?.Region.RegionHandle == trip.Origin.Region.RegionHandle) Cancel("Returned to the original region.");
            else if (current?.Region.RegionHandle != trip.TemporaryRegion)
                Cancel("The avatar left the temporary region. Return cancelled.");
        }
    }

    public void Disconnected()
    {
        if (!_disposed && _trip != null) Cancel("Disconnected. Pending restart teleport cancelled.");
    }

    public void Cancel(string status)
    {
        var trip = _trip;
        _trip = null;
        trip?.Dispose();
        if (!_disposed) SetStatus(status);
    }

    private bool IsCurrent(Trip trip) => !_disposed && _trip == trip && !trip.Token.IsCancellationRequested;
    private void SetStatus(string status) { Status = status; Changed?.Invoke(); }
    public void Dispose() { _disposed = true; Cancel(""); }

    private enum Phase { Scheduled, Leaving, Waiting, Returning }
    private sealed class Trip : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        public RestartLocation Origin { get; set; }
        public CancellationToken Token { get; }
        public Trip(RestartLocation origin) { Origin = origin; Token = _cancel.Token; }
        public ulong TemporaryRegion;
        public Phase Phase;
        public int TimerGeneration;
        public long DepartureReportedAt;
        public TimeSpan DepartureDelay;
        public ITimer? Timer;
        public void Dispose()
        {
            Timer?.Dispose();
            _cancel.Cancel(); _cancel.Dispose();
        }
    }
}
