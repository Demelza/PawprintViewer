using System.Text.Json;

namespace Radegast.Gtk;

internal sealed record AccountSettings
{
    public const int MaximumDelaySeconds = 86400;
    public const int MaximumReturnDelayMinutes = 1440;
    public bool AutoReconnect { get; init; }
    public int ReconnectDelaySeconds { get; init; } = 30;
    public bool TeleportOnRegionRestart { get; init; }
    public string RestartDestinationRegion { get; init; } = "";
    public float RestartDestinationX { get; init; } = 128;
    public float RestartDestinationY { get; init; } = 128;
    public float RestartDestinationZ { get; init; } = 25;
    public int ReturnDelayMinutes { get; init; } = 5;

    public void Validate()
    {
        if (ReconnectDelaySeconds is < 1 or > MaximumDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(ReconnectDelaySeconds), "Reconnect delay must be between 1 and 86400 seconds.");
        if (ReturnDelayMinutes is < 1 or > MaximumReturnDelayMinutes)
            throw new ArgumentOutOfRangeException(nameof(ReturnDelayMinutes), "Return delay must be between 1 and 1440 minutes.");
        if (RestartDestinationRegion == null || new[] { RestartDestinationX, RestartDestinationY, RestartDestinationZ }
            .Any(value => !float.IsFinite(value) || value < 0 || value > 65535))
            throw new ArgumentOutOfRangeException(nameof(RestartDestinationRegion), "Enter a region and coordinates between 0 and 65535.");
    }
}

/// <summary>Preferences for one resident on one grid. Login secrets remain outside these files.</summary>
internal sealed class AccountSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }

    public AccountSettingsStore(string? directory = null)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(config, "pawprint-viewer", "account-settings"));
    }

    public string FilePath(SavedLogin account) => Path.Combine(DirectoryPath, account.SecretKey + ".json");

    public AccountSettings Load(SavedLogin account, out string? error)
    {
        error = null;
        try
        {
            var path = FilePath(account);
            var value = File.Exists(path) ? JsonSerializer.Deserialize<AccountSettings>(File.ReadAllText(path)) ?? new() : new();
            value.Validate();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            error = "Saved account settings could not be read. Using defaults.";
            return new();
        }
    }

    public void Save(SavedLogin account, AccountSettings value)
    {
        value.Validate();
        var path = FilePath(account);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
