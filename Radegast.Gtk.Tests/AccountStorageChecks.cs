using System.Reflection;
using LibreMetaverse;
using Radegast.Gtk;

internal static class AccountStorageChecks
{
    private const string Sl = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    public static async Task PathsAndMigration()
    {
        using var directory = new CacheDirectory();
        var paths = new AccountStoragePaths(Path.Combine(directory.Path, "settings"));
        var identity = new SavedLogin("Test Account", Sl);
        Check(paths.CacheDirectory(identity) == Path.Combine(paths.RootDirectory, "test.account", "cache") &&
            paths.ChatDirectory(identity) == Path.Combine(paths.RootDirectory, "test.account", "chats"), "SL account storage did not use the requested layout");
        Check(paths.AccountDirectory(new("test.account", Sl)) == paths.AccountDirectory(identity), "Name spelling variants created different account folders");
        Check(paths.AccountDirectory(new("Test Account", "https://other.test/login")) != paths.AccountDirectory(identity), "Identical names on different grids shared transcripts");
        var hostile = paths.AccountDirectory(new("../../outside account", Sl));
        Check(hostile.StartsWith(paths.RootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.GetRelativePath(paths.RootDirectory, hostile).StartsWith(".."),
            "An account name escaped the settings folder");

        using var f = new Fixture();
        var store = f.Client.Inventory.Store!;
        var folders = new Queue<InventoryFolder>(); folders.Enqueue(store.RootFolder!);
        var all = new List<InventoryFolder>();
        while (folders.TryDequeue(out var folder))
        {
            folder.Version = 7; all.Add(folder);
            foreach (var child in store.GetContents(folder.UUID).OfType<InventoryFolder>()) folders.Enqueue(child);
        }
        var legacyDirectory = Path.Combine(directory.Path, "legacy");
        var old = new InventoryDiskCache(legacyDirectory);
        await old.SaveAsync(identity, old.Capture(identity, store));
        var fresh = new LibreMetaverse.Inventory(f.Client, f.Owner);
        foreach (var folder in all)
        {
            var copy = new InventoryFolder(folder.UUID) { Name = folder.Name, ParentUUID = folder.ParentUUID,
                OwnerID = folder.OwnerID, Version = folder.Version, PreferredType = folder.PreferredType };
            fresh.UpdateNodeFor(copy);
            if (folder.UUID == store.RootFolder!.UUID) fresh.RootFolder = copy;
        }
        var cache = new InventoryDiskCache(paths: paths, legacyDirectory: legacyDirectory);
        Check(cache.Restore(identity, fresh) > 0 && fresh.Contains(f.Object.UUID), "Moving the cache required rebuilding matching inventory metadata");
        Check(File.Exists(cache.FilePath(identity)) && File.Exists(old.FilePath(identity)), "Migration lost the old cache or failed to save its new copy");
        Check(Path.GetDirectoryName(cache.FilePath(identity)) == paths.CacheDirectory(identity), "The inventory file was not stored in the account's cache folder");
        Check(fresh.GetContents(f.Outfit.COF!.UUID).Count == 0 && fresh.GetNodeFor(f.Outfit.COF.UUID).NeedsUpdate,
            "Migration trusted a saved outfit instead of loading the live Current Outfit");
        if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(cache.FilePath(identity)) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
            "The migrated inventory cache was readable by other users");
    }

