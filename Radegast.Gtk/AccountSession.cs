using LibreMetaverse;
using LibreMetaverse.Appearance;
using Radegast;

namespace Radegast.Gtk;

internal sealed record NearbyResident(UUID Id, string Name, int Distance);
internal sealed record ScriptMenu(UUID ObjectId, string ObjectName, string OwnerName,
    string Message, int Channel, IReadOnlyList<string> Buttons);

/// <summary>A single grid connection. All public events are delivered on the GTK thread.</summary>
internal sealed class AccountSession : IDisposable
{
    private readonly Dictionary<UUID, string> _names = new();
    private readonly HashSet<UUID> _requestedNames = new();
    private readonly object _nameLock = new();
    private List<NearbyResident> _nearby = new();
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public GridClient Client { get; } = new();
    public CurrentOutfitFolder Outfit { get; }
    public NetCom Net { get; }
    public string Name { get; private set; } = "Connecting…";
    public string Status { get; private set; } = "Connecting…";
    public bool IsConnected => Net.IsLoggedIn;
    public IReadOnlyList<NearbyResident> Nearby => _nearby;

    public event Action<AccountSession, LoginStatus, string, string>? LoginProgress;
    public event Action<AccountSession>? StateChanged;
    public event Action<AccountSession, string>? ChatLine;
    public event Action<AccountSession>? NearbyChanged;
    public event Action<AccountSession, ScriptMenu>? ScriptDialogReceived;

    public AccountSession()
    {
        Outfit = new CurrentOutfitFolder(Client);
        Net = new NetCom(Client);
        Net.ClientLoginStatus += OnLoginProgress;
        Net.ClientDisconnected += OnDisconnected;
        Net.ClientLoggedOut += OnLoggedOut;
        Net.ChatReceived += OnChatReceived;
        Client.Self.ScriptDialog += OnScriptDialog;
        Client.Grid.CoarseLocationUpdate += OnCoarseLocationUpdate;
        Client.Avatars.UUIDNameReply += OnNameReply;
        Client.Network.RegisterLoginResponseCallback(OnLoginResponse);
    }

    public void Login(string username, string password, Grid grid, StartLocationType startLocation, string mfaToken = "")
    {
        var parts = username.Trim().Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Enter an account name.", nameof(username));

        Net.LoginOptions.FirstName = parts[0];
        Net.LoginOptions.LastName = parts.Length > 1 ? parts[1] : "Resident";
        Net.LoginOptions.Password = password;
        Net.LoginOptions.Grid = grid;
        Net.LoginOptions.Channel = "Radegast GTK";
        Net.LoginOptions.Version = "Radegast GTK 0.1";
        Net.LoginOptions.StartLocation = startLocation;
        Net.LoginOptions.MfaToken = mfaToken;
        Net.LoginOptions.MfaHash ??= string.Empty;
        Net.AgreeToTos = true;
        Name = Net.LoginOptions.FullName;
        Status = "Connecting…";
        StateChanged?.Invoke(this);
        Net.Login();
    }

