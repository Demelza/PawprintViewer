using System.IO.Compression;
using System.Text.Json;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Radegast.Gtk;

/// <summary>Metadata only. Login's authoritative folder skeleton validates every restored item.</summary>
internal sealed class InventoryDiskCache
{
    private const int Format = 1;
    private const int MaximumEntries = 250_000;
    private const long MaximumFileBytes = 64 * 1024 * 1024;
    private const int MaximumDecodedBytes = 256 * 1024 * 1024;
    public string DirectoryPath { get; }
    private readonly AccountStoragePaths _paths;
    private readonly bool _explicitDirectory;
    private readonly string _legacyDirectory;

    public InventoryDiskCache(string? directory = null, AccountStoragePaths? paths = null, string? legacyDirectory = null)
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathFullyQualified(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        _paths = paths ?? new();
        _explicitDirectory = directory != null;
        DirectoryPath = Path.GetFullPath(directory ?? _paths.RootDirectory);
        _legacyDirectory = Path.GetFullPath(legacyDirectory ?? Path.Combine(cache, "pawprint-viewer", "inventory"));
    }

    public string FilePath(SavedLogin identity) => Path.Combine(_explicitDirectory ? DirectoryPath : _paths.CacheDirectory(identity), identity.SecretKey + ".json.gz");

