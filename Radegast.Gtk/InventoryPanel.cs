using Gtk;
using LibreMetaverse;
using LibreMetaverse.Packets;
using System.Text.RegularExpressions;
using Task = System.Threading.Tasks.Task;
using Timeout = GLib.Timeout;

namespace Radegast.Gtk;

/// <summary>GTK inventory browser for one account. Folder requests start when the tab is opened.</summary>
internal sealed class InventoryPanel : Box
{
    private const int NameColumn = 0;
    private const int TypeColumn = 1;
    private const int IdColumn = 2;
    private const int LibraryColumn = 3;
    private static readonly HashSet<string> PinnedRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "#Firestorm", "#RLV", "Animations", "Body Parts", "Calling Cards", "Clothing",
        "Current Outfit", "Favorites", "Gestures", "Landmarks", "Lost And Found",
        "Materials", "Notecards", "Objects", "Outfits", "Photo Album", "Scripts",
        "Settings", "Sounds", "Textures", "Trash"
    };
    private static readonly AttachmentPoint[] BodyPoints =
    {
        AttachmentPoint.Chest, AttachmentPoint.Skull, AttachmentPoint.LeftShoulder,
        AttachmentPoint.RightShoulder, AttachmentPoint.LeftHand, AttachmentPoint.RightHand,
        AttachmentPoint.LeftFoot, AttachmentPoint.RightFoot, AttachmentPoint.Spine,
        AttachmentPoint.Pelvis, AttachmentPoint.Mouth, AttachmentPoint.Chin,
        AttachmentPoint.LeftEar, AttachmentPoint.RightEar, AttachmentPoint.LeftEyeball,
        AttachmentPoint.RightEyeball, AttachmentPoint.Nose, AttachmentPoint.RightUpperArm,
        AttachmentPoint.RightForearm, AttachmentPoint.LeftUpperArm, AttachmentPoint.LeftForearm,
        AttachmentPoint.RightHip, AttachmentPoint.RightUpperLeg, AttachmentPoint.RightLowerLeg,
        AttachmentPoint.LeftHip, AttachmentPoint.LeftUpperLeg, AttachmentPoint.LeftLowerLeg,
        AttachmentPoint.Stomach, AttachmentPoint.LeftPec, AttachmentPoint.RightPec,
        AttachmentPoint.Neck, AttachmentPoint.Root, AttachmentPoint.LeftHandRing,
        AttachmentPoint.RightHandRing, AttachmentPoint.TailBase, AttachmentPoint.TailTip,
        AttachmentPoint.LeftWing, AttachmentPoint.RightWing, AttachmentPoint.Jaw,
        AttachmentPoint.AltLeftEar, AttachmentPoint.AltRightEar, AttachmentPoint.AltLeftEye,
        AttachmentPoint.AltRightEye, AttachmentPoint.Tongue, AttachmentPoint.Groin,
        AttachmentPoint.LeftHindFoot, AttachmentPoint.RightHindFoot
    };
    private static readonly AttachmentPoint[] HudPoints =
    {
        AttachmentPoint.HUDCenter2, AttachmentPoint.HUDTopRight, AttachmentPoint.HUDTop,
        AttachmentPoint.HUDTopLeft, AttachmentPoint.HUDCenter, AttachmentPoint.HUDBottomLeft,
        AttachmentPoint.HUDBottom, AttachmentPoint.HUDBottomRight
    };

    private readonly AccountSession _session;
    private readonly TreeStore _treeModel = new(typeof(string), typeof(string), typeof(string), typeof(bool));
    private readonly TreeView _tree;
    private readonly TreeViewColumn _typeViewColumn;
    private readonly Stack _resultsStack = new();
    private readonly ListBox _searchResults = new();
    private readonly List<(Button Button, UUID Id, string Display)> _searchButtons = new();
    private readonly Entry _searchEntry = new() { PlaceholderText = "Search My Inventory…" };
    private readonly Label _status = new("Open a folder to load its contents.") { Xalign = 0 };
    private readonly Label _details = new("Select a folder or item.")
    {
        Xalign = 0,
        Yalign = 0,
        LineWrap = true,
        Selectable = true
    };
    private readonly Button _newFolder = new("New folder");
    private readonly Button _rename = new("Rename");
    private readonly Button _trash = new("Move to Trash");
    private readonly Button _add = new("Add");
    private readonly Button _detach = new("Detach");
    private readonly MenuButton _addTo = new() { Label = "Add To" };
    private readonly MenuButton _addToHud = new() { Label = "Add To HUD" };
    private readonly Button _emptyTrash = new("Empty Trash");
    private readonly HashSet<UUID> _expanded = new();
    private readonly HashSet<UUID> _fetched = new();
    private readonly HashSet<UUID> _fetching = new();
    private HashSet<UUID> _wornIds = new();
    private CancellationTokenSource? _searchCancel;
    private LibreMetaverse.Inventory? _subscribedStore;
    private UUID _selectedId = UUID.Zero;
    private bool _selectedIsLibrary;
    private bool _active;
    private bool _building;
    private bool _searching;
    private bool _refreshQueued;
    private bool _disposed;
    private int _storeGeneration;

    private GridClient Client => _session.Client;
    private LibreMetaverse.Inventory? Store => Client.Inventory.Store;

    public InventoryPanel(AccountSession session) : base(Orientation.Vertical, 6)
    {
        _session = session;
        _session.Rlv.Changed += OnRestrictionsChanged;
        _session.StateChanged += OnSessionChanged;
        BorderWidth = 8;

        var toolbar = new Box(Orientation.Horizontal, 6);
        var searchButton = new Button("Search all");
        var clearButton = new Button("Clear");
        var refreshButton = new Button("Refresh folder");
        _searchEntry.Activated += (_, _) => BeginSearch();
        searchButton.Clicked += (_, _) => BeginSearch();
        clearButton.Clicked += (_, _) => ClearSearch();
        refreshButton.Clicked += (_, _) => RefreshSelectedFolder();
        toolbar.PackStart(_searchEntry, true, true, 0);
        toolbar.PackStart(searchButton, false, false, 0);
        toolbar.PackStart(clearButton, false, false, 0);
        toolbar.PackStart(refreshButton, false, false, 0);
        PackStart(toolbar, false, false, 0);

        var split = new Box(Orientation.Horizontal, 8);
        PackStart(split, true, true, 0);

        _tree = new TreeView(_treeModel) { HeadersVisible = true };
        var nameViewColumn = _tree.AppendColumn("Name", new CellRendererText(), "text", NameColumn);
        nameViewColumn.Expand = true;
        _typeViewColumn = _tree.AppendColumn("Type", new CellRendererText(), "text", TypeColumn);
        _typeViewColumn.Sizing = TreeViewColumnSizing.Fixed;
        _tree.SizeAllocated += (_, e) =>
        {
            var width = Math.Max(1, (int)(e.Allocation.Width * 0.20));
            if (_typeViewColumn.FixedWidth != width) _typeViewColumn.FixedWidth = width;
        };
        _tree.RowExpanded += OnRowExpanded;
        _tree.RowCollapsed += OnRowCollapsed;
        _tree.Selection.Changed += (_, _) => OnTreeSelectionChanged();
        var treeScroll = new ScrolledWindow();
        treeScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        treeScroll.Add(_tree);
        _resultsStack.AddNamed(treeScroll, "tree");

        var resultScroll = new ScrolledWindow();
        resultScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        resultScroll.Add(_searchResults);
        _resultsStack.AddNamed(resultScroll, "search");
        _resultsStack.VisibleChildName = "tree";
        split.PackStart(_resultsStack, true, true, 0);

        var detailPane = new Box(Orientation.Vertical, 8);
        split.SizeAllocated += (_, e) =>
        {
            var width = Math.Max(1, (int)(e.Allocation.Width * 0.25));
            if (detailPane.WidthRequest != width) detailPane.WidthRequest = width;
        };
        detailPane.PackStart(new Label("Item details") { Xalign = 0 }, false, false, 0);
        var detailScroll = new ScrolledWindow();
        detailScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        detailScroll.Add(_details);
        detailPane.PackStart(detailScroll, true, true, 0);
        _newFolder.Clicked += (_, _) => CreateFolder();
        _rename.Clicked += (_, _) => RenameSelected();
        _trash.Clicked += (_, _) => MoveSelectedToTrash();
        _add.Clicked += (_, _) => ChangeOutfit(_selectedId, null, false);
        _detach.Clicked += (_, _) => ChangeOutfit(_selectedId, null, true);
        _emptyTrash.Clicked += (_, _) => EmptyTrash();
        _addTo.Popup = CreatePointMenu(BodyPoints);
        _addToHud.Popup = CreatePointMenu(HudPoints);
        detailPane.PackStart(_add, false, false, 0);
        detailPane.PackStart(_detach, false, false, 0);
        detailPane.PackStart(_addTo, false, false, 0);
        detailPane.PackStart(_addToHud, false, false, 0);
        detailPane.PackStart(_newFolder, false, false, 0);
        detailPane.PackStart(_rename, false, false, 0);
        detailPane.PackStart(_trash, false, false, 0);
        detailPane.PackStart(_emptyTrash, false, false, 0);
        split.PackStart(detailPane, false, false, 0);

        PackEnd(_status, false, false, 0);
        UpdateDetails();
    }

    public void StartLoading()
    {
        if (_active || _disposed) return;
        _active = true;
        Client.Inventory.FolderUpdated += OnFolderUpdated;
        Client.Inventory.ItemReceived += OnItemReceived;
        Client.Appearance.AppearanceSet += OnAppearanceChanged;
        Client.Appearance.AgentWearablesReply += OnWearablesChanged;

        // The inventory store is created by the login response, sometimes after the
        // session window. Retry briefly without blocking the GTK main loop.
        Timeout.Add(500, () =>
        {
            if (_disposed) return false;
            if (!_session.IsConnected || Store?.RootFolder == null) return true;
            LoadStore();
            return false;
        });
    }

    private void OnSessionChanged(AccountSession account)
    {
        if (_disposed || !_active) return;
        if (account.IsConnected) LoadStore();
        else { _storeGeneration++; _searchCancel?.Cancel(); }
        UpdateDetails();
    }

    private void LoadStore()
    {
        if (Store?.RootFolder == null || Store == _subscribedStore) return;
        DetachStoreEvents();
        _storeGeneration++;
        _searchCancel?.Cancel();
        _searching = false;
        _resultsStack.VisibleChildName = "tree";
        _expanded.Clear();
        _fetched.Clear();
        _fetching.Clear();
        _selectedId = UUID.Zero;
        _wornIds.Clear();
        AttachStoreEvents();
        _expanded.Add(Store.RootFolder.UUID);
        RebuildTree();
        FetchFolder(Store.RootFolder.UUID, false);
        var generation = _storeGeneration;
        _ = Task.Run(async () =>
        {
            try { await _session.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None); }
            catch { return; }
            GtkDispatch.Post(() => { if (!_disposed && generation == _storeGeneration) ScheduleRebuild(); });
        });
    }

    private void AttachStoreEvents()
    {
        if (Store == null || _subscribedStore == Store) return;
        _subscribedStore = Store;
        _subscribedStore.InventoryObjectAdded += OnObjectAdded;
        _subscribedStore.InventoryObjectRemoved += OnObjectRemoved;
        _subscribedStore.InventoryObjectUpdated += OnObjectUpdated;
    }

    private void OnFolderUpdated(object? sender, FolderUpdatedEventArgs e)
    {
        var folderId = e.FolderID;
        var success = e.Success;
        GtkDispatch.Post(() =>
        {
            if (_disposed) return;
            _fetching.Remove(folderId);
            if (success) _fetched.Add(folderId);
            else _status.Text = "A folder could not be loaded. Select it and retry with Refresh folder.";
            ScheduleRebuild();
        });
    }

    private void OnItemReceived(object? sender, ItemReceivedEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void OnAppearanceChanged(object? sender, AppearanceSetEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void OnWearablesChanged(object? sender, AgentWearablesReplyEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void OnObjectAdded(object? sender, InventoryObjectAddedEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void OnObjectRemoved(object? sender, InventoryObjectRemovedEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void OnObjectUpdated(object? sender, InventoryObjectUpdatedEventArgs e) =>
        GtkDispatch.Post(() => { if (!_disposed) ScheduleRebuild(); });

    private void ScheduleRebuild()
    {
        if (_searching) return;
        if (_refreshQueued) return;
        _refreshQueued = true;
        Timeout.Add(180, () =>
        {
            _refreshQueued = false;
            if (!_disposed) RebuildTree();
            return false;
        });
    }

    private void RebuildTree()
    {
        var inventory = Store;
        if (inventory?.RootFolder == null) return;
        RefreshWornState();
        _building = true;
        try
        {
            _treeModel.Clear();
            var pathsToExpand = new List<TreePath>();
            AddRow(inventory.RootFolder, null, false, pathsToExpand);
            if (inventory.LibraryFolder != null)
                AddRow(inventory.LibraryFolder, null, true, pathsToExpand);
            foreach (var path in pathsToExpand)
                _tree.ExpandRow(path, false);
            if (_selectedId == UUID.Zero)
                _selectedId = inventory.RootFolder.UUID;
            if (_selectedId != UUID.Zero && FindRow(_selectedId, out var selected))
                _tree.Selection.SelectIter(selected);
            UpdateDetails();
        }
        finally
        {
            _building = false;
        }
    }

    private void AddRow(InventoryBase item, TreeIter? parent, bool isLibrary, List<TreePath> expandedPaths)
    {
        var type = item is InventoryFolder ? "Folder" : (item as InventoryItem)?.InventoryType.ToString() ?? "Item";
        var label = item.Name ?? "(unnamed)";
        if (item is InventoryItem inventoryItem && IsWorn(inventoryItem)) label += " (worn)";
        var row = parent.HasValue
            ? _treeModel.AppendValues(parent.Value, label, type, item.UUID.ToString(), isLibrary)
            : _treeModel.AppendValues(label, type, item.UUID.ToString(), isLibrary);

        if (item is not InventoryFolder folder) return;

        if (!_expanded.Contains(folder.UUID))
        {
            // Keep an expansion arrow even before the server has returned contents.
            if (!_fetched.Contains(folder.UUID) || GetContents(folder.UUID).Count > 0)
                _treeModel.AppendValues(row, "", "", "", isLibrary);
            return;
        }

        expandedPaths.Add(_treeModel.GetPath(row));
        var children = GetContents(folder.UUID);
        var isMyInventoryRoot = !isLibrary && folder.UUID == Store?.RootFolder?.UUID;
        foreach (var child in children.OrderBy(c => isMyInventoryRoot && c is InventoryFolder rootFolder &&
                                                PinnedRootFolders.Contains(rootFolder.Name) ? 0 : 1)
                     .ThenBy(c => c is InventoryFolder ? 0 : 1)
                     .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            AddRow(child, row, isLibrary, expandedPaths);
        if (children.Count == 0 && !_fetched.Contains(folder.UUID))
            _treeModel.AppendValues(row, "Loading…", "", "", isLibrary);
    }

    private List<InventoryBase> GetContents(UUID folderId)
    {
        try { return Store?.GetContents(folderId).ToList() ?? new List<InventoryBase>(); }
        catch (Exception) { return new List<InventoryBase>(); }
    }

    private void OnRowExpanded(object? sender, RowExpandedArgs e)
    {
        if (_building) return;
        var id = RowId(e.Iter);
        if (id == UUID.Zero) return;
        var isLibrary = RowIsLibrary(e.Iter);
        _expanded.Add(id);
        ScheduleRebuild();
        FetchFolder(id, isLibrary);
    }

    private void OnRowCollapsed(object? sender, RowCollapsedArgs e)
    {
        if (_building) return;
        var id = RowId(e.Iter);
        if (id != UUID.Zero) _expanded.Remove(id);
    }

    private void FetchFolder(UUID folderId, bool isLibrary, bool force = false)
    {
        if (_disposed || !_session.IsConnected || Store == null) return;
        if (_fetching.Contains(folderId) || (!force && _fetched.Contains(folderId))) return;
        _fetching.Add(folderId);
        var generation = _storeGeneration;
        _status.Text = "Loading folder…";
        var owner = isLibrary ? GetLibraryOwner() : Client.Self.AgentID;
        if (owner == UUID.Zero)
        {
            _fetching.Remove(folderId);
            _status.Text = "The library owner is not available.";
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (isLibrary)
                    await Client.Inventory.FolderContentsAsync(folderId, owner, true, true, InventorySortOrder.ByName);
                else
                    await Client.Inventory.RequestFolderContentsAsync(folderId, owner, true, true, InventorySortOrder.ByName);
                GtkDispatch.Post(() =>
                {
                    if (_disposed || generation != _storeGeneration) return;
                    _fetching.Remove(folderId);
                    _fetched.Add(folderId);
                    _status.Text = "Inventory ready";
                    ScheduleRebuild();
                });
            }
            catch (Exception ex)
            {
                GtkDispatch.Post(() =>
                {
                    if (_disposed || generation != _storeGeneration) return;
                    _fetching.Remove(folderId);
                    _status.Text = $"Could not load folder: {ex.Message}";
                });
            }
        });
    }

    private UUID GetLibraryOwner()
    {
        var inventory = Store;
        if (inventory?.LibraryFolder == null) return UUID.Zero;
        return GetContents(inventory.LibraryFolder.UUID)
            .OfType<InventoryFolder>()
            .Select(f => f.OwnerID)
            .FirstOrDefault(id => id != UUID.Zero);
    }

    private bool FindRow(UUID id, out TreeIter found)
    {
        if (_treeModel.GetIterFirst(out var root))
        {
            do
            {
                if (FindRowBelow(root, id, out found)) return true;
            } while (_treeModel.IterNext(ref root));
        }
        found = default;
        return false;
    }

    private bool FindRowBelow(TreeIter row, UUID id, out TreeIter found)
    {
        if (RowId(row) == id) { found = row; return true; }
        if (_treeModel.IterChildren(out var child, row))
        {
            do
            {
                if (FindRowBelow(child, id, out found)) return true;
            } while (_treeModel.IterNext(ref child));
        }
        found = default;
        return false;
    }

    private UUID RowId(TreeIter row)
    {
        var value = _treeModel.GetValue(row, IdColumn) as string;
        return value != null && UUID.TryParse(value, out var id) ? id : UUID.Zero;
    }

    private bool RowIsLibrary(TreeIter row) => _treeModel.GetValue(row, LibraryColumn) is true;

    private void OnTreeSelectionChanged()
    {
        if (_building || !_tree.Selection.GetSelected(out var model, out var row)) return;
        _selectedId = RowId(row);
        _selectedIsLibrary = RowIsLibrary(row);
        RefreshWornState();
        UpdateDetails();
    }

    private Menu CreatePointMenu(AttachmentPoint[] points)
    {
        var menu = new Menu();
        var entries = new List<(MenuItem Entry, AttachmentPoint Point)>();
        foreach (var point in points)
        {
            var entry = new MenuItem(PointLabel(point));
            entry.Activated += (_, _) => ChangeOutfit(_selectedId, point, false);
            entries.Add((entry, point));
            menu.Append(entry);
        }
        menu.Shown += (_, _) =>
        {
            var item = Store != null && Store.TryGetValue(_selectedId, out InventoryBase? selected) ? selected as InventoryItem : null;
            var map = _session.Rlv.Enabled ? _session.Rlv.BuildInventoryMap() : null;
            foreach (var (entry, point) in entries) entry.Sensitive = item != null && _session.Rlv.CanAdd(item, point, map);
        };
        menu.ShowAll();
        return menu;
    }

    private static string PointLabel(AttachmentPoint point)
    {
        if (point == AttachmentPoint.Root) return "Avatar Center";
        var name = point.ToString();
        if (name.StartsWith("HUD", StringComparison.Ordinal)) name = "HUD " + name[3..];
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private InventoryItem? ResolveItem(InventoryItem item)
    {
        if (!item.IsLink()) return item;
        return Store != null && Store.TryGetValue(item.AssetUUID, out InventoryBase? target)
            ? target as InventoryItem : null;
    }

    private void RefreshWornState()
    {
        var worn = new HashSet<UUID>();
        try { foreach (var item in Client.Appearance.GetWearables()) worn.Add(item.ItemID); }
        catch { /* Appearance data may still be loading. */ }
        try { foreach (var id in Client.Appearance.GetAttachmentsByItemId().Keys) worn.Add(id); }
        catch { /* Attachment data may still be loading. */ }

        // COF links fill gaps when wearable data arrives before its inventory item.
        var cof = _session.Outfit.COF;
        if (cof != null && Store != null)
        {
            foreach (var link in GetContents(cof.UUID).OfType<InventoryItem>())
                if (link.IsLink() && Store.TryGetValue(link.AssetUUID, out InventoryBase? target) &&
                    target is InventoryWearable)
                    worn.Add(link.AssetUUID);
        }
        _wornIds = worn;
        foreach (var (button, id, display) in _searchButtons)
            button.Label = display + (IsWornId(id) ? " (worn)" : string.Empty);
    }

    private bool IsWorn(InventoryItem item) => _wornIds.Contains(item.IsLink() ? item.AssetUUID : item.UUID);

    private bool IsWornId(UUID id) => Store != null &&
        Store.TryGetValue(id, out InventoryBase? item) &&
        item is InventoryItem inventoryItem && IsWorn(inventoryItem);

    private void UpdateDetails()
    {
        if (_selectedId == UUID.Zero || Store == null || !Store.TryGetValue(_selectedId, out InventoryBase? item))
        {
            _details.Text = "Select a folder or item.";
            _newFolder.Sensitive = false;
            _rename.Sensitive = false;
            _trash.Sensitive = false;
            _add.Sensitive = false;
            _detach.Sensitive = false;
            _addTo.Sensitive = false;
            _addToHud.Sensitive = false;
            _emptyTrash.Sensitive = false;
            return;
        }

        var isFolder = item is InventoryFolder;
        var type = isFolder ? "Folder" : (item as InventoryItem)?.InventoryType.ToString() ?? "Item";
        var description = item is InventoryItem inventoryItem ? inventoryItem.Description : string.Empty;
        var wornText = item is InventoryItem wornItem && IsWorn(wornItem) ? "\n\nWorn: Yes" : string.Empty;
        _details.Text = $"Name: {item.Name}\n\nType: {type}{wornText}\n\nDescription: {description}\n\nUUID: {item.UUID}";
        var isRoot = item.UUID == Store.RootFolder?.UUID || item.UUID == Store.LibraryFolder?.UUID;
        var protectedFolder = item is InventoryFolder folder && folder.PreferredType != FolderType.None;
        var protectedParent = Store.TryGetValue(item.ParentUUID, out InventoryBase? parent) &&
                              parent is InventoryFolder parentFolder &&
                              parentFolder.PreferredType is FolderType.CurrentOutfit or FolderType.Inbox;
        var canModify = isRoot || protectedFolder || protectedParent || _session.Rlv.CanModifyInventory(item);
        _newFolder.Sensitive = isFolder && !_selectedIsLibrary && !protectedParent &&
                               item is InventoryFolder f && f.PreferredType is not (FolderType.Trash or FolderType.CurrentOutfit or FolderType.Inbox);
        _rename.Sensitive = canModify && !_selectedIsLibrary && !isRoot && !protectedFolder && !protectedParent;
        _trash.Sensitive = !_selectedIsLibrary && !isRoot && !protectedFolder && !protectedParent &&
                           TryGetTrashFolder(out var trashId) && item.ParentUUID != trashId &&
                           !IsKnownWorn(item) && canModify;
        var selectedItem = item as InventoryItem;
        var target = selectedItem != null && !_selectedIsLibrary ? ResolveItem(selectedItem) : null;
        var attachable = target is InventoryWearable or InventoryObject or InventoryAttachment;
        var worn = selectedItem != null && IsWorn(selectedItem);
        _add.Sensitive = attachable && !worn && selectedItem != null && _session.Rlv.CanAdd(selectedItem, null);
        _detach.Sensitive = attachable && worn &&
                            target is not InventoryWearable { AssetType: AssetType.Bodypart } &&
                            selectedItem != null && _session.Rlv.CanDetach(selectedItem);
        _addTo.Sensitive = target is InventoryObject or InventoryAttachment && !worn;
        _addToHud.Sensitive = _addTo.Sensitive;
        _emptyTrash.Sensitive = !_selectedIsLibrary && item is InventoryFolder trashFolder &&
                                IsRootTrash(trashFolder);
    }

    private bool TryGetTrashFolder(out UUID trashId)
    {
        trashId = Client.Inventory.FindFolderForType(FolderType.Trash);
        return trashId != UUID.Zero && Store != null &&
               Store.TryGetValue(trashId, out InventoryBase? folder) &&
               folder is InventoryFolder f && f.PreferredType == FolderType.Trash;
    }

    private bool IsKnownWorn(InventoryBase item)
    {
        var id = item is InventoryItem link && link.IsLink() ? link.AssetUUID : item.UUID;
        try
        {
            if (Client.Appearance.GetWearables().Any(w => w.ItemID == id)) return true;
            if (Client.Appearance.GetAttachmentsByItemId().ContainsKey(id)) return true;
            if (Client.Self.ActiveGestures.ContainsKey(id)) return true;
        }
        catch { /* Appearance data can still be loading. */ }
        return false;
    }

    private void RefreshSelectedFolder()
    {
        var id = _selectedId;
        var library = _selectedIsLibrary;
        if (id == UUID.Zero || Store == null || !Store.TryGetValue(id, out InventoryBase? item) || item is not InventoryFolder)
        {
            id = Store?.RootFolder?.UUID ?? UUID.Zero;
            library = false;
        }
        if (id != UUID.Zero) FetchFolder(id, library, force: true);
    }

    private void ClearSearch()
    {
        _searchCancel?.Cancel();
        _searching = false;
        _searchEntry.Text = string.Empty;
        _resultsStack.VisibleChildName = "tree";
        _status.Text = "Inventory ready";
        ScheduleRebuild();
    }

    private void BeginSearch()
    {
        var query = _searchEntry.Text.Trim();
        if (query.Length == 0) { ClearSearch(); return; }
        if (Store?.RootFolder == null) { _status.Text = "Inventory is still loading."; return; }
        _searchCancel?.Cancel();
        _searchCancel?.Dispose();
        _searchCancel = new CancellationTokenSource();
        var token = _searchCancel.Token;
        _searching = true;
        _status.Text = "Loading inventory folders for search…";
        _ = Task.Run(async () =>
        {
            try
            {
                var failedFolders = await FetchAllFoldersForSearch(token);
                var matches = FindMatches(query, token);
                GtkDispatch.Post(() =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    ShowSearchResults(matches, query, failedFolders);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GtkDispatch.Post(() =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    _searching = false;
                    _status.Text = $"Search failed: {ex.Message}";
                    ScheduleRebuild();
                });
            }
        }, token);
    }

    private async Task<int> FetchAllFoldersForSearch(CancellationToken token)
    {
        var rootId = Store?.RootFolder?.UUID ?? UUID.Zero;
        if (rootId == UUID.Zero) return 0;
        var queue = new Queue<UUID>();
        var seen = new HashSet<UUID>();
        var failedFolders = 0;
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<UUID>(4);
            while (batch.Count < 4 && queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (seen.Add(id)) batch.Add(id);
            }
            if (batch.Count == 0) continue;
            await Task.WhenAll(batch.Select(async id =>
            {
                try
                {
                    await Client.Inventory.RequestFolderContentsAsync(
                        id, Client.Self.AgentID, true, true, InventorySortOrder.ByName, token);
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref failedFolders); }
            }));
            foreach (var id in batch)
                foreach (var child in GetContents(id).OfType<InventoryFolder>())
                    if (!seen.Contains(child.UUID)) queue.Enqueue(child.UUID);

            if (seen.Count % 20 < batch.Count)
            {
                var count = seen.Count;
                GtkDispatch.Post(() => { if (!_disposed && !token.IsCancellationRequested)
                    _status.Text = $"Searching inventory: {count} folders loaded…"; });
            }
        }
        return failedFolders;
    }

    private List<(UUID Id, string Display)> FindMatches(string query, CancellationToken token)
    {
        var results = new List<(UUID, string)>();
        var root = Store?.RootFolder;
        if (root == null) return results;
        var queue = new Queue<(InventoryFolder Folder, string Path)>();
        var seen = new HashSet<UUID>();
        queue.Enqueue((root, root.Name ?? "My Inventory"));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (folder, path) = queue.Dequeue();
            if (!seen.Add(folder.UUID)) continue;
            foreach (var child in GetContents(folder.UUID))
            {
                var display = $"{path} / {child.Name}";
                if (child.Name?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true && results.Count < 500)
                    results.Add((child.UUID, display));
                if (child is InventoryFolder subfolder)
                    queue.Enqueue((subfolder, display));
            }
        }
        results.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Item2, b.Item2));
        return results;
    }

    private void ShowSearchResults(List<(UUID Id, string Display)> matches, string query, int failedFolders)
    {
        _searching = false;
        foreach (Widget child in _searchResults.Children) _searchResults.Remove(child);
        _searchButtons.Clear();
        foreach (var (id, display) in matches)
        {
            var button = new Button(display + (IsWornId(id) ? " (worn)" : string.Empty))
            {
                TooltipText = id.ToString()
            };
            button.Clicked += (_, _) =>
            {
                _selectedId = id;
                _selectedIsLibrary = false;
                RefreshWornState();
                UpdateDetails();
            };
            _searchButtons.Add((button, id, display));
            _searchResults.Add(button);
        }
        _searchResults.ShowAll();
        _resultsStack.VisibleChildName = "search";
        var summary = matches.Count == 500
            ? $"Showing the first 500 results for “{query}”."
            : $"Found {matches.Count} results for “{query}”.";
        _status.Text = failedFolders == 0 ? summary
            : $"{summary} {failedFolders} folder(s) could not be loaded.";
        ScheduleRebuild();
    }

    private string? AskForName(string title, string initial)
    {
        var dialog = new Dialog(title, (Window)Toplevel, DialogFlags.Modal);
        dialog.AddButton("Cancel", ResponseType.Cancel);
        dialog.AddButton("Save", ResponseType.Ok);
        var input = new Entry { Text = initial, ActivatesDefault = true };
        dialog.ContentArea.PackStart(input, false, false, 8);
        dialog.DefaultResponse = ResponseType.Ok;
        dialog.ShowAll();
        input.GrabFocus();
        var accepted = (ResponseType)dialog.Run() == ResponseType.Ok;
        var result = accepted ? input.Text.Trim() : null;
        dialog.Destroy();
        return result;
    }

    private void CreateFolder()
    {
        if (Store == null || _selectedIsLibrary ||
            !Store.TryGetValue(_selectedId, out InventoryBase? selected) || selected is not InventoryFolder folder)
            return;
        var name = AskForName("New folder", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Client.Inventory.CreateFolder(folder.UUID, name);
            _expanded.Add(folder.UUID);
            FetchFolder(folder.UUID, false, force: true);
        }
        catch (Exception ex) { _status.Text = $"Could not create folder: {ex.Message}"; }
    }

    private void RenameSelected()
    {
        if (Store == null || _selectedIsLibrary ||
            !Store.TryGetValue(_selectedId, out InventoryBase? selected)) return;
        if (!_session.Rlv.CanModifyInventory(selected)) { _status.Text = "Changing this item is restricted by RLV."; return; }
        var name = AskForName("Rename inventory item", selected.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name) || name == selected.Name) return;
        var parentId = selected.ParentUUID;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_session.Rlv.CanModifyInventory(selected)) throw new InvalidOperationException("Changing this item is restricted by RLV.");
                if (selected is InventoryFolder)
                    Client.Inventory.UpdateFolderProperties(selected.UUID, parentId, name, FolderType.None);
                else
                    await Client.Inventory.MoveItemAsync(selected.UUID, parentId, name);
                GtkDispatch.Post(() =>
                {
                    if (_disposed) return;
                    _status.Text = $"Renamed to {name}.";
                    FetchFolder(parentId, false, force: true);
                });
            }
            catch (Exception ex)
            {
                GtkDispatch.Post(() => { if (!_disposed) _status.Text = $"Rename failed: {ex.Message}"; });
            }
        });
    }

    private void MoveSelectedToTrash()
    {
        if (!_trash.Sensitive || Store == null || _selectedIsLibrary ||
            !Store.TryGetValue(_selectedId, out InventoryBase? selected)) return;
        if (!TryGetTrashFolder(out var trashId) || trashId == selected.UUID || IsKnownWorn(selected)) return;
        var confirm = new MessageDialog((Window)Toplevel, DialogFlags.Modal,
            MessageType.Question, ButtonsType.YesNo, $"Move “{selected.Name}” to Trash?");
        var accepted = (ResponseType)confirm.Run() == ResponseType.Yes;
        confirm.Destroy();
        if (!accepted) return;

        var oldParent = selected.ParentUUID;
        _status.Text = $"Moving {selected.Name} to Trash…";
        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await MoveToTrashOnServerAsync(selected, trashId);
                if (!confirmed)
                    throw new InvalidOperationException("The server did not confirm the move.");

                UpdateLocalParent(selected.UUID, trashId);
                GtkDispatch.Post(() =>
                {
                    if (_disposed) return;
                    _selectedId = UUID.Zero;
                    FetchFolder(oldParent, false, force: true);
                    if (_expanded.Contains(trashId)) FetchFolder(trashId, false, force: true);
                    _status.Text = $"Moved {selected.Name} to Trash.";
                    ScheduleRebuild();
                });
            }
            catch (Exception ex)
            {
                UpdateLocalParent(selected.UUID, oldParent);
                GtkDispatch.Post(() => { if (!_disposed) _status.Text = $"Move failed: {ex.Message}"; });
            }
        });
    }

    private async Task<bool> MoveToTrashOnServerAsync(InventoryBase selected, UUID trashId)
    {
        if (!_session.Rlv.CanModifyInventory(selected)) throw new InvalidOperationException("Moving this item is restricted by RLV.");
        // Second Life's viewer moves inventory to Trash with these packets.
        // Send them directly so the library cannot route the move through AIS
        // or update its local store before the server has processed it.
        if (selected is InventoryFolder)
        {
            var move = new MoveInventoryFolderPacket
            {
                AgentData = { AgentID = Client.Self.AgentID, SessionID = Client.Self.SessionID, Stamp = true },
                InventoryData = new MoveInventoryFolderPacket.InventoryDataBlock[1]
            };
            move.InventoryData[0] = new MoveInventoryFolderPacket.InventoryDataBlock
            {
                FolderID = selected.UUID, ParentID = trashId
            };
            move.Header.Reliable = true;
            Client.Network.SendPacket(move);
        }
        else
        {
            var move = new MoveInventoryItemPacket
            {
                AgentData = { AgentID = Client.Self.AgentID, SessionID = Client.Self.SessionID, Stamp = true },
                InventoryData = new MoveInventoryItemPacket.InventoryDataBlock[1]
            };
            move.InventoryData[0] = new MoveInventoryItemPacket.InventoryDataBlock
            {
                ItemID = selected.UUID, FolderID = trashId,
                NewName = Utils.StringToBytes(string.Empty)
            };
            move.Header.Reliable = true;
            Client.Network.SendPacket(move);
        }

        // UDP has no acknowledgement. Confirm the new parent with a fresh HTTP
        // inventory fetch; allow for the server applying the packet asynchronously.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(750);
            if (selected is InventoryItem item)
            {
                var fetched = await Client.Inventory.FetchItemHttpAsync(item.UUID, Client.Self.AgentID);
                if (fetched?.ParentUUID == trashId) return true;
                if (fetched != null) continue;
            }

            // FetchInventory2 is not offered on every grid. The Trash listing
            // also confirms folder moves, which cannot be fetched as single items.
            var contents = await Client.Inventory.RequestFolderContentsAsync(
                trashId, Client.Self.AgentID, selected is InventoryFolder,
                selected is InventoryItem, InventorySortOrder.ByName);
            if (contents.Any(child => child.UUID == selected.UUID)) return true;
        }
        return false;
    }

    private void UpdateLocalParent(UUID id, UUID parentId)
    {
        var inventory = Store;
        if (inventory == null || !inventory.TryGetValue(id, out InventoryBase? current) ||
            current.ParentUUID == parentId) return;
        current.ParentUUID = parentId;
        inventory.UpdateNodeFor(current);
    }

    private bool IsRootTrash(InventoryFolder folder) =>
        folder.PreferredType == FolderType.Trash &&
        folder.ParentUUID == Store?.RootFolder?.UUID &&
        TryGetTrashFolder(out var trashId) && folder.UUID == trashId;

    private void EmptyTrash()
    {
        if (Store == null || !Store.TryGetValue(_selectedId, out InventoryBase? selected) ||
            selected is not InventoryFolder folder || !IsRootTrash(folder)) return;

        var confirm = new MessageDialog((Window)Toplevel, DialogFlags.Modal,
            MessageType.Warning, ButtonsType.YesNo,
            "Permanently delete everything in Trash? This cannot be undone.");
        var accepted = (ResponseType)confirm.Run() == ResponseType.Yes;
        confirm.Destroy();
        if (!accepted) return;

        _status.Text = "Emptying Trash…";
        _ = Task.Run(async () =>
        {
            try
            {
                await Client.Inventory.EmptyTrashAsync();
                GtkDispatch.Post(() =>
                {
                    if (_disposed) return;
                    _fetched.Remove(folder.UUID);
                    _status.Text = "Trash emptied.";
                    FetchFolder(folder.UUID, false, force: true);
                    ScheduleRebuild();
                });
            }
            catch (Exception ex)
            {
                GtkDispatch.Post(() => { if (!_disposed) _status.Text = $"Could not empty Trash: {ex.Message}"; });
            }
        });
    }

    private void ChangeOutfit(UUID id, AttachmentPoint? point, bool detach)
    {
        if (_disposed || !_session.IsConnected || Store == null ||
            !Store.TryGetValue(id, out InventoryBase? selected) || selected is not InventoryItem item)
            return;
        var target = ResolveItem(item);
        if (target is not (InventoryWearable or InventoryObject or InventoryAttachment)) return;
        RefreshWornState();
        if (detach != IsWorn(item)) return;
        if (detach ? !_session.Rlv.CanDetach(item) : !_session.Rlv.CanAdd(item, point))
        {
            _status.Text = detach ? "Removing this item is restricted by RLV." : "Adding this item is restricted by RLV.";
            return;
        }

        _status.Text = detach ? $"Detaching {target.Name}…" : $"Adding {target.Name}…";
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.Outfit.GetCurrentOutfitLinksAsync(CancellationToken.None);
                if (item.IsLink())
                {
                    // A link can be loaded before its target has current metadata.
                    // Fetch the target, then let COF resolve the original link.
                    var freshTarget = await Client.Inventory.FetchItemHttpAsync(
                        item.AssetUUID, Client.Self.AgentID);
                    if (freshTarget is InventoryWearable or InventoryObject or InventoryAttachment)
                        target = freshTarget;
                }
                if (detach)
                    await _session.Rlv.RemoveAsync(target, CancellationToken.None);
                else
                {
                    var replace = target is InventoryWearable wearable &&
                                  (wearable.AssetType == AssetType.Bodypart ||
                                   wearable.WearableType == WearableType.Physics);
                    await _session.Rlv.AddAsync(target, point, replace, CancellationToken.None);
                }

                GtkDispatch.Post(() =>
                {
                    if (_disposed) return;
                    _status.Text = detach ? $"Detached {target.Name}." : $"Add requested for {target.Name}.";
                    ScheduleRebuild();
                    Timeout.Add(1500, () => { if (!_disposed) ScheduleRebuild(); return false; });
                });
            }
            catch (Exception ex)
            {
                GtkDispatch.Post(() =>
                {
                    if (!_disposed) _status.Text = $"Could not {(detach ? "detach" : "add")} {target.Name}: {ex.Message}";
                });
            }
        });
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Rlv.Changed -= OnRestrictionsChanged;
        _session.StateChanged -= OnSessionChanged;
        _searchCancel?.Cancel();
        _searchCancel?.Dispose();
        if (_active)
        {
            Client.Inventory.FolderUpdated -= OnFolderUpdated;
            Client.Inventory.ItemReceived -= OnItemReceived;
            Client.Appearance.AppearanceSet -= OnAppearanceChanged;
            Client.Appearance.AgentWearablesReply -= OnWearablesChanged;
        }
        DetachStoreEvents();
    }

    private void DetachStoreEvents()
    {
        if (_subscribedStore != null)
        {
            _subscribedStore.InventoryObjectAdded -= OnObjectAdded;
            _subscribedStore.InventoryObjectRemoved -= OnObjectRemoved;
            _subscribedStore.InventoryObjectUpdated -= OnObjectUpdated;
            _subscribedStore = null;
        }
    }

    private void OnRestrictionsChanged()
    {
        if (!_disposed && _active) UpdateDetails();
    }
}