    public static async Task TranscriptsAndNames()
    {
        using var directory = new CacheDirectory();
        var paths = new AccountStoragePaths(directory.Path);
        var account = new SavedLogin("Test Account", Sl);
        var peer = UUID.Random();
        var errors = new List<string>();
        var first = new ImTranscriptStore(paths, errors.Add);
        first.Append(account, peer, null, new(DateTime.Now, "first outgoing 🌸", true));
        await first.FlushAsync();
        Check(File.Exists(Path.Combine(paths.ChatDirectory(account), peer + ".txt")), "An unresolved conversation was not logged");
        first.ResolveName(account, peer, "Other Avatar");
        first.Append(account, peer, "Other Avatar", new(DateTime.Now, "incoming [https://example.test Wishlist ♥]\nsecond line", false));
        await first.FlushAsync();
        var file = Path.Combine(paths.ChatDirectory(account), "other.avatar.txt");
        Check(File.Exists(file) && !File.Exists(Path.Combine(paths.ChatDirectory(account), peer + ".txt")), "Resolving the name created a second conversation file");
        var text = await File.ReadAllTextAsync(file);
        Check(text.Contains("Test Account: first outgoing 🌸") && text.Contains("Other Avatar: incoming [https://example.test Wishlist ♥]\nsecond line"),
            "IM transcripts lost direction, Unicode, URLs or multiline text");
        first.Dispose(); await first.FlushAsync();
        using var next = new ImTranscriptStore(paths, errors.Add);
        next.Append(account, peer, "Other Avatar", new(DateTime.Now, "after viewer update", true));
        next.Append(new("Second Avatar", Sl), peer, "Other Avatar", new(DateTime.Now, "different account", true));
        next.Append(new(account.AccountName, "https://grid.test/login"), peer, "Other Avatar", new(DateTime.Now, "different grid", true));
        await next.FlushAsync();
        text = await File.ReadAllTextAsync(file);
        Check(text.Contains("first outgoing") && text.Contains("after viewer update") && !text.Contains("different account") && !text.Contains("different grid"),
            "Restarting the writer overwrote history or mixed accounts/grids");
        Check(Directory.GetFiles(paths.ChatDirectory(account), "*.txt").Length == 1 && errors.Count == 0, "The writer produced duplicate logs or a persistence error");
        if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "Private conversations were readable by other users");
        next.Dispose(); await next.FlushAsync();
    }

    public static async Task CollisionsAndPathSafety()
    {
        using var directory = new CacheDirectory();
        var paths = new AccountStoragePaths(directory.Path);
        var account = new SavedLogin("Test Account", Sl);
        using var store = new ImTranscriptStore(paths);
        var a = UUID.Random(); var b = UUID.Random(); var hostile = UUID.Random();
        store.Append(account, a, "Same Name", new(DateTime.Now, "first peer", false));
        store.Append(account, b, "Same Name", new(DateTime.Now, "second peer", false));
        store.Append(account, hostile, "../../escape person", new(DateTime.Now, "safe path", false));
        await store.FlushAsync();
        var files = Directory.GetFiles(paths.ChatDirectory(account), "*.txt");
        Check(files.Length == 3 && files.All(file => Path.GetDirectoryName(file) == paths.ChatDirectory(account)), "Peer names escaped the chat directory or merged distinct conversations");
        Check((await File.ReadAllTextAsync(Path.Combine(paths.ChatDirectory(account), "same.name.txt"))).Contains("first peer") &&
            !(await File.ReadAllTextAsync(Path.Combine(paths.ChatDirectory(account), "same.name.txt"))).Contains("second peer"), "A filename collision mixed two residents' messages");
        // A lost index must not permit a different UUID to append to an old log.
        store.Dispose(); await store.FlushAsync();
        File.Delete(Path.Combine(paths.ChatDirectory(account), ".conversations.json"));
        using var restarted = new ImTranscriptStore(paths);
        restarted.Append(account, UUID.Random(), "Same Name", new(DateTime.Now, "third peer", false));
        await restarted.FlushAsync();
        Check(!(await File.ReadAllTextAsync(Path.Combine(paths.ChatDirectory(account), "same.name.txt"))).Contains("third peer"), "A missing index allowed transcripts to be merged by name alone");
        restarted.Dispose(); await restarted.FlushAsync();
    }

    public static async Task SessionImFiltering()
    {
        using var h = new ReconnectHarness(); h.Connect();
        var peer = UUID.Random();
        await Receive(h, h.Fixture.PrivateIm(peer, "Other Avatar", "accepted incoming"));
        h.Account.SendInstantMessage(peer, "accepted outgoing");
        await h.Account.FlushImTranscriptsAsync();
        var identity = new SavedLogin(h.Name, h.Grid.LoginURI);
        var file = Path.Combine(h.Store.StoragePaths.ChatDirectory(identity), "other.avatar.txt");
        Check(File.Exists(file) && (await File.ReadAllTextAsync(file)).Contains("accepted outgoing"), "The account did not persist both incoming and outgoing IMs");
        h.Account.BlockResident(peer);
        await Receive(h, h.Fixture.PrivateIm(peer, "Other Avatar", "blocked incoming"));
        await Receive(h, h.Fixture.GroupIm(UUID.Random(), peer, "Other Avatar", "group message"));
        await h.Account.FlushImTranscriptsAsync();
        var text = await File.ReadAllTextAsync(file);
        Check(!text.Contains("blocked incoming") && !text.Contains("group message"), "Blocked or group messages were logged as accepted private IMs");
        h.Disconnect();
        Check(File.Exists(file), "Disconnect deleted the saved conversation");
    }

    private static async Task Receive(ReconnectHarness h, LibreMetaverse.Packets.Packet packet)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? _, InstantMessageEventArgs e) => complete.TrySetResult();
        h.Account.Client.Self.IM += Received;
        try { h.Fixture.Receive(packet); await complete.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { h.Account.Client.Self.IM -= Received; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class CacheDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pawprint-account-storage-" + Guid.NewGuid().ToString("N"));
        public CacheDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
