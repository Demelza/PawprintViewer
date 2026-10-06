using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radegast.Gtk;

internal sealed record SavedLogin(string AccountName, string LoginUri)
{
    [JsonIgnore]
    public string SecretKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        NormalizeUri(LoginUri) + "\n" + NormalizeName(AccountName))));

    public static string NormalizeName(string name)
    {
        var parts = name.Trim().Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty :
            (parts[0] + " " + (parts.Length > 1 ? parts[1] : "Resident")).ToLowerInvariant();
    }

    public static string NormalizeUri(string uri) =>
        Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) ? parsed.AbsoluteUri : uri.Trim();
}

internal interface ILoginPasswordStore
{
    Task<string?> LookupAsync(string key, CancellationToken token);
    Task StoreAsync(string key, string label, string password, CancellationToken token);
}

/// <summary>Account names are local metadata; passwords live in the desktop keyring.</summary>
internal sealed class SavedLoginStore
{
    private readonly ILoginPasswordStore _passwords;
    private readonly List<SavedLogin> _accounts = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }
    public string? LoadError { get; }

    public SavedLoginStore(string? path = null, ILoginPasswordStore? passwords = null)
    {
        _passwords = passwords ?? new SecretServicePasswordStore();
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        FilePath = Path.GetFullPath(path ?? Path.Combine(config, "pawprint-viewer", "accounts.json"));
        try
        {
            if (File.Exists(FilePath))
                foreach (var account in JsonSerializer.Deserialize<List<SavedLogin>>(File.ReadAllText(FilePath)) ?? new())
                    if (account != null && !string.IsNullOrWhiteSpace(account.AccountName) && ValidUri(account.LoginUri) &&
                        !_accounts.Any(saved => saved.SecretKey == account.SecretKey)) _accounts.Add(account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = "Remembered accounts could not be loaded. You can still enter an account manually.";
        }
    }

    private static bool ValidUri(string? uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme is "http" or "https";

    public IReadOnlyList<SavedLogin> ForGrid(string uri) => _accounts
        .Where(account => SavedLogin.NormalizeUri(account.LoginUri) == SavedLogin.NormalizeUri(uri))
        .OrderBy(account => account.AccountName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public Task<string?> LookupPasswordAsync(SavedLogin account, CancellationToken token = default) =>
        _passwords.LookupAsync(account.SecretKey, token);

    public async Task RememberAsync(string name, string loginUri, string password, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name) || !ValidUri(loginUri) || string.IsNullOrEmpty(password))
            throw new ArgumentException("An account, valid login URI and password are required.");
        var account = new SavedLogin(name.Trim(), SavedLogin.NormalizeUri(loginUri));
        var previous = _accounts.FindIndex(saved => saved.SecretKey == account.SecretKey);
        if (previous >= 0) _accounts[previous] = account; else _accounts.Add(account);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
                JsonSerializer.Serialize(file, _accounts, JsonOptions);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        await _passwords.StoreAsync(account.SecretKey, $"{Program.ViewerName}: {account.AccountName}", password, token).ConfigureAwait(false);
    }
}
