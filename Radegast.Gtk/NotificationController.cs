namespace Radegast.Gtk;

internal interface INotificationOutput : IDisposable
{
    string? Error { get; }
    void Show(string key, string accountId, NotificationCategory category, string title, string body);
    void Clear(string? accountId = null, NotificationCategory? category = null);
}

/// <summary>Apply shared preferences to account events without changing chat or menus.</summary>
internal sealed class NotificationController : IDisposable
{
    private readonly GlobalSettings _settings;
    private readonly INotificationOutput _output;
    private bool _disposed;
    public string? Error => _output.Error;

    public NotificationController(GlobalSettings settings, INotificationOutput output)
    {
        _settings = settings;
        _output = output;
        settings.Changed += OnSettingsChanged;
    }

    public void Notify(AccountSession account, AccountNotification notice, bool alreadyVisible)
    {
        if (_disposed || alreadyVisible || !_settings.Value.IsEnabled(notice.Category)) return;
        _output.Show($"{account.Id}/{notice.Category}/{notice.TargetId}", account.Id, notice.Category,
            PlainText(notice.Title, 160), notice.Message);
    }

    public void Test()
    {
        if (_disposed) return;
        _output.Show("settings-test", string.Empty, NotificationCategory.Menus, Program.ViewerName,
            "Your notification settings are active.");
    }

    private static string PlainText(string text, int limit)
    {
        text = text.Replace("\0", string.Empty);
        if (text.Length <= limit) return text;
        if (char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit] + "…";
    }

    public void CloseAccount(string accountId) => _output.Clear(accountId);

    private void OnSettingsChanged()
    {
        foreach (var category in Enum.GetValues<NotificationCategory>())
            if (!_settings.Value.IsEnabled(category)) _output.Clear(category: category);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _output.Dispose();
    }
}
