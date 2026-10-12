using System.Security.Cryptography;
using System.Text;

namespace Radegast.Gtk;

internal sealed class AccountStoragePaths
{
    public string RootDirectory { get; }
    public AccountStoragePaths(string? root = null) => RootDirectory = Path.GetFullPath(root ?? DefaultRoot);

    public static string DefaultRoot
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "pawprint-viewer");
        }
    }

    public string AccountDirectory(SavedLogin identity)
    {
        var directory = Path.Combine(RootDirectory, ResidentFileName(identity.AccountName));
        // SL uses the requested simple layout. Identically named residents on
        // other grids must have separate inventory and conversations.
        if (Uri.TryCreate(identity.LoginUri, UriKind.Absolute, out var grid) &&
            grid.Host.Equals("login.agni.lindenlab.com", StringComparison.OrdinalIgnoreCase)) return directory;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SavedLogin.NormalizeUri(identity.LoginUri))));
        return Path.Combine(directory, "grids", key[..16].ToLowerInvariant());
    }

    public string CacheDirectory(SavedLogin identity) => Path.Combine(AccountDirectory(identity), "cache");
    public string ChatDirectory(SavedLogin identity) => Path.Combine(AccountDirectory(identity), "chats");

    public static string ResidentFileName(string name)
    {
        var normalized = SavedLogin.NormalizeName(name).Replace(' ', '.');
        if (normalized.Length == 0) throw new ArgumentException("A resident name is required.", nameof(name));
        var result = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(normalized))
        {
            if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' || value is (byte)'.' or (byte)'_' or (byte)'-')
                result.Append((char)value);
            else result.Append('%').Append(value.ToString("X2"));
        }
        var text = result.ToString().Trim('.');
        if (text.Length > 100) text = text[..70] + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return text.Length == 0 ? "resident" : text;
    }
}
