using Radegast.Gtk;

internal static class SavedLoginChecks
{
    public static async Task Persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-saved-logins-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "accounts.json");
        const string grid = "https://login.example.org/login.cgi", otherGrid = "https://other.example.org/login.cgi";
        var passwords = new Passwords();
        try
        {
            var store = new SavedLoginStore(path, passwords);
            await store.RememberAsync("Alice Resident", grid, "first-test-secret");
            await store.RememberAsync("Bob Example", grid, "second-test-secret");
            await store.RememberAsync("alice", grid, "updated-test-secret");
            await store.RememberAsync("Alice Resident", otherGrid, "other-grid-test-secret");
            var reopened = new SavedLoginStore(path, passwords);
            Check(reopened.ForGrid(grid).Count == 2 && reopened.ForGrid(grid)[0].AccountName == "alice",
                "Resident aliases/case created duplicate remembered accounts or names were not sorted");
            Check(await reopened.LookupPasswordAsync(reopened.ForGrid(grid)[0]) == "updated-test-secret" &&
                await reopened.LookupPasswordAsync(reopened.ForGrid(otherGrid).Single()) == "other-grid-test-secret",
                "Updating a password replaced another grid's credentials or lookup did not persist");
            Check(new SavedLogin("alice", grid).SecretKey == new SavedLogin("ALICE.Resident", grid).SecretKey &&
                new SavedLogin("alice", grid).SecretKey != new SavedLogin("alice", otherGrid).SecretKey,
                "Credential identities differed from login name parsing or ignored the grid");
            var content = File.ReadAllText(path);
            Check(!content.Contains("secret", StringComparison.OrdinalIgnoreCase) && !content.Contains("Password", StringComparison.OrdinalIgnoreCase),
                "The account metadata file contained passwords or secret lookup material");
            if (!OperatingSystem.IsWindows())
                Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Account metadata was accessible to other users");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "An atomic save left temporary files");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    public static async Task Failures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-saved-logins-errors-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "accounts.json");
        const string grid = "https://login.example.org/login.cgi";
        var passwords = new Passwords { FailStore = true };
        try
        {
            var store = new SavedLoginStore(path, passwords);
            try { await store.RememberAsync("Alice Resident", grid, "not-written-to-metadata"); }
            catch (InvalidOperationException) { }
            var reopened = new SavedLoginStore(path, passwords);
            Check(reopened.ForGrid(grid).Count == 1 && await reopened.LookupPasswordAsync(reopened.ForGrid(grid)[0]) == null,
                "An unavailable keyring lost the remembered name or fabricated a password");
            Check(!File.ReadAllText(path).Contains("not-written-to-metadata"), "Keyring failure stored a plaintext fallback password");
            File.WriteAllText(path, "[{\"AccountName\":null,\"LoginUri\":null},null,{\"AccountName\":\"Bad\",\"LoginUri\":\"file:///tmp/test\"}]");
            Check(new SavedLoginStore(path, passwords).ForGrid(grid).Count == 0, "Malformed account records were not ignored");
            File.WriteAllText(path, "invalid json");
            var corrupt = new SavedLoginStore(path, passwords);
            Check(corrupt.LoadError != null && corrupt.ForGrid(grid).Count == 0, "Corrupt account metadata blocked manual login");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await corrupt.RememberAsync("Never saved", grid, "unused", cancelled.Token); }
            catch (OperationCanceledException) { }
            Check(corrupt.ForGrid(grid).Count == 0 && File.ReadAllText(path) == "invalid json", "Cancelled saving wrote account metadata");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Passwords : ILoginPasswordStore
    {
        private readonly Dictionary<string, string> _saved = new();
        public bool FailStore;
        public Task<string?> LookupAsync(string key, CancellationToken token) => Task.FromResult(_saved.GetValueOrDefault(key));
        public Task StoreAsync(string key, string label, string password, CancellationToken token)
        {
            if (FailStore) return Task.FromException(new InvalidOperationException("Test keyring unavailable"));
            _saved[key] = password;
            return Task.CompletedTask;
        }
    }
}
