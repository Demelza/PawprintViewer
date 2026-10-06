using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Distinguishes login presence discovery from subsequent friend status changes.</summary>
internal sealed class FriendPresenceTracker(TimeProvider? clock = null)
{
    // The buddy-list has no online statuses, and OnlineNotification has no
    // initial-list flag or completion marker. Allow its startup replies to arrive.
    internal static readonly TimeSpan LoginSettlingTime = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<UUID, bool> _presence = new();
    private readonly HashSet<UUID> _observed = new();
    private long? _connectedAt;
    private bool _rosterReady;

    public void SetRoster(IEnumerable<(UUID Id, bool Online)> friends)
    {
        // Preserve any presence packets received before the roster callback.
        foreach (var (id, online) in friends) _presence.TryAdd(id, online);
        _rosterReady = true;
    }

    public void Connected() => _connectedAt = _clock.GetTimestamp();

    public bool Update(UUID id, bool online, bool connected)
    {
        var known = _presence.TryGetValue(id, out var previous);
        // Once a real status was observed for this friend, subsequent changes
        // are meaningful even during startup. Unobserved roster entries are
        // placeholders until the settling period ends.
        var settled = _observed.Contains(id) || (_connectedAt is { } start &&
            _clock.GetElapsedTime(start) >= LoginSettlingTime);
        _presence[id] = online;
        _observed.Add(id);
        return connected && _rosterReady && known && settled && previous != online;
    }

    public void Reset()
    {
        _presence.Clear();
        _observed.Clear();
        _connectedAt = null;
        _rosterReady = false;
    }
}
