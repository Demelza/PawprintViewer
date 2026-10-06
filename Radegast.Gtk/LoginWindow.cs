using Gtk;
using LibreMetaverse;
using Radegast;
using GridDefinition = Radegast.Grid;

namespace Radegast.Gtk;

internal sealed class LoginWindow : Window
{
    private readonly List<GridDefinition> _grids = new();
    private readonly ComboBoxText _accountChoice = ComboBoxText.NewWithEntry();
    private readonly Entry _username;
    private readonly SavedLoginStore _savedLogins;
    private IReadOnlyList<SavedLogin> _remembered = Array.Empty<SavedLogin>();
    private readonly Entry _password = new() { PlaceholderText = "Password", Visibility = false };
    private readonly Entry _uri = new() { PlaceholderText = "https://…/login.cgi" };
    private readonly Entry _mfa = new() { PlaceholderText = "Authenticator code" };
    private readonly ComboBoxText _gridChoice = new();
    private readonly ComboBoxText _locationChoice = new();
    private readonly Label _status = new("Enter an account and password.") { Xalign = 0, LineWrap = true, MaxWidthChars = 48 };
    private readonly Button _loginButton = new("Log in");
    private readonly Box _uriRow = new(Orientation.Horizontal, 6);
    private readonly Box _mfaRow = new(Orientation.Horizontal, 6);
    private AccountSession? _pending;
    private bool _mfaRequired;
    private bool _updatingAccount, _applyingPassword, _closed;
    private string _identity = string.Empty;
    private CancellationTokenSource? _passwordLookup;
    private (string Name, string Uri, string Password)? _submittedCredentials;

    public event Action<AccountSession>? LoginSucceeded;
    public event Action<AccountSession, string>? CredentialsWarning;

