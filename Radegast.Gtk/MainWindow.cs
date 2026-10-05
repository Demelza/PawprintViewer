using Gtk;

namespace Radegast.Gtk;

internal sealed class MainWindow : Window
{
    private readonly Box _accountRows = new(Orientation.Vertical, 4);
    private readonly Stack _pages = new();
    private readonly Dictionary<AccountSession, SessionWidgets> _sessions = new();
    private AccountSession? _selected;
    private LoginWindow? _loginWindow;

    public MainWindow() : base("Radegast GTK")
    {
        SetDefaultSize(1120, 720);
        DeleteEvent += (_, _) =>
        {
            foreach (var session in _sessions.Keys.ToArray())
                RemoveSession(session);
            _loginWindow?.Destroy();
            Application.Quit();
        };

        var root = new Box(Orientation.Horizontal, 0);
        Add(root);

        var rail = new Box(Orientation.Vertical, 6) { BorderWidth = 6 };
        rail.SetSizeRequest(185, -1);
        var accountsHeading = new Label("Accounts") { Xalign = 0 };
        rail.PackStart(accountsHeading, false, false, 4);
        var accountScroll = new ScrolledWindow();
        accountScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        accountScroll.Add(_accountRows);
        rail.PackStart(accountScroll, true, true, 0);
        var addButton = new Button("+ Add account");
        addButton.Clicked += (_, _) => ShowLogin();
        rail.PackStart(addButton, false, false, 0);
        root.PackStart(rail, false, false, 0);
        root.PackStart(new Separator(Orientation.Vertical), false, false, 0);

        _pages.AddNamed(new Label("Use + Add account to log in."), "empty");
        _pages.VisibleChildName = "empty";
        root.PackStart(_pages, true, true, 0);
    }

    private void ShowLogin()
    {
        if (_loginWindow != null)
        {
            _loginWindow.Present();
            return;
        }

        _loginWindow = new LoginWindow(this);
        _loginWindow.LoginSucceeded += AddSession;
        _loginWindow.Destroyed += (_, _) => _loginWindow = null;
        _loginWindow.Show();
    }

    private void AddSession(AccountSession session)
    {
        var widgets = new SessionWidgets(session);
        _sessions.Add(session, widgets);
        _accountRows.PackStart(widgets.AccountRow, false, false, 0);
        _pages.AddNamed(widgets.Root, session.Id);
        widgets.AccountButton.Clicked += (_, _) => SelectSession(session);
        widgets.LogoutButton.Clicked += (_, _) => RemoveSession(session);
        session.StateChanged += OnStateChanged;
        session.ChatLine += OnChatLine;
        session.NearbyChanged += OnNearbyChanged;
        widgets.AccountRow.ShowAll();
        widgets.Root.ShowAll();
        SelectSession(session);
    }

    private void SelectSession(AccountSession session)
    {
        if (!_sessions.TryGetValue(session, out var widgets)) return;
        _selected = session;
        _pages.VisibleChildName = session.Id;
        widgets.UnreadCount = 0;
        foreach (var (account, view) in _sessions)
            view.UpdateAccountLabel(account == session);
        Title = $"{session.Name} — Radegast GTK";
    }

    private void RemoveSession(AccountSession session)
    {
        if (!_sessions.Remove(session, out var widgets)) return;
        session.StateChanged -= OnStateChanged;
        session.ChatLine -= OnChatLine;
        session.NearbyChanged -= OnNearbyChanged;
        widgets.Dispose();
        _accountRows.Remove(widgets.AccountRow);
        _pages.Remove(widgets.Root);
        session.Dispose();

        if (_selected == session)
        {
            _selected = null;
            var next = _sessions.Keys.FirstOrDefault();
            if (next != null) SelectSession(next);
            else
            {
                _pages.VisibleChildName = "empty";
                Title = "Radegast GTK";
            }
        }
    }

    private void OnStateChanged(AccountSession session)
    {
        if (!_sessions.TryGetValue(session, out var widgets)) return;
        widgets.UpdateAccountLabel(session == _selected);
        if (session == _selected) Title = $"{session.Name} — Radegast GTK";
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

    private void OnNearbyChanged(AccountSession session)
    {
        if (_sessions.TryGetValue(session, out var widgets))
            widgets.RefreshNearby();
    }
}
