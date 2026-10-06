using Gtk;

namespace Radegast.Gtk;

internal sealed class GlobalSettingsWindow : Window
{
    private readonly GlobalSettings _settings;
    private readonly Label _status = new() { Xalign = 0, Wrap = true };
    private bool _closing;

    public GlobalSettingsWindow(Window parent, GlobalSettings settings, NotificationController notifications)
        : base("Global Settings — " + Program.ViewerName)
    {
        _settings = settings;
        TransientFor = parent;
        DestroyWithParent = true;
        WindowPosition = WindowPosition.CenterOnParent;
        Resizable = false;
        DeleteEvent += (_, args) => { args.RetVal = true; CloseSettings(); };
        var content = new Box(Orientation.Vertical, 10) { BorderWidth = 12 };
        Add(content);
        content.PackStart(new Label("Desktop notifications for all accounts") { Xalign = 0 }, false, false, 0);
        AddCategory(content, NotificationCategory.InstantMessages, "IMs", "Incoming private messages from residents");
        AddCategory(content, NotificationCategory.GroupChats, "Group Chats", "Incoming group messages");
        AddCategory(content, NotificationCategory.WornObjects, "Worn Objects", "Private chat from objects you are wearing");
        AddCategory(content, NotificationCategory.Menus, "Menus", "Object menus and text prompts");
        AddCategory(content, NotificationCategory.Friends, "Friends", "Friends going online or offline");
        AddCategory(content, NotificationCategory.TeleportOffers, "Teleport Offers", "Incoming offers to teleport to another resident");
        _status.SetSizeRequest(360, -1);
        _status.Text = settings.LoadError ?? notifications.Error ?? "Changes apply immediately and are saved automatically.";
        content.PackStart(_status, false, false, 0);
        var buttons = new Box(Orientation.Horizontal, 6);
        var test = new Button("Test notification");
        test.Clicked += (_, _) =>
        {
            notifications.Test();
            _status.Text = notifications.Error ?? "Test notification sent.";
        };
        var dismiss = new Button("Dismiss");
        dismiss.Clicked += (_, _) => CloseSettings();
        buttons.PackStart(test, true, true, 0);
        buttons.PackStart(dismiss, false, false, 0);
        content.PackStart(buttons, false, false, 0);
    }

    private void AddCategory(Box content, NotificationCategory category, string label, string description)
    {
        var toggle = new CheckButton(label) { Active = _settings.Value.IsEnabled(category), TooltipText = description };
        toggle.Toggled += (_, _) =>
        {
            try
            {
                _settings.Update(_settings.Value.WithCategory(category, toggle.Active));
                _status.Text = "Changes apply immediately and are saved automatically.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _status.Text = $"Applied for this session, but settings could not be saved: {ex.Message}";
            }
        };
        content.PackStart(toggle, false, false, 0);
    }

    public void CloseSettings()
    {
        if (_closing) return;
        _closing = true;
        Dispose();
    }
}
