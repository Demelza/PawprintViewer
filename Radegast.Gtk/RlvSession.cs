using LibreMetaverse;
using LibreMetaverse.Appearance;
using LibreMetaverse.RLV;
using LibreMetaverse.RLV.EventArguments;
using System.Threading.Channels;

namespace Radegast.Gtk;

/// <summary>One account's RLV engine. Commands run in arrival order outside the GTK thread.</summary>
internal sealed partial class RlvSession : IDisposable, IRlvActionCallbacks, IRlvQueryCallbacks, ICurrentOutfitPolicy
{
    private readonly GridClient _client;
    private readonly CurrentOutfitFolder _outfit;
    private readonly Action<Action> _post;
    private readonly Action<int, string> _sendReply;
    private readonly Channel<Work> _commands = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private readonly List<CancellationTokenSource> _generations = new();
    private readonly Dictionary<Guid, DateTime> _missingSince = new();
    private readonly Dictionary<Guid, UUID> _issuerItems = new();
    private readonly System.Threading.Timer _cleanup;
    private readonly Task _worker;
    private CancellationTokenSource _generation = new();
    private volatile RlvService _service;
    private volatile bool _disposed;

    public RlvService Service => _service;
    public bool Enabled => !_disposed && Service.Enabled;
    public bool DebugCommands { get; set; }
    public string Status { get; private set; } = "Ready for commands from your objects.";
    public event Action? Changed;
    public event Action<string>? Message;

    public RlvSession(GridClient client, CurrentOutfitFolder outfit, Action<Action> post, Action<int, string>? sendReply = null)
    {
        _client = client;
        _outfit = outfit;
        _post = post;
        _sendReply = sendReply ?? ((channel, message) =>
        {
            if (client.Network.Connected) client.Self.Chat(message, channel, ChatType.Normal);
        });
        _generations.Add(_generation);
        _service = CreateService(true);
        _outfit.AddPolicy(this);
        _worker = Task.Run(ProcessQueueAsync);
        _cleanup = new System.Threading.Timer(_ => Observe(QueueAsync(CleanupAsync)), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private RlvService CreateService(bool enabled)
    {
        var service = new RlvService(this, this, enabled);
        // Advertise unavailable visual and pending UI features through @getblacklist.
        foreach (var behavior in new[]
        {
            "adjustheight", "setcam", "setcam_fov", "getcam", "camdrawmin", "camdrawmax",
            "setcam_fovmin", "setcam_fovmax", "camdistmax", "camdistmin", "camdrawalphamin",
            "camdrawalphamax", "setcam_avdistmax", "setcam_avdistmin", "camdrawcolor", "camunlock",
            "setcam_unlock", "camavdist", "camtextures", "setcam_textures", "setenv", "getenv",
            "setdebug", "getdebug", "showminimap", "shownametags",
            "showhovertext", "showhovertextall", "showhovertexthud", "showhovertextworld",
            "edit", "editobj", "editworld", "editattach", "rez", "viewnote", "viewscript", "viewtexture",
            "share", "share_sec", "accepttprequest", "tprequest", "tprequest_sec"
        }) service.Blacklist.BlacklistBehavior(behavior);
        service.Restrictions.RestrictionUpdated += OnRestrictionUpdated;
        return service;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_stateLock)
        {
            if (_disposed || Enabled == enabled) return;
            _generation.Cancel();
            _service.Enabled = false;
            _service.Restrictions.RestrictionUpdated -= OnRestrictionUpdated;
            _generation = new CancellationTokenSource();
            _generations.Add(_generation);
            _service = CreateService(enabled);
            _missingSince.Clear();
            _issuerItems.Clear();
        }
        SetStatus(enabled ? "Ready for commands; objects must resend their restrictions." : "Disabled. Restrictions cleared.");
    }

    public bool TryHandleChat(ChatEventArgs chat)
    {
        if (!Enabled || chat.SourceType != ChatSourceType.Object || chat.Type != ChatType.OwnerSay ||
            chat.OwnerID != _client.Self.AgentID || chat.SourceID == UUID.Zero ||
            !chat.Message.StartsWith('@')) return false;
        var root = FindRoot(chat.SourceID);
        var source = root?.ID ?? chat.SourceID;
        lock (_stateLock)
            _issuerItems[source.Guid] = root != null && root.IsAttachment ? AttachmentItemId(root) : UUID.Zero;
        Observe(ProcessCommandAsync(chat.Message, source.Guid, chat.FromName));
        return true;
    }

    internal Task ProcessCommandAsync(string message, Guid source, string name) => QueueAsync(async (service, token) =>
    {
        if (DebugCommands) Notify($"{name}: {message}");
        var processed = true;
        string? failure = null;
        foreach (var command in message.TrimStart('@').Split(','))
        {
            token.ThrowIfCancellationRequested();
            var behavior = command.Split(':', '=')[0].ToLowerInvariant();
            var previousRequest = _inventoryRequest.Value;
            _inventoryRequest.Value = InventoryRequestFor(command, behavior);
            if (behavior.StartsWith("setenv_", StringComparison.Ordinal) || behavior.StartsWith("getenv_", StringComparison.Ordinal) ||
                behavior.StartsWith("setdebug_", StringComparison.Ordinal) || behavior.StartsWith("getdebug_", StringComparison.Ordinal))
                service.Blacklist.BlacklistBehavior(behavior);
            try
            {
                processed &= await service.ProcessMessageAsync("@" + command, source, name, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                processed = false;
                failure = ex.Message;
                Notify($"{name}: {behavior} failed: {ex.Message}");
            }
            finally { _inventoryRequest.Value = previousRequest; }
        }
        token.ThrowIfCancellationRequested();
        SetStatus(processed ? "Command processed." : failure ?? "Some commands were unavailable or could not be applied.");
        if (DebugCommands) Notify($"{name}: {message} ({(processed ? "ok" : "unavailable")})");
    });

    private Task QueueAsync(Func<RlvService, CancellationToken, Task> action)
    {
        lock (_stateLock)
        {
            if (_disposed) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commands.Writer.TryWrite(new Work(action, _service, _generation.Token, completion)))
                completion.TrySetCanceled();
            return completion.Task;
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var work in _commands.Reader.ReadAllAsync())
        {
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, work.Generation);
            try
            {
                token.Token.ThrowIfCancellationRequested();
                if (work.Service.Enabled) await work.Action(work.Service, token.Token).ConfigureAwait(false);
                work.Completion.TrySetResult();
            }
            catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
            catch (Exception ex)
            {
                SetStatus($"Command failed: {ex.Message}");
                Notify($"Command failed: {ex.Message}");
                work.Completion.TrySetException(ex);
            }
        }
    }

