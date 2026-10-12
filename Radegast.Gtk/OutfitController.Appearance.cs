using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Radegast.Gtk;

internal sealed partial class OutfitController
{
    private CancellationTokenSource? _bake;
    private bool _bakeDirty;
    private int _lastBakedVersion = -1;

    private void CancelBakeLocked()
    {
        // The running task owns disposal; cancellation must remain usable until
        // all HTTP callbacks and semaphore waiters have unwound.
        _bake?.Cancel();
        _bake = null;
    }

    private void ScheduleBakeLocked()
    {
        if (_disposed || _batchDepth != 0 || !_ready || !_bakeDirty || _bake != null || _clientSideBaking) return;
        var sim = _client.Network.CurrentSim;
        if (sim == null || (sim.Protocols & RegionProtocols.AgentAppearanceService) == 0 && sim.Caps?.CapabilityURI("UpdateAvatarAppearance") == null) return;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _bake = cancel;
        _ = RunBakeAsync(sim, cancel);
    }

    private async Task RunBakeAsync(Simulator sim, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token).ConfigureAwait(false);
            await BakeAsync(sim, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn($"Appearance update failed: {ex.Message}", _client); }
        finally
        {
            lock (_state) { if (ReferenceEquals(_bake, cancel)) _bake = null; }
            cancel.Dispose();
        }
    }

    internal async Task BakeAsync(Simulator sim, CancellationToken token)
    {
        using var session = SessionToken(token);
        token = session.Token;
        await _inventory.WaitForCapabilityAsync("UpdateAvatarAppearance", token).ConfigureAwait(false);
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (sim != _client.Network.CurrentSim) return;
                var cap = sim.Caps?.CapabilityURI("UpdateAvatarAppearance") ?? throw new InvalidOperationException("The appearance service is unavailable.");
                var version = ((InventoryFolder)_store![_cof!.UUID]).Version;
                if (version < 0)
                {
                    await FetchCofAsync(token, true).ConfigureAwait(false);
                    version = _cof.Version;
                }
                if (version < 0) throw new InvalidOperationException("The server has not supplied a Current Outfit version.");
                if (version == _lastBakedVersion) { lock (_state) _bakeDirty = false; return; }
                Logger.Info($"Requesting appearance for Current Outfit v{version}.", _client);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                OSDMap? result = null;
                try
                {
                    var (response, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml,
                        new OSDMap { ["cof_version"] = OSD.FromInteger(version) }, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    result = data == null ? null : OSDParser.Deserialize(data) as OSDMap;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                {
                    Logger.Warn($"Appearance request failed (attempt {attempt + 1}): {ex.Message}", _client);
                }
                token.ThrowIfCancellationRequested();
                if (sim != _client.Network.CurrentSim) return;
                if (result?["success"].AsBoolean() == true)
                {
                    // Rendering is not required to send this request or to finish
                    // login. The sim distributes the baked appearance to others.
                    _lastBakedVersion = result.ContainsKey("cof_version") ? result["cof_version"].AsInteger() : version;
                    _client.Avatars.RequestOwnAvatarTextures();
                    lock (_state) _bakeDirty = false;
                    Changed?.Invoke();
                    return;
                }
                if (result?.ContainsKey("expected") == true)
                {
                    Logger.Warn($"Appearance service expects COF v{result["expected"].AsInteger()}; refreshing the live outfit (sent v{version}).", _client);
                    _client.Avatars.RequestOwnAvatarTextures();
                    // Refresh both content and version. Never invent a version
                    // from the error response or restore a cached outfit.
                    await FetchCofAsync(token, true).ConfigureAwait(false);
                }
                else if (result?.ContainsKey("error") == true)
                    throw new InvalidOperationException(result["error"].AsString());
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("The appearance service did not confirm the updated outfit after five attempts.");
        }
        finally { _changes.Release(); }
    }
}