    public Snapshot Capture(SavedLogin identity, LibreMetaverse.Inventory store)
    {
        var root = store.RootFolder ?? throw new InvalidOperationException("Inventory is not initialized.");
        var folders = new List<Folder>();
        var pending = new Queue<UUID>();
        var seen = new HashSet<UUID>();
        pending.Enqueue(root.UUID);
        var entries = 0;
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id) || !store.TryGetNodeFor(id, out var node) || node.Data is not InventoryFolder folder) continue;
            if (seen.Count > MaximumEntries) break;
            var contents = store.GetContents(id);
            var children = contents.OfType<InventoryFolder>().Where(child => child.OwnerID == store.Owner).ToArray();
            foreach (var child in children) pending.Enqueue(child.UUID);
            // The root's version is not reliably supplied by login; COF must always be fresh.
            if (id == root.UUID || folder.OwnerID != store.Owner || folder.PreferredType == FolderType.CurrentOutfit || folder.Version < 0) continue;
            var version = folder.Version;
            var parent = folder.ParentUUID;
            var itemEntries = contents.OfType<InventoryItem>().Where(item => item.OwnerID == store.Owner && item.ParentUUID == id).ToArray();
            if (entries + itemEntries.Length + 1 > MaximumEntries) continue;
            var items = itemEntries.Select(SerializeItem).ToArray();
            entries += items.Length + 1;
            if (folder.Version != version || folder.ParentUUID != parent) continue;
            // The SDK's DescendentCount also changes as nested items are inserted.
            // Cache direct child counts independently rather than persisting that total.
            var complete = !node.NeedsUpdate;
            folders.Add(new(id.Guid, parent.Guid, version, complete, contents.Count,
                children.Select(child => child.UUID.Guid).ToArray(), items));
        }
        return new(Format, SavedLogin.NormalizeUri(identity.LoginUri), store.Owner.Guid, root.UUID.Guid, folders.ToArray());
    }

    private static string SerializeItem(InventoryItem item)
    {
        var data = (OSDMap)item.GetOSD();
        // GetOSD uses a date, whereas FromOSD expects Unix seconds, and Permissions
        // contains masks rather than the inventory item's owner identity.
        data["created_at"] = OSD.FromInteger(item.CreationDate <= DateTime.UnixEpoch ? 0u : Utils.DateTimeToUnixTime(item.CreationDate));
        ((OSDMap)data["permissions"])["owner_id"] = item.OwnerID;
        return OSDParser.SerializeJsonString(data);
    }

    public int Restore(SavedLogin identity, LibreMetaverse.Inventory store)
    {
        var path = FilePath(identity);
        var destination = path;
        if (!File.Exists(path) && !_explicitDirectory) path = Path.Combine(_legacyDirectory, identity.SecretKey + ".json.gz");
        if (!File.Exists(path)) return 0;
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes) return 0;
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (decoded.Length + read > MaximumDecodedBytes) return 0;
                decoded.Write(buffer, 0, read);
            }
            decoded.Position = 0;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(decoded);
            if (snapshot == null) return 0;
            var restored = Apply(identity, store, snapshot);
            if (path != destination && snapshot.Format == Format && snapshot.Grid == SavedLogin.NormalizeUri(identity.LoginUri) &&
                snapshot.Owner == store.Owner.Guid && snapshot.Root == store.RootFolder?.UUID.Guid)
            {
                // Preserve the old cache as a fallback. Migration is independent
                // of folder validity and cannot make login fail.
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(path, destination, false);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return restored;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            // A broken cache must never prevent login or hide server inventory.
            return 0;
        }
    }

    internal int Apply(SavedLogin identity, LibreMetaverse.Inventory store, Snapshot snapshot)
    {
        if (snapshot.Format != Format || snapshot.Grid != SavedLogin.NormalizeUri(identity.LoginUri) ||
            snapshot.Owner != store.Owner.Guid || snapshot.Root != store.RootFolder?.UUID.Guid || snapshot.Folders == null ||
            snapshot.Folders.Length > MaximumEntries) return 0;
        var count = 0;
        var budget = MaximumEntries;
        foreach (var saved in snapshot.Folders)
        {
            if (saved == null || saved.Items == null || saved.Children == null || (budget -= saved.Items.Length + 1) < 0) break;
            var id = new UUID(saved.Id);
            if (!store.TryGetNodeFor(id, out var node) || node.Data is not InventoryFolder folder ||
                id == store.RootFolder?.UUID || folder.PreferredType == FolderType.CurrentOutfit || folder.OwnerID != store.Owner ||
                folder.Version < 0 || folder.Version != saved.Version || folder.ParentUUID.Guid != saved.Parent || !node.NeedsUpdate) continue;
            // Parse and validate the complete folder before changing the store.
            List<InventoryItem> items;
            try { items = saved.Items.Select(text => InventoryItem.FromOSD(OSDParser.DeserializeJson(text))).ToList(); }
            catch { continue; }
            if (items.Any(item => item.UUID == UUID.Zero || item.OwnerID != store.Owner || item.ParentUUID != id) ||
                items.Select(item => item.UUID).Distinct().Count() != items.Count) continue;
            foreach (var item in items)
            {
                if (store.TryGetValue(item.UUID, out InventoryBase? _)) continue;
                store.UpdateNodeFor(item);
                count++;
            }
            var children = store.GetContents(id).OfType<InventoryFolder>().Select(child => child.UUID.Guid).ToHashSet();
            // Partial item metadata is useful for resolving links, but does not make
            // an unloaded folder complete. Empty folders need the same validation.
            if (saved.Complete && saved.Descendants == saved.Items.Length + saved.Children.Length &&
                children.SetEquals(saved.Children) && store.GetContents(id).Count == saved.Descendants &&
                folder.Version == saved.Version && folder.ParentUUID.Guid == saved.Parent)
            {
                folder.DescendentCount = saved.Descendants;
                node.NeedsUpdate = false;
            }
        }
        return count;
    }

    public async Task SaveAsync(SavedLogin identity, Snapshot snapshot, CancellationToken token = default)
    {
        var path = FilePath(identity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, snapshot, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (new FileInfo(temporary).Length <= MaximumFileBytes) File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal sealed record Snapshot(int Format, string Grid, Guid Owner, Guid Root, Folder[] Folders);
    internal sealed record Folder(Guid Id, Guid Parent, int Version, bool Complete, int Descendants, Guid[] Children, string[] Items);
}
