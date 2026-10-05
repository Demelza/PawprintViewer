using LibreMetaverse;
using LibreMetaverse.RLV;

namespace Radegast.Gtk;

internal sealed partial class RlvSession
{
    public Task SendReplyAsync(int channel, string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (channel != 0 && Enabled) _sendReply(channel, message);
        return Task.CompletedTask;
    }

    public Task SendInstantMessageAsync(Guid target, string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Enabled && _client.Network.Connected) _client.Self.InstantMessage(new UUID(target), message);
        return Task.CompletedTask;
    }

    public async Task AttachAsync(IReadOnlyList<AttachmentRequest> requests, CancellationToken token)
    {
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            if (await ItemAsync(new UUID(request.ItemId), token).ConfigureAwait(false) is not { } item) continue;
            try
            {
                await AddAsync(item, item is InventoryWearable or InventoryGesture ? null : (AttachmentPoint)request.AttachmentPoint,
                    request.ReplaceExistingAttachments, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) { Notify($"Could not add {item.Name}: {ex.Message}"); }
        }
    }

    public async Task DetachAsync(IReadOnlyList<Guid> items, CancellationToken token)
    {
        foreach (var id in items)
        {
            token.ThrowIfCancellationRequested();
            if (await ItemAsync(new UUID(id), token).ConfigureAwait(false) is { } item)
                await RemoveAsync(item, token).ConfigureAwait(false);
        }
    }

    public Task RemOutfitAsync(IReadOnlyList<Guid> items, CancellationToken token) => DetachAsync(items, token);

    private async Task<InventoryItem?> ItemAsync(UUID id, CancellationToken token)
    {
        if (_client.Inventory.Store != null && _client.Inventory.Store.TryGetValue(id, out InventoryBase? entry) &&
            entry is InventoryItem item) return Resolve(item);
        return await FetchItemAsync(id, token).ConfigureAwait(false);
    }

    public Task SetRotAsync(float angle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _client.Self.Movement.UpdateFromHeading(Math.PI / 2 - angle, true);
        return Task.CompletedTask;
    }

    public async Task SitAsync(Guid target, CancellationToken token)
    {
        var id = new UUID(target);
        await WaitForAsync<AvatarSitResponseEventArgs>(
            h => _client.Self.AvatarSitResponse += h, h => _client.Self.AvatarSitResponse -= h,
            () => _client.Self.RequestSit(id, Vector3.Zero), e => e.ObjectID == id, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _client.Self.Sit();
        await Service.ReportSitAsync(target, token).ConfigureAwait(false);
    }

    public async Task SitGroundAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _client.Self.SitOnGround();
        await Service.ReportSitAsync(null, token).ConfigureAwait(false);
    }

    public async Task UnsitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (_, target) = await TryGetSitIdAsync(token).ConfigureAwait(false);
        _client.Self.Stand();
        await Service.ReportUnsitAsync(target == Guid.Empty ? null : target, token).ConfigureAwait(false);
    }

    public async Task TpToAsync(float x, float y, float z, string? region, float? lookat, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var direction = lookat.HasValue
            ? Vector3.UnitX * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, lookat.Value) : Vector3.UnitY;
        bool success;
        if (string.IsNullOrEmpty(region))
        {
            var handle = Helpers.GlobalPosToRegionHandle(x, y, out var localX, out var localY);
            success = await _client.Self.TeleportAsync(handle, new Vector3(localX, localY, z), direction, token).ConfigureAwait(false);
        }
        else success = await _client.Self.TeleportAsync(region, new Vector3(x, y, z), direction, token).ConfigureAwait(false);
        if (!success) throw new InvalidOperationException(_client.Self.TeleportMessage);
        RaiseChanged();
    }

    public async Task SetGroupAsync(Guid groupId, string? role, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = new UUID(groupId);
        if (id == UUID.Zero) { _client.Groups.ActivateGroup(id); return; }
        var groups = await GroupsAsync(token).ConfigureAwait(false);
        if (!groups.ContainsKey(id)) throw new InvalidOperationException("This account is not a member of that group.");
        UUID? roleId = null;
        if (!string.IsNullOrEmpty(role))
        {
            var reply = await WaitForAsync<GroupRolesDataReplyEventArgs>(
                h => _client.Groups.GroupRoleDataReply += h, h => _client.Groups.GroupRoleDataReply -= h,
                () => _client.Groups.RequestGroupRoles(id), e => e.GroupID == id, token).ConfigureAwait(false);
            roleId = reply.Roles.Values.Where(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase))
                .Select(r => (UUID?)r.ID).FirstOrDefault();
            if (!roleId.HasValue) throw new InvalidOperationException("That group role could not be found.");
        }
        token.ThrowIfCancellationRequested();
        _client.Groups.ActivateGroup(id);
        if (roleId.HasValue) _client.Groups.ActivateTitle(id, roleId.Value);
    }

    public async Task SetGroupAsync(string name, string? role, CancellationToken token)
    {
        if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await SetGroupAsync(Guid.Empty, null, token).ConfigureAwait(false);
            return;
        }
        var groups = await GroupsAsync(token).ConfigureAwait(false);
        var group = groups.Values.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group.ID == UUID.Zero) throw new InvalidOperationException("That group could not be found.");
        await SetGroupAsync(group.ID.Guid, role, token).ConfigureAwait(false);
    }

    private async Task<Dictionary<UUID, Group>> GroupsAsync(CancellationToken token)
    {
        var reply = await WaitForAsync<CurrentGroupsEventArgs>(
            h => _client.Groups.CurrentGroups += h, h => _client.Groups.CurrentGroups -= h,
            _client.Groups.RequestCurrentGroups, _ => true, token).ConfigureAwait(false);
        return reply.Groups;
    }

    private static async Task<T> WaitForAsync<T>(Action<EventHandler<T>> subscribe, Action<EventHandler<T>> unsubscribe,
        Action request, Func<T, bool> matches, CancellationToken token) where T : EventArgs
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Reply(object? _, T args) { if (matches(args)) completion.TrySetResult(args); }
        subscribe(Reply);
        try
        {
            token.ThrowIfCancellationRequested();
            request();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        finally { unsubscribe(Reply); }
    }

    public Task<bool> ObjectExistsAsync(Guid id, CancellationToken token) => Task.FromResult(FindObject(new UUID(id)) != null);
    public Task<bool> IsSittingAsync(CancellationToken token) =>
        Task.FromResult(_client.Self.SittingOn != 0 || _client.Self.Movement.SitOnGround);
    public Task<(bool Success, Guid SitId)> TryGetSitIdAsync(CancellationToken token) =>
        Task.FromResult((true, _client.Network.CurrentSim != null &&
            _client.Network.CurrentSim.ObjectsPrimitives.TryGetValue(_client.Self.SittingOn, out var prim) ? prim.ID.Guid : Guid.Empty));

    public async Task<(bool Success, string ActiveGroupName)> TryGetActiveGroupNameAsync(CancellationToken token)
    {
        var id = _client.Self.ActiveGroup;
        if (id == UUID.Zero) return (true, "none");
        var reply = await WaitForAsync<GroupNamesEventArgs>(
            h => _client.Groups.GroupNamesReply += h, h => _client.Groups.GroupNamesReply -= h,
            () => _client.Groups.RequestGroupName(id), e => e.GroupNames.ContainsKey(id), token).ConfigureAwait(false);
        return (true, reply.GroupNames[id]);
    }

    public Task<(bool Success, CameraSettings? CameraSettings)> TryGetCameraSettingsAsync(CancellationToken token) =>
        Task.FromResult<(bool, CameraSettings?)>((false, null));
    public Task<(bool Success, string DebugSettingValue)> TryGetDebugSettingValueAsync(string name, CancellationToken token) => Task.FromResult((false, ""));
    public Task<(bool Success, string EnvironmentSettingValue)> TryGetEnvironmentSettingValueAsync(string name, CancellationToken token) => Task.FromResult((false, ""));
    public Task AdjustHeightAsync(float distance, float factor, float delta, CancellationToken token) => throw new NotSupportedException("Avatar height adjustment is unavailable.");
    public Task SetCamFOVAsync(float fov, CancellationToken token) => throw new NotSupportedException("There is no 3D camera.");
    public Task SetDebugAsync(string name, string value, CancellationToken token) => throw new NotSupportedException("Viewer debug settings are unavailable.");
    public Task SetEnvAsync(string name, string value, CancellationToken token) => throw new NotSupportedException("There is no rendered environment.");
}