    public LoginWindow(Window parent, SavedLoginStore? savedLogins = null) : base("Add account")
    {
        _savedLogins = savedLogins ?? new SavedLoginStore();
        _username = _accountChoice.Entry;
        _username.PlaceholderText = "First Last or username";
        TransientFor = parent;
        Modal = true;
        DestroyWithParent = true;
        SetDefaultSize(430, 280);
        Destroyed += (_, _) =>
        {
            _closed = true;
            CancelPasswordLookup();
            _submittedCredentials = null;
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
        _gridChoice.Changed += (_, _) => { UpdateGridRow(); RefreshRememberedAccounts(); };
        _uri.Changed += (_, _) => RefreshRememberedAccounts();
        _username.Changed += (_, _) => OnIdentityChanged();
        _accountChoice.Changed += (_, _) => SelectRememberedAccount();
        _password.Changed += (_, _) =>
        {
            if (_applyingPassword || _passwordLookup == null) return;
            CancelPasswordLookup();
            _status.Text = "Enter or select an account and password.";
        };

        _locationChoice.AppendText("Last location");
        _locationChoice.AppendText("Home");
        _locationChoice.Active = 0;

        var outer = new Box(Orientation.Vertical, 10) { BorderWidth = 14 };
        Add(outer);
        var form = new global::Gtk.Grid { RowSpacing = 8, ColumnSpacing = 10 };
        outer.PackStart(form, false, false, 0);
        AddField(form, "Account", _accountChoice, 0);
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

        outer.ShowAll();
        UpdateGridRow();
        RefreshRememberedAccounts();
        _status.Text = _savedLogins.LoadError ?? "Enter or select an account and password.";
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

    private string SelectedLoginUri => _gridChoice.Active >= 0 && _gridChoice.Active < _grids.Count
        ? _grids[_gridChoice.Active].LoginURI : _uri.Text.Trim();

    private void RefreshRememberedAccounts()
    {
        if (_closed || _updatingAccount) return;
        var name = _username.Text;
        _updatingAccount = true;
        _remembered = _savedLogins.ForGrid(SelectedLoginUri);
        _accountChoice.RemoveAll();
        foreach (var account in _remembered) _accountChoice.AppendText(account.AccountName);
        _accountChoice.Active = -1;
        _username.Text = name;
        _updatingAccount = false;
        OnIdentityChanged();
        if (SavedLogin.NormalizeName(name).Length == 0) return;
        var match = _remembered.Select((account, index) => (account, index))
            .FirstOrDefault(pair => SavedLogin.NormalizeName(pair.account.AccountName) == SavedLogin.NormalizeName(name));
        if (match.account != null) _accountChoice.Active = match.index;
    }

    private void OnIdentityChanged()
    {
        if (_closed || _updatingAccount) return;
        var identity = SavedLogin.NormalizeUri(SelectedLoginUri) + "\n" + SavedLogin.NormalizeName(_username.Text);
        if (_identity == identity) return;
        _identity = identity;
        CancelPasswordLookup();
        _password.Text = string.Empty;
        _mfaRequired = false;
        if (_pending != null) _pending.Net.LoginOptions.MfaHash = string.Empty;
        _mfa.Text = string.Empty;
        _mfaRow.Hide();
        _status.Text = "Enter or select an account and password.";
    }

    private async void SelectRememberedAccount()
    {
        if (_closed || _updatingAccount || _accountChoice.Active < 0 || _accountChoice.Active >= _remembered.Count) return;
        var account = _remembered[_accountChoice.Active];
        _updatingAccount = true;
        _username.Text = account.AccountName;
        _updatingAccount = false;
        OnIdentityChanged();
        CancelPasswordLookup();
        _applyingPassword = true;
        _password.Text = string.Empty;
        _applyingPassword = false;
        var lookup = _passwordLookup = new CancellationTokenSource();
        var identity = _identity;
        _status.Text = "Loading saved password…";
        try
        {
            var password = await _savedLogins.LookupPasswordAsync(account, lookup.Token).ConfigureAwait(false);
            GtkDispatch.Post(() =>
            {
                if (_closed || lookup.IsCancellationRequested || _passwordLookup != lookup || _identity != identity) return;
                _applyingPassword = true;
                _password.Text = password ?? string.Empty;
                _applyingPassword = false;
                _status.Text = password == null ? "No saved password. Enter it to log in." : "Saved password loaded.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GtkDispatch.Post(() =>
            {
                if (!_closed && !lookup.IsCancellationRequested && _passwordLookup == lookup && _identity == identity)
                    _status.Text = $"Could not load the saved password. Enter it manually. {ex.Message}";
            });
        }
    }

    private void CancelPasswordLookup()
    {
        _passwordLookup?.Cancel();
        _passwordLookup?.Dispose();
        _passwordLookup = null;
    }

    private void SetLoginBusy(bool busy)
    {
        _loginButton.Sensitive = !busy;
        _accountChoice.Sensitive = _password.Sensitive = _gridChoice.Sensitive = _uri.Sensitive = !busy;
        _locationChoice.Sensitive = _mfa.Sensitive = !busy;
    }

    private void BeginLogin()
    {
        if (_closed || !_loginButton.Sensitive) return;
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
        CancelPasswordLookup();
        _submittedCredentials = (username, grid.LoginURI, password);
        SetLoginBusy(true);
        _status.Text = _mfaRequired ? "Verifying code…" : "Logging in…";

        try
        {
            var start = _locationChoice.Active == 1 ? StartLocationType.Home : StartLocationType.Last;
            _pending.Login(username, password, grid, start, _mfaRequired ? _mfa.Text.Trim() : "");
        }
        catch (Exception ex)
        {
            SetLoginBusy(false);
            _status.Text = ex.Message;
        }
    }

    private void OnLoginProgress(AccountSession session, LoginStatus status, string message, string reason)
    {
        if (status == LoginStatus.Success)
        {
            session.LoginProgress -= OnLoginProgress;
            var saving = _submittedCredentials is { } credentials
                ? _savedLogins.RememberAsync(credentials.Name, credentials.Uri, credentials.Password) : Task.CompletedTask;
            _submittedCredentials = null;
            _pending = null;
            LoginSucceeded?.Invoke(session);
            Destroy();
            _ = ReportSaveResultAsync(session, saving);
            return;
        }

        if (status == LoginStatus.Failed && reason == "mfa_challenge")
        {
            _mfaRequired = true;
            _mfa.Text = string.Empty;
            _mfaRow.ShowAll();
            _mfa.GrabFocus();
            _status.Text = "Enter your authenticator code.";
            SetLoginBusy(false);
            return;
        }

        _status.Text = status == LoginStatus.Failed ? $"Login failed: {message}" : message;
        if (status == LoginStatus.Failed) SetLoginBusy(false);
    }

    private async Task ReportSaveResultAsync(AccountSession session, Task saving)
    {
        try { await saving.ConfigureAwait(false); }
        catch (Exception ex)
        {
            GtkDispatch.Post(() => CredentialsWarning?.Invoke(session,
                $"[{DateTime.Now:HH:mm}] Logged in, but credentials could not be fully remembered: {ex.Message}"));
        }
    }
}
