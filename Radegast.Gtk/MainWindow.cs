using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed class MainWindow : Window
{
    private readonly Box _accountRows = new(Orientation.Vertical, 4);
    private readonly Stack _pages = new();
    private readonly Stack _nearbyPages = new();
    private readonly Dictionary<AccountSession, SessionWidgets> _sessions = new();
    private readonly Dictionary<AccountSession, HashSet<ScriptDialogWindow>> _scriptDialogs = new();
    private readonly Dictionary<AccountSession, HashSet<ScriptPermissionWindow>> _permissionDialogs = new();
    private readonly ChildWindowPresenter _childWindows;
    private readonly GlobalSettings _globalSettings;
    private readonly NotificationController _notifications;
    private AccountSession? _selected;
    private LoginWindow? _loginWindow;
    private GlobalSettingsWindow? _settingsWindow;

    public MainWindow(GlobalSettings? settings = null, INotificationOutput? notificationOutput = null) : base(Program.ViewerName)
    {
        _childWindows = new ChildWindowPresenter(this);
        _globalSettings = settings ?? new GlobalSettings();
        _notifications = new NotificationController(_globalSettings, notificationOutput ?? new DesktopNotificationOutput());
        Destroyed += (_, _) => _notifications.Dispose();
        SetDefaultSize(1120, 720);
        DeleteEvent += (_, _) =>
        {
            _childWindows.Dispose();
            _settingsWindow?.CloseSettings();
            _notifications.Dispose();
            foreach (var session in _sessions.Keys.ToArray())
                RemoveSession(session);
            _loginWindow?.Destroy();
            Application.Quit();
        };

        // Twenty equal columns give each side pane exactly three columns (15%).
        var root = new global::Gtk.Grid { ColumnHomogeneous = true };
        Add(root);

        var rail = new Box(Orientation.Vertical, 6) { BorderWidth = 6 };
        var accountsHeading = new Label("Accounts") { Xalign = 0 };
        rail.PackStart(accountsHeading, false, false, 4);
        var accountScroll = new ScrolledWindow();
        accountScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        accountScroll.Add(_accountRows);
        rail.PackStart(accountScroll, true, true, 0);
        var addButton = new Button("+ Add account");
        addButton.Clicked += (_, _) => ShowLogin();
        rail.PackStart(addButton, false, false, 0);
        var settingsButton = new Button("Global Settings");
        settingsButton.Clicked += (_, _) => ShowGlobalSettings();
        rail.PackStart(settingsButton, false, false, 0);
        root.Attach(rail, 0, 0, 3, 1);

        _pages.AddNamed(new Label("Use + Add account to log in."), "empty");
        _pages.VisibleChildName = "empty";
        var center = new Box(Orientation.Horizontal, 0) { Hexpand = true, Vexpand = true };
        center.PackStart(new Separator(Orientation.Vertical), false, false, 0);
        center.PackStart(_pages, true, true, 0);
        center.PackStart(new Separator(Orientation.Vertical), false, false, 0);
        root.Attach(center, 3, 0, 14, 1);
        _nearbyPages.AddNamed(new Box(Orientation.Vertical, 0), "empty");
        _nearbyPages.VisibleChildName = "empty";
        root.Attach(_nearbyPages, 17, 0, 3, 1);
    }

    internal void ShowChildWindow(Window window, bool showContents = true) => _childWindows.Show(window, showContents);

    private void ShowLogin()
    {
        if (_loginWindow != null)
        {
            ShowChildWindow(_loginWindow, showContents: false);
            return;
        }

        _loginWindow = new LoginWindow(this);
        _loginWindow.LoginSucceeded += AddSession;
        _loginWindow.Destroyed += (_, _) => _loginWindow = null;
        ShowChildWindow(_loginWindow, showContents: false);
    }

    private void ShowGlobalSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new GlobalSettingsWindow(this, _globalSettings, _notifications, () =>
            {
                Present();
                ShowGlobalSettings();
            });
            _settingsWindow.Destroyed += (_, _) => _settingsWindow = null;
        }
        ShowChildWindow(_settingsWindow);
    }

    private void AddSession(AccountSession session)
    {
        var widgets = new SessionWidgets(session);
        _sessions.Add(session, widgets);
        _accountRows.PackStart(widgets.AccountRow, false, false, 0);
        _pages.AddNamed(widgets.Root, session.Id);
        _nearbyPages.AddNamed(widgets.NearbyPane, session.Id);
        widgets.AccountButton.Clicked += (_, _) => SelectSession(session);
        widgets.LogoutButton.Clicked += (_, _) => RemoveSession(session);
        session.StateChanged += OnStateChanged;
        session.ChatLine += OnChatLine;
        session.ConversationChanged += OnConversationChanged;
        session.GroupConversationChanged += OnGroupConversationChanged;
        session.GroupsChanged += OnStateChanged;
        session.NearbyChanged += OnNearbyChanged;
        session.ScriptDialogReceived += OnScriptDialogReceived;
        session.PermissionRequested += OnPermissionRequested;
        session.NotificationReceived += OnNotification;
        widgets.AccountRow.ShowAll();
        widgets.Root.ShowAll();
        widgets.NearbyPane.ShowAll();
        SelectSession(session);
    }

    private void SelectSession(AccountSession session)
    {
        if (!_sessions.TryGetValue(session, out var widgets)) return;
        _selected = session;
        _pages.VisibleChildName = session.Id;
        _nearbyPages.VisibleChildName = session.Id;
        widgets.UnreadCount = 0;
        foreach (var (account, view) in _sessions)
        {
            view.SetSelected(account == session);
            view.UpdateAccountLabel(account == session);
        }
    }

    private void RemoveSession(AccountSession session)
    {
        if (!_sessions.Remove(session, out var widgets)) return;
        session.StateChanged -= OnStateChanged;
        session.ChatLine -= OnChatLine;
        session.ConversationChanged -= OnConversationChanged;
        session.GroupConversationChanged -= OnGroupConversationChanged;
        session.GroupsChanged -= OnStateChanged;
        session.NearbyChanged -= OnNearbyChanged;
        session.ScriptDialogReceived -= OnScriptDialogReceived;
        session.PermissionRequested -= OnPermissionRequested;
        session.NotificationReceived -= OnNotification;
        _notifications.CloseAccount(session.Id);
        if (_scriptDialogs.Remove(session, out var dialogs))
            foreach (var dialog in dialogs.ToArray()) dialog.CloseMenu();
        if (_permissionDialogs.Remove(session, out var permissions))
            foreach (var dialog in permissions.ToArray()) dialog.ClosePrompt();
        widgets.Dispose();
        _accountRows.Remove(widgets.AccountRow);
        _pages.Remove(widgets.Root);
        _nearbyPages.Remove(widgets.NearbyPane);
        session.Dispose();

        if (_selected == session)
        {
            _selected = null;
            var next = _sessions.Keys.FirstOrDefault();
            if (next != null) SelectSession(next);
            else
            {
                _pages.VisibleChildName = "empty";
                _nearbyPages.VisibleChildName = "empty";
            }
        }
    }

    private void OnStateChanged(AccountSession session)
    {
        if (!_sessions.TryGetValue(session, out var widgets)) return;
        widgets.UpdateAccountLabel(session == _selected);
        if (!session.IsConnected) _notifications.CloseAccount(session.Id);
    }

    private void OnChatLine(AccountSession session, string line)
    {
        if (!_sessions.TryGetValue(session, out var widgets)) return;
        widgets.AppendChat(line);
        if (session != _selected)
        {
            widgets.UnreadCount++;
            widgets.UpdateAccountLabel(false);
        }
    }

    private void OnConversationChanged(AccountSession session, ImConversation conversation)
    {
        if (_sessions.TryGetValue(session, out var widgets)) widgets.UpdateAccountLabel(session == _selected);
    }

    private void OnGroupConversationChanged(AccountSession session, GroupConversation conversation)
    {
        if (_sessions.TryGetValue(session, out var widgets)) widgets.UpdateAccountLabel(session == _selected);
    }

    private void OnNearbyChanged(AccountSession session)
    {
        if (_sessions.TryGetValue(session, out var widgets))
            widgets.RefreshNearby();
    }

    private void OnScriptDialogReceived(AccountSession session, ScriptMenu menu)
    {
        if (!_sessions.ContainsKey(session)) return;
        var dialog = new ScriptDialogWindow(this, session, menu);
        if (!_scriptDialogs.TryGetValue(session, out var dialogs))
            _scriptDialogs[session] = dialogs = new HashSet<ScriptDialogWindow>();
        dialogs.Add(dialog);
        dialog.Destroyed += (_, _) => dialogs.Remove(dialog);
        ShowChildWindow(dialog);
    }

    private void OnPermissionRequested(AccountSession session, ScriptQuestionEventArgs request)
    {
        if (!_sessions.ContainsKey(session)) return;
        var dialog = new ScriptPermissionWindow(this, session, request);
        if (!_permissionDialogs.TryGetValue(session, out var dialogs))
            _permissionDialogs[session] = dialogs = new();
        dialogs.Add(dialog);
        dialog.Destroyed += (_, _) => dialogs.Remove(dialog);
        ShowChildWindow(dialog);
    }

    private void OnNotification(AccountSession session, AccountNotification notice)
    {
        if (!_sessions.TryGetValue(session, out var widgets) || !session.IsConnected) return;
        var visible = _childWindows.HasFocus && (notice.Category == NotificationCategory.Menus ||
            (session == _selected && widgets.IsNotificationDisplayed(notice)));
        _notifications.Notify(session, notice, visible, () => ActivateNotification(session, notice));
    }

    private void ActivateNotification(AccountSession session, AccountNotification notice)
    {
        if (!_sessions.TryGetValue(session, out var widgets) || !session.IsConnected) return;
        SelectSession(session);
        widgets.OpenNotification(notice);
        // A notification click is a manual request to bring the viewer forward.
        Present();
        if (notice.Category == NotificationCategory.Menus && _scriptDialogs.TryGetValue(session, out var dialogs) &&
            dialogs.LastOrDefault(dialog => dialog.ObjectId == notice.TargetId) is { } menu) ShowChildWindow(menu);
    }
}