    private static async void Observe(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { /* The queue reports failures; cancelled work needs no notification. */ }
    }

    private void OnRestrictionUpdated(object? sender, RestrictionUpdatedEventArgs args)
    {
        if (DebugCommands) Notify($"{(args.IsDeleted ? "Removed" : "Added")}: {args.Restriction}");
        RaiseChanged();
    }

    private void RaiseChanged() => _post(() => { if (!_disposed) Changed?.Invoke(); });
    private void SetStatus(string status)
    {
        _post(() =>
        {
            if (_disposed) return;
            Status = status;
            Changed?.Invoke();
        });
    }
    private void Notify(string text) => _post(() => { if (!_disposed) Message?.Invoke($"[RLV] {text}"); });

    private async Task CleanupAsync(RlvService service, CancellationToken token)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        foreach (var id in service.Restrictions.GetTrackedPrimIds())
        {
            token.ThrowIfCancellationRequested();
            var exists = FindObject(new UUID(id)) != null;
            lock (_stateLock)
            {
                if (exists) { _missingSince.Remove(id); continue; }
                if (!_missingSince.TryGetValue(id, out var since))
                {
                    _missingSince[id] = DateTime.UtcNow;
                    continue;
                }
                if (DateTime.UtcNow - since < TimeSpan.FromMinutes(2)) continue;
                // Preserve locks while attachments are still loading after a region change.
                if (_issuerItems.TryGetValue(id, out var itemId) && itemId != UUID.Zero &&
                    _client.Appearance.GetAttachmentsByItemId().ContainsKey(itemId) &&
                    !Objects().Any(p => p.IsAttachment && AttachmentItemId(p) == itemId)) continue;
                _missingSince.Remove(id);
                _issuerItems.Remove(id);
            }
            await service.ProcessMessageAsync("@clear", id, "", token).ConfigureAwait(false);
        }
    }

    internal IEnumerable<Primitive> Objects()
    {
        Simulator[] simulators;
        lock (_client.Network.Simulators) simulators = _client.Network.Simulators.ToArray();
        return simulators.SelectMany(sim => sim.ObjectsPrimitives.Values.ToArray()).ToArray();
    }

    private Primitive? FindObject(UUID id) => Objects().FirstOrDefault(p => p.ID == id);

    private IEnumerable<Primitive> MyAttachments() =>
        (_client.Network.CurrentSim?.ObjectsPrimitives.Values.ToArray() ?? Array.Empty<Primitive>())
        .Where(p => p.IsAttachment && _client.Self.LocalID != 0 && p.ParentID == _client.Self.LocalID);

    private Primitive? FindRoot(UUID id)
    {
        Simulator[] simulators;
        lock (_client.Network.Simulators) simulators = _client.Network.Simulators.ToArray();
        foreach (var sim in simulators)
        {
            var prim = sim.ObjectsPrimitives.Values.FirstOrDefault(p => p.ID == id);
            if (prim == null) continue;
            var seen = new HashSet<uint>();
            while (prim.ParentID != _client.Self.LocalID && prim.ParentID != 0 && seen.Add(prim.LocalID) &&
                   sim.ObjectsPrimitives.TryGetValue(prim.ParentID, out var parent)) prim = parent;
            return prim;
        }
        return null;
    }

    internal static UUID AttachmentItemId(Primitive prim)
    {
        try { return CurrentOutfitFolder.GetAttachmentItemID(prim); }
        catch { return UUID.Zero; }
    }

    public bool CanTouch(UUID objectId, AttachmentPoint point) => !Enabled ||
        Service.Permissions.CanTouch(point.ToString().StartsWith("HUD", StringComparison.Ordinal)
            ? RlvPermissionsService.TouchLocation.Hud : RlvPermissionsService.TouchLocation.AttachedSelf,
            objectId.Guid, _client.Self.AgentID.Guid, 0);

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _service.Enabled = false;
            _service.Restrictions.RestrictionUpdated -= OnRestrictionUpdated;
            _cleanup.Dispose();
            _lifetime.Cancel();
            _generation.Cancel();
            _commands.Writer.TryComplete();
        }
        _outfit.RemovePolicy(this);
        _ = _worker.ContinueWith(_ =>
        {
            _lifetime.Dispose();
            foreach (var generation in _generations) generation.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed record Work(Func<RlvService, CancellationToken, Task> Action, RlvService Service,
        CancellationToken Generation, TaskCompletionSource Completion);
}
