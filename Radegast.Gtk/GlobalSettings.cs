using System.Text.Json;
using LibreMetaverse;

namespace Radegast.Gtk;

internal enum NotificationCategory { InstantMessages, GroupChats, WornObjects, Menus, Friends, TeleportOffers, SimRestarts }
internal sealed record AccountNotification(NotificationCategory Category, string Title, string Message, UUID TargetId);

internal sealed record NotificationSettings
{
    public bool InstantMessages { get; init; } = true;
    public bool GroupChats { get; init; } = true;
    public bool WornObjects { get; init; } = true;
    public bool Menus { get; init; } = true;
    public bool Friends { get; init; } = true;
    public bool TeleportOffers { get; init; } = true;
    public bool SimRestarts { get; init; } = true;

    public bool IsEnabled(NotificationCategory category) => category switch
    {
        NotificationCategory.InstantMessages => InstantMessages,
        NotificationCategory.GroupChats => GroupChats,
        NotificationCategory.WornObjects => WornObjects,
        NotificationCategory.Menus => Menus,
        NotificationCategory.Friends => Friends,
        NotificationCategory.TeleportOffers => TeleportOffers,
        NotificationCategory.SimRestarts => SimRestarts,
        _ => false
    };

    public NotificationSettings WithCategory(NotificationCategory category, bool enabled) => category switch
    {
        NotificationCategory.InstantMessages => this with { InstantMessages = enabled },
        NotificationCategory.GroupChats => this with { GroupChats = enabled },
        NotificationCategory.WornObjects => this with { WornObjects = enabled },
        NotificationCategory.Menus => this with { Menus = enabled },
        NotificationCategory.Friends => this with { Friends = enabled },
        NotificationCategory.TeleportOffers => this with { TeleportOffers = enabled },
        NotificationCategory.SimRestarts => this with { SimRestarts = enabled },
        _ => this
    };
}

/// <summary>Shared notification preferences, independent of account connections.</summary>
internal sealed class GlobalSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }
    public string? LoadError { get; }
    public NotificationSettings Value { get; private set; } = new();
    public event Action? Changed;

    public GlobalSettings(string? path = null)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        FilePath = Path.GetFullPath(path ?? Path.Combine(config, "pawprint-viewer", "settings.json"));
        try
        {
            if (File.Exists(FilePath)) Value = JsonSerializer.Deserialize<NotificationSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = $"Saved settings could not be read. Using defaults. {ex.Message}";
        }
    }

    public void Update(NotificationSettings value)
    {
        Value = value;
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Changed?.Invoke();
        }
    }
}