    public void SendNearbyChat(string message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(message)) return;
        Net.ChatOut(message, ChatType.Normal, 0);
    }

    public void ReplyToScriptDialog(ScriptMenu menu, int buttonIndex, string label)
    {
        if (!IsConnected) throw new InvalidOperationException("This account is disconnected.");
        Client.Self.ReplyToScriptDialog(menu.Channel, buttonIndex, label, menu.ObjectId);
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        var owner = string.Join(" ", new[] { e.FirstName, e.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var menu = new ScriptMenu(e.ObjectID, e.ObjectName, owner, e.Message,
            e.Channel, e.ButtonLabels.ToArray());
        GtkDispatch.Post(() =>
        {
            if (!_disposed) ScriptDialogReceived?.Invoke(this, menu);
        });
    }

    private void OnLoginProgress(object? sender, LoginProgressEventArgs e)
    {
        var status = e.Status;
        var message = e.Message;
        var reason = e.FailReason;
        GtkDispatch.Post(() =>
        {
            if (_disposed) return;
            Status = status == LoginStatus.Success ? "Connected" :
                status == LoginStatus.Failed ? "Login failed" : message;
            if (status == LoginStatus.Success)
                Name = Client.Self.Name;
            StateChanged?.Invoke(this);
            LoginProgress?.Invoke(this, status, message, reason);
        });
    }

    private void OnLoginResponse(bool success, bool redirect, string message, string reason, LoginResponseData? reply)
    {
        if ((success || reason == "mfa_challenge") && reply != null)
            Net.LoginOptions.MfaHash = reply.MfaHash;
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e) =>
        GtkDispatch.Post(() => SetDisconnected("Disconnected"));

    private void OnLoggedOut(object? sender, EventArgs e) =>
        GtkDispatch.Post(() => SetDisconnected("Logged out"));

    private void SetDisconnected(string status)
    {
        if (_disposed) return;
        Status = status;
        _nearby.Clear();
        StateChanged?.Invoke(this);
        NearbyChanged?.Invoke(this);
    }

    private void OnChatReceived(object? sender, ChatEventArgs e)
    {
        if (e.Message == null || e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping)
            return;
        var from = string.IsNullOrWhiteSpace(e.FromName) ? "System" : e.FromName;
        var line = $"[{DateTime.Now:HH:mm}] {from}: {e.Message}";
        GtkDispatch.Post(() =>
        {
            if (!_disposed) ChatLine?.Invoke(this, line);
        });
    }

    private void OnCoarseLocationUpdate(object? sender, CoarseLocationUpdateEventArgs e)
    {
        if (e.Simulator != Client.Network.CurrentSim) return;

        // Copy the network event's mutable data before posting it to GTK.
        var positions = e.Positions.ToArray();
        var selfId = Client.Self.AgentID;
        var selfPosition = e.Positions.TryGetValue(selfId, out var coarseSelf)
            ? coarseSelf : Client.Self.SimPosition;
        var missing = new List<UUID>();
        List<NearbyResident> nearby;

        lock (_nameLock)
        {
            nearby = new List<NearbyResident>(positions.Length);
            foreach (var (id, position) in positions)
            {
                if (id == selfId) continue;
                if (!_names.TryGetValue(id, out var name))
                {
                    name = id.ToString()[..8];
                    if (_requestedNames.Add(id)) missing.Add(id);
                }
                var dx = position.X - selfPosition.X;
                var dy = position.Y - selfPosition.Y;
                var dz = position.Z - selfPosition.Z;
                var distance = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
                nearby.Add(new NearbyResident(id, name, distance));
            }
        }

        nearby.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        GtkDispatch.Post(() =>
        {
            if (_disposed) return;
            _nearby = nearby;
            NearbyChanged?.Invoke(this);
        });

        if (missing.Count > 0)
            Client.Avatars.RequestAvatarNames(missing);
    }

    private void OnNameReply(object? sender, UUIDNameReplyEventArgs e)
    {
        var resolved = e.Names.ToArray();
        GtkDispatch.Post(() =>
        {
            if (_disposed) return;
            lock (_nameLock)
            {
                foreach (var (id, name) in resolved)
                    _names[id] = name;
                _nearby = _nearby.Select(person =>
                    _names.TryGetValue(person.Id, out var name)
                        ? person with { Name = name } : person).ToList();
            }
            NearbyChanged?.Invoke(this);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Net.ClientLoginStatus -= OnLoginProgress;
        Net.ClientDisconnected -= OnDisconnected;
        Net.ClientLoggedOut -= OnLoggedOut;
        Net.ChatReceived -= OnChatReceived;
        Client.Self.ScriptDialog -= OnScriptDialog;
        Client.Grid.CoarseLocationUpdate -= OnCoarseLocationUpdate;
        Client.Avatars.UUIDNameReply -= OnNameReply;
        Client.Network.UnregisterLoginResponseCallback(OnLoginResponse);
        Outfit.Dispose();
        if (Net.IsLoggingIn) Net.CancelLogin();
        if (Net.IsLoggedIn) Net.Logout();
        Net.Dispose();
    }
}
