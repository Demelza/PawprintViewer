using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using System.Globalization;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    private readonly RegionRestartRecovery _restartRecovery;
    public string RestartTeleportStatus => RedactText(_restartRecovery.Status);
    public event Action<AccountSession>? RestartTeleportChanged;

    private RestartLocation? CurrentRestartLocation() => !_disposed && IsConnected && Client.Network.CurrentSim is { } sim
        ? new(new GridRegion { Name = sim.Name, RegionHandle = sim.Handle, Access = sim.Access }, Client.Self.SimPosition)
        : null;

    private async Task<GridRegion> FindRestartDestinationAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected) throw new InvalidOperationException("This account is disconnected.");
        if (Rlv.Enabled && (!Rlv.Service.Permissions.CanTpLoc() || (IsSitting && !Rlv.Service.Permissions.CanUnsit())))
            throw new InvalidOperationException("Teleporting is restricted by RLV.");
        if (Client.Network.CurrentSim is { } sim && string.Equals(sim.Name, name, StringComparison.OrdinalIgnoreCase))
            return new GridRegion { Name = sim.Name, RegionHandle = sim.Handle, Access = sim.Access };
        var region = await Client.Grid.GetGridRegionAsync(name, GridLayerType.Objects, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (region is not { } found || found.Access == SimAccess.NonExistent ||
            !string.Equals(found.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The temporary destination region was not found.");
        return found;
    }

    private void OnRegionRestartChanged() => RestartTeleportChanged?.Invoke(this);
    private void OnRestartTeleportProgress(object? sender, TeleportEventArgs e) =>
        Interlocked.Exchange(ref _serverTeleportBusyUntil, e.Status is TeleportStatus.Start or TeleportStatus.Progress
            ? _clock.GetUtcNow().AddMilliseconds(Client.Settings.Timing.TeleportTimeout).UtcTicks : 0);
    private void OnRestartSimChanged(object? sender, SimChangedEventArgs e) =>
        _post(() => { if (!_disposed) _restartRecovery.LocationChanged(); });

    private void OnRegionRestartAlert(object? sender, PacketReceivedEventArgs e)
    {
        var sim = e.Simulator;
        if (sim != Client.Network.CurrentSim || e.Packet is not AlertMessagePacket alert ||
            !TryGetRegionRestartCountdown(alert, sim.Name, out var remaining)) return;
        var reportedAt = _clock.GetTimestamp();
        _post(() =>
        {
            if (!_disposed && sim == Client.Network.CurrentSim)
                _restartRecovery.ObserveRestart(sim.Handle, remaining - _clock.GetElapsedTime(reportedAt));
        });
    }

    internal static bool TryGetRegionRestartCountdown(AlertMessagePacket alert, string regionName, out TimeSpan remainingTime)
    {
        remainingTime = TimeSpan.Zero;
        // The structured simulator alert is authoritative; object chat and other
        // messages containing the word "restart" cannot initiate a teleport.
        if (alert.AlertInfo == null) return false;
        foreach (var info in alert.AlertInfo)
        {
            if (info?.Message == null || info.ExtraParams == null) continue;
            var id = Utils.BytesToString(info.Message);
            var units = id switch { "RegionRestartMinutes" => "MINUTES", "RegionRestartSeconds" => "SECONDS", _ => null };
            if (units == null) continue;
            try
            {
                if (OSDParser.Deserialize(info.ExtraParams) is OSDMap data &&
                    data.TryGetValue("NAME", out var name) && string.Equals(name.AsString(), regionName, StringComparison.OrdinalIgnoreCase) &&
                    data.TryGetValue(units, out var remaining) &&
                    int.TryParse(remaining.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
                {
                    remainingTime = TimeSpan.FromSeconds((long)count * (units == "MINUTES" ? 60 : 1));
                    return true;
                }
            }
            catch { /* A malformed alert is not a restart warning. */ }
        }
        return false;
    }
}
