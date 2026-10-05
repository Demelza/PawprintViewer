using Gtk;
using LibreMetaverse;
using Radegast;
using GridDefinition = Radegast.Grid;

namespace Radegast.Gtk;

internal sealed class LoginWindow : Window
{
    private readonly List<GridDefinition> _grids = new();
    private readonly Entry _username = new() { PlaceholderText = "First Last or username" };
    private readonly Entry _password = new() { PlaceholderText = "Password", Visibility = false };
    private readonly Entry _uri = new() { PlaceholderText = "https://…/login.cgi" };
    private readonly Entry _mfa = new() { PlaceholderText = "Authenticator code" };
    private readonly ComboBoxText _gridChoice = new();
    private readonly ComboBoxText _locationChoice = new();
    private readonly Label _status = new("Enter an account and password.") { Xalign = 0 };
    private readonly Button _loginButton = new("Log in");
    private readonly Box _uriRow = new(Orientation.Horizontal, 6);
    private readonly Box _mfaRow = new(Orientation.Horizontal, 6);
    private AccountSession? _pending;
    private bool _mfaRequired;

    public event Action<AccountSession>? LoginSucceeded;

    public LoginWindow(Window parent) : base("Add account")
    {
        TransientFor = parent;
        Modal = true;
        DestroyWithParent = true;
        SetDefaultSize(430, 280);
        Destroyed += (_, _) =>
        {
            _pending?.Dispose();
            _pending = null;
        };

        using (var manager = new GridManager())
        {
            manager.LoadGrids();
            _grids.AddRange(manager.Grids);
        }
        foreach (var grid in _grids) _gridChoice.AppendText(grid.Name);
        _gridChoice.AppendText("Custom login URI");
        _gridChoice.Active = 0;
        _gridChoice.Changed += (_, _) => UpdateGridRow();

        _locationChoice.AppendText("Last location");
        _locationChoice.AppendText("Home");
        _locationChoice.Active = 0;

        var outer = new Box(Orientation.Vertical, 10) { BorderWidth = 14 };
        Add(outer);
        var form = new global::Gtk.Grid { RowSpacing = 8, ColumnSpacing = 10 };
        outer.PackStart(form, false, false, 0);
        AddField(form, "Account", _username, 0);
        AddField(form, "Password", _password, 1);
        AddField(form, "Grid", _gridChoice, 2);
        AddField(form, "Start at", _locationChoice, 3);

        _uriRow.PackStart(new Label("Login URI") { Xalign = 0 }, false, false, 0);
        _uriRow.PackStart(_uri, true, true, 0);
        outer.PackStart(_uriRow, false, false, 0);
        _mfaRow.PackStart(new Label("MFA code") { Xalign = 0 }, false, false, 0);
        _mfaRow.PackStart(_mfa, true, true, 0);
        outer.PackStart(_mfaRow, false, false, 0);
        outer.PackStart(_status, false, false, 0);

        var actions = new Box(Orientation.Horizontal, 8);
        var cancel = new Button("Cancel");
        cancel.Clicked += (_, _) => Destroy();
        _loginButton.Clicked += (_, _) => BeginLogin();
        _password.Activated += (_, _) => BeginLogin();
        _mfa.Activated += (_, _) => BeginLogin();
        actions.PackEnd(_loginButton, false, false, 0);
        actions.PackEnd(cancel, false, false, 0);
        outer.PackEnd(actions, false, false, 0);

        ShowAll();
        UpdateGridRow();
        _mfaRow.Hide();
        _username.GrabFocus();
    }

    private static void AddField(global::Gtk.Grid form, string title, Widget field, int row)
    {
        var label = new Label(title) { Xalign = 0 };
        form.Attach(label, 0, row, 1, 1);
        form.Attach(field, 1, row, 1, 1);
    }

    private void UpdateGridRow()
    {
        if (_gridChoice.Active == _grids.Count) _uriRow.ShowAll();
        else _uriRow.Hide();
    }

    private void BeginLogin()
    {
        var username = _username.Text.Trim();
        var password = _password.Text;
        if (username.Length == 0 || password.Length == 0)
        {
            _status.Text = "Enter an account and password.";
            return;
        }

        GridDefinition grid;
        if (_gridChoice.Active == _grids.Count)
        {
            if (!Uri.TryCreate(_uri.Text.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                _status.Text = "Enter a valid HTTP or HTTPS login URI.";
                return;
            }
            grid = new GridDefinition("custom", "Custom", uri.ToString());
        }
        else if (_gridChoice.Active >= 0 && _gridChoice.Active < _grids.Count)
        {
            grid = _grids[_gridChoice.Active];
        }
        else
        {
            _status.Text = "Choose a grid.";
            return;
        }

        if (_mfaRequired && string.IsNullOrWhiteSpace(_mfa.Text))
        {
            _status.Text = "Enter your authenticator code.";
            return;
        }

        _pending ??= new AccountSession();
        _pending.LoginProgress -= OnLoginProgress;
        _pending.LoginProgress += OnLoginProgress;
        _loginButton.Sensitive = false;
        _status.Text = _mfaRequired ? "Verifying code…" : "Logging in…";

        try
        {
            var start = _locationChoice.Active == 1 ? StartLocationType.Home : StartLocationType.Last;
            _pending.Login(username, password, grid, start, _mfaRequired ? _mfa.Text.Trim() : "");
        }
        catch (Exception ex)
        {
            _loginButton.Sensitive = true;
            _status.Text = ex.Message;
        }
    }

    private void OnLoginProgress(AccountSession session, LoginStatus status, string message, string reason)
    {
        if (status == LoginStatus.Success)
        {
            _pending = null;
            LoginSucceeded?.Invoke(session);
            Destroy();
            return;
        }

        if (status == LoginStatus.Failed && reason == "mfa_challenge")
        {
            _mfaRequired = true;
            _mfa.Text = string.Empty;
            _mfaRow.ShowAll();
            _mfa.GrabFocus();
            _status.Text = "Enter your authenticator code.";
            _loginButton.Sensitive = true;
            return;
        }

        _status.Text = status == LoginStatus.Failed ? $"Login failed: {message}" : message;
        if (status == LoginStatus.Failed) _loginButton.Sensitive = true;
    }
}
