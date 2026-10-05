using Gtk;
using LibreMetaverse;
using LibreMetaverse.Appearance;
using System.Text.RegularExpressions;
using Task = System.Threading.Tasks.Task;
using Timeout = GLib.Timeout;

namespace Radegast.Gtk;

/// <summary>Equipped objects for one account, with touch actions for scripted prims.</summary>
internal sealed class AttachmentsPanel : Box
{
    private readonly AccountSession _session;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.None };
    private readonly Label _status = new("Open this tab to load attachments.") { Xalign = 0 };
    private readonly HashSet<UUID> _requestedNames = new();
    private readonly HashSet<UUID> _requestedProperties = new();
    private Simulator? _simulator;
    private bool _active;
    private bool _refreshQueued;
    private bool _disposed;

    private GridClient Client => _session.Client;

    public AttachmentsPanel(AccountSession session) : base(Orientation.Vertical, 6)
    {
        _session = session;
        BorderWidth = 8;
        PackStart(new Label("Equipped objects") { Xalign = 0 }, false, false, 0);
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        scroll.Add(_list);
        PackStart(scroll, true, true, 0);
        PackStart(_status, false, false, 0);
    }

    public void StartLoading()
    {
        if (_disposed) return;
        if (!_active)
        {
            _active = true;
            Client.Objects.ObjectUpdate += OnObjectUpdate;
            Client.Objects.KillObject += OnKillObject;
            Client.Objects.ObjectProperties += OnObjectProperties;
            Client.Appearance.AppearanceSet += OnAppearanceChanged;
            Client.Inventory.ItemReceived += OnItemReceived;
            _session.StateChanged += OnSessionChanged;
            Timeout.Add(2000, () =>
            {
                if (_disposed) return false;
                if (_simulator != Client.Network.CurrentSim) ScheduleRefresh();
                return true;
            });
        }
        Refresh();
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        if (e.Simulator != Client.Network.CurrentSim) return;
        var prim = e.Prim;
        var selfId = Client.Self.LocalID;
        if (selfId == 0 ||
            (prim.ParentID != selfId &&
             !(prim.ParentID != 0 && e.Simulator.ObjectsPrimitives.TryGetValue(prim.ParentID, out var parent) &&
               parent.ParentID == selfId))) return;
        GtkDispatch.Post(ScheduleRefresh);
    }

    private void OnKillObject(object? sender, KillObjectEventArgs e)
    {
        if (e.Simulator == Client.Network.CurrentSim) GtkDispatch.Post(ScheduleRefresh);
    }

    private void OnObjectProperties(object? sender, ObjectPropertiesEventArgs e)
    {
        var objectId = e.Properties.ObjectID;
        GtkDispatch.Post(() =>
        {
            if (_requestedProperties.Contains(objectId)) ScheduleRefresh();
        });
    }

    private void OnAppearanceChanged(object? sender, AppearanceSetEventArgs e) =>
        GtkDispatch.Post(ScheduleRefresh);

    private void OnItemReceived(object? sender, ItemReceivedEventArgs e) =>
        GtkDispatch.Post(ScheduleRefresh);

    private void OnSessionChanged(AccountSession session) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_disposed || !_active || _refreshQueued) return;
        _refreshQueued = true;
        Timeout.Add(180, () =>
        {
            _refreshQueued = false;
            if (!_disposed) Refresh();
            return false;
        });
    }

    private void Refresh()
    {
        if (_disposed || !_active) return;
        var sim = Client.Network.CurrentSim;
        if (sim != _simulator)
        {
            _simulator = sim;
            _requestedNames.Clear();
            _requestedProperties.Clear();
        }

        foreach (Widget child in _list.Children) _list.Remove(child);
        if (!_session.IsConnected || sim == null)
        {
            _list.Add(new Label("Connect to see equipped objects.") { Xalign = 0, Margin = 8 });
            _status.Text = "Not connected";
            _list.ShowAll();
            return;
        }

        var selfId = Client.Self.LocalID;
        var prims = sim.ObjectsPrimitives.Values.ToArray();
        var children = prims.ToLookup(prim => prim.ParentID);
        var worn = Client.Appearance.GetAttachmentsByItemId();
        var seen = new HashSet<UUID>();
        var entries = new List<AttachmentEntry>();

        foreach (var root in prims.Where(prim => selfId != 0 && prim.ParentID == selfId && prim.IsAttachment))
        {
            UUID itemId;
            try { itemId = CurrentOutfitFolder.GetAttachmentItemID(root); }
            catch { itemId = UUID.Zero; }
            if (itemId != UUID.Zero) seen.Add(itemId);

            var point = root.PrimData.AttachmentPoint;
            if (point == AttachmentPoint.Default && worn.TryGetValue(itemId, out var wornPoint))
                point = wornPoint;
            var name = ItemName(itemId);
            if (name == null)
            {
                name = root.Properties?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"Attachment {root.ID.ToString()[..8]}";
                    RequestName(sim, root, itemId);
                }
            }

            var touchPart = TouchablePart(root, children[root.LocalID]);
            entries.Add(new AttachmentEntry(root.ID, name, point,
                root.ID != UUID.Zero && touchPart != null));
        }

        // Appearance data can arrive before the simulator's attachment objects.
        foreach (var (itemId, point) in worn)
        {
            if (!seen.Add(itemId)) continue;
            var name = ItemName(itemId);
            if (name == null)
            {
                name = $"Attachment {itemId.ToString()[..8]}";
                RequestName(sim, null, itemId);
            }
            entries.Add(new AttachmentEntry(UUID.Zero, name, point, false));
        }

        foreach (var entry in entries.OrderBy(entry => PointLabel(entry.Point))
                     .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new Box(Orientation.Horizontal, 10) { Margin = 4 };
            var touch = new Button("Touch")
            {
                Sensitive = entry.Touchable,
                TooltipText = entry.Touchable ? $"Touch {entry.Name}" :
                    entry.ObjectId == UUID.Zero ? "Waiting for attachment object data" :
                    "This attachment is not touchable"
            };
            touch.Clicked += (_, _) => Touch(entry);
            row.PackStart(touch, false, false, 0);
            var text = new Box(Orientation.Vertical, 1);
            text.PackStart(new Label(entry.Name) { Xalign = 0 }, false, false, 0);
            text.PackStart(new Label(PointLabel(entry.Point)) { Xalign = 0 }, false, false, 0);
            row.PackStart(text, true, true, 0);
            _list.Add(row);
        }

        if (entries.Count == 0)
            _list.Add(new Label("No equipped objects found.") { Xalign = 0, Margin = 8 });
        _status.Text = $"{entries.Count} equipped object{(entries.Count == 1 ? "" : "s")}";
        _list.ShowAll();
    }

    private string? ItemName(UUID itemId)
    {
        if (itemId == UUID.Zero || Client.Inventory.Store == null ||
            !Client.Inventory.Store.TryGetValue(itemId, out InventoryBase? item)) return null;
        return string.IsNullOrWhiteSpace(item.Name) ? null : item.Name;
    }

    private void RequestName(Simulator sim, Primitive? root, UUID itemId)
    {
        if (itemId != UUID.Zero && _requestedNames.Add(itemId))
            _ = Task.Run(async () =>
            {
                try { await Client.Inventory.FetchItemHttpAsync(itemId, Client.Self.AgentID); }
                catch { /* The object name can still arrive from ObjectProperties. */ }
                GtkDispatch.Post(ScheduleRefresh);
            });
        if (root != null && _requestedProperties.Add(root.ID))
            Client.Objects.SelectObject(sim, root.LocalID);
    }

    private static Primitive? TouchablePart(Primitive root, IEnumerable<Primitive> children)
    {
        if ((root.Flags & PrimFlags.Touch) != 0) return root;
        return children.FirstOrDefault(child => (child.Flags & PrimFlags.Touch) != 0);
    }

    private void Touch(AttachmentEntry entry)
    {
        var sim = Client.Network.CurrentSim;
        if (!_session.IsConnected || sim == null || entry.ObjectId == UUID.Zero) return;
        var selfId = Client.Self.LocalID;
        var root = sim.ObjectsPrimitives.Values.FirstOrDefault(prim =>
            prim.ID == entry.ObjectId && prim.IsAttachment && prim.ParentID == selfId);
        if (root == null) { ScheduleRefresh(); return; }
        var part = TouchablePart(root, sim.ObjectsPrimitives.Values.Where(prim => prim.ParentID == root.LocalID));
        if (part == null) { ScheduleRefresh(); return; }

        _ = Task.Run(async () =>
        {
            try
            {
                await Client.Objects.ClickObjectAsync(sim, part.LocalID);
                GtkDispatch.Post(() => { if (!_disposed) _status.Text = $"Touch sent to {entry.Name}."; });
            }
            catch (Exception ex)
            {
                GtkDispatch.Post(() => { if (!_disposed) _status.Text = $"Touch failed: {ex.Message}"; });
            }
        });
    }

    private static string PointLabel(AttachmentPoint point) => point == AttachmentPoint.Default
        ? "Attachment point unknown"
        : Regex.Replace(point.ToString(), @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_active) return;
        Client.Objects.ObjectUpdate -= OnObjectUpdate;
        Client.Objects.KillObject -= OnKillObject;
        Client.Objects.ObjectProperties -= OnObjectProperties;
        Client.Appearance.AppearanceSet -= OnAppearanceChanged;
        Client.Inventory.ItemReceived -= OnItemReceived;
        _session.StateChanged -= OnSessionChanged;
    }

    private sealed record AttachmentEntry(UUID ObjectId, string Name, AttachmentPoint Point, bool Touchable);
}
