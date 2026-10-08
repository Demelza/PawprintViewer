using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>A lightweight resident profile and actions for its originating account.</summary>
internal sealed class AvatarProfileWindow : Window
{
    private readonly AccountSession _session;
    private readonly UUID _avatar;
    private readonly CancellationTokenSource _stop = new();
    private readonly Label _name = new() { Xalign = 0, Selectable = true };
    private readonly Label _born = new() { Xalign = 0 };
    private readonly Label _status = new("Loading profile…") { Xalign = 0, LineWrap = true };
    private readonly ChatHistoryView _about;
    private readonly ChatHistoryView _firstLife;
    private readonly Button _im = new("IM");
    private readonly Button _pay = new("Pay");
    private readonly Button _teleport = new("Offer TP");
    private readonly Button _friend = new("Add Friend");
    private readonly Button _block = new("Block");
    private readonly Window _parent;
    private readonly Action<string> _openWebLink;
    private ResidentPaymentWindow? _payment;
    private UUID _paymentRecipient;
    private bool _closing, _received, _receivedCapability;

    public AvatarProfileWindow(Window parent, AccountSession session, UUID avatar, Action<string>? openWebLink = null) : base("Avatar profile")
    {
        _session = session;
        _avatar = avatar;
        _parent = parent;
        _openWebLink = openWebLink ?? ExternalLinks.Open;
        _about = new ChatHistoryView(session, followEnd: false, profileLinks: true);
        _firstLife = new ChatHistoryView(session, followEnd: false, profileLinks: true);
        _about.ProfileLinkActivated += OnProfileLink;
        _firstLife.ProfileLinkActivated += OnProfileLink;
        TransientFor = parent;
        DestroyWithParent = true;
        // Keep the action rows unchanged and add roughly half the previous
        // text area's height to the window.
        SetDefaultSize(440, 520);
        var content = new Box(Orientation.Vertical, 8) { BorderWidth = 12 };
        Add(content);
        content.PackStart(_name, false, false, 0);
        content.PackStart(_born, false, false, 0);
        var tabs = new Notebook();
        AddPage(tabs, _about, "Second Life");
        AddPage(tabs, _firstLife, "First Life");
        content.PackStart(tabs, true, true, 0);
        content.PackStart(_status, false, false, 0);
        var contact = new Box(Orientation.Horizontal, 6) { Homogeneous = true };
        contact.PackStart(_im, true, true, 0);
        contact.PackStart(_pay, true, true, 0);
        contact.PackStart(_teleport, true, true, 0);
        content.PackStart(contact, false, false, 0);
        var relationship = new Box(Orientation.Horizontal, 6) { Homogeneous = true };
        relationship.PackStart(_friend, true, true, 0);
        relationship.PackStart(_block, true, true, 0);
        content.PackStart(relationship, false, false, 0);
        _im.Clicked += (_, _) => RunAction(() => _session.RequestInstantMessages(_avatar));
        _pay.Clicked += (_, _) => RunAction(() => OpenPayment(_avatar));
        _teleport.Clicked += (_, _) => RunAction(() =>
        {
            _session.OfferTeleport(_avatar);
            _status.Text = "Teleport offer sent.";
        });
        _friend.Clicked += (_, _) => RunAction(() =>
        {
            if (_session.IsFriend(_avatar))
            {
                _session.RemoveFriend(_avatar);
                _status.Text = "Friend removed.";
            }
            else
            {
                _session.OfferFriendship(_avatar);
                _status.Text = "Friend request sent.";
            }
        });
        _block.Clicked += (_, _) => RunAction(() =>
        {
            var blocked = _session.IsResidentBlocked(_avatar);
            if (blocked) _session.UnblockResident(_avatar);
            else _session.BlockResident(_avatar);
            _status.Text = blocked ? "Resident unblocked." : "Resident blocked.";
        });
        var dismiss = new Button("Dismiss");
        dismiss.Clicked += (_, _) => CloseProfile();
        content.PackEnd(dismiss, false, false, 0);
        DeleteEvent += (_, args) => { args.RetVal = true; CloseProfile(); };
        Destroyed += (_, _) => Stop();
        _session.Client.Avatars.AvatarPropertiesReply += OnProperties;
        _session.AvatarNamesChanged += OnNamesChanged;
        _session.FriendsChanged += OnNamesChanged;
        _session.StateChanged += OnNamesChanged;
        _session.BlockListChanged += OnNamesChanged;
        _session.Rlv.Changed += RefreshPresentation;
        content.ShowAll();
        RefreshPresentation();
        if (_closing) return;
        _session.RequestChatAvatarNames(new[] { avatar });
        if (!session.IsConnected) { _status.Text = "This account is disconnected."; return; }
        try
        {
            // UDP remains available on grids or avatars without an AgentProfile capability.
            session.Client.Avatars.RequestAvatarProperties(avatar);
            if (session.Client.Avatars.AgentProfileAvailable()) _ = LoadCapabilityAsync(_stop.Token);
        }
        catch (Exception ex) { _status.Text = $"Could not load the profile: {_session.RedactText(ex.Message)}"; }
    }

    private static void AddPage(Notebook tabs, TextView text, string label)
    {
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        scroll.Add(text);
        tabs.AppendPage(scroll, new Label(label));
    }

    private void OnNamesChanged(AccountSession account) => RefreshPresentation();
    private void RefreshPresentation()
    {
        if (_closing) return;
        if (!_session.CanViewAvatarProfile(_avatar)) { CloseProfile(); return; }
        var name = _session.DisplayChatAvatarName(_avatar);
        if (_name.Text != name) _name.Text = name;
        _im.Sensitive = _session.CanOpenConversation(_avatar);
        _pay.Sensitive = _session.CanPayResident(_avatar);
        _teleport.Sensitive = _session.CanOfferTeleport(_avatar);
        var friend = _session.IsFriend(_avatar);
        _friend.Label = friend ? "Remove Friend" : "Add Friend";
        _friend.Sensitive = friend ? _session.CanInteractWithResident(_avatar) : _session.CanOfferFriendship(_avatar);
        _friend.TooltipText = _session.IsFriendshipOfferPending(_avatar) ? "Waiting for a reply to your friendship offer" : null;
        _block.Label = _session.IsResidentBlocked(_avatar) ? "Unblock" : "Block";
        _block.Sensitive = _session.CanInteractWithResident(_avatar);
    }

    private void RunAction(System.Action action)
    {
        if (_closing) return;
        try { action(); }
        catch (Exception ex) { _status.Text = _session.RedactText(ex.Message); }
        RefreshPresentation();
    }

    private void OnProfileLink(ProfileTextLink link) => RunAction(() =>
    {
        if (!_session.CanUseProfileLink(link)) return;
        if (link.Action == ProfileLinkAction.Web) _openWebLink(link.Url);
        else if (link.Action == ProfileLinkAction.PayResident) OpenPayment(link.AvatarId);
    });

    private void OpenPayment(UUID recipient)
    {
        if (!_session.CanPayResident(recipient)) return;
        if (_payment != null && _paymentRecipient != recipient) _payment.ClosePayment();
        _session.RequestChatAvatarNames(new[] { recipient });
        if (_payment == null)
        {
            _paymentRecipient = recipient;
            var payment = new ResidentPaymentWindow(this, _session, recipient);
            _payment = payment;
            payment.Closed += () => { if (_payment == payment) _payment = null; };
        }
        if (_parent is MainWindow main) main.ShowChildWindow(_payment);
        else _payment.ShowAll();
    }

    private void OnProperties(object? sender, AvatarPropertiesReplyEventArgs e)
    {
        if (e.AvatarID != _avatar) return;
        var properties = e.Properties;
        GtkDispatch.Post(() => Apply(properties.BornOn, properties.AboutText, properties.FirstLifeText));
    }

    private async Task LoadCapabilityAsync(CancellationToken token)
    {
        try
        {
            var (success, profile) = await _session.Client.Avatars.RequestAgentProfileAsync(_avatar, token).ConfigureAwait(false);
            if (success && profile != null && profile.AvatarID == _avatar)
                GtkDispatch.Post(() => Apply(profile.MemberSince.Year > 1 ? profile.MemberSince.ToShortDateString() : "",
                    profile.SecondLifeAboutText, profile.FirstLifeAboutText, complete: true));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The UDP reply can still populate the profile. */ }
    }

    private void Apply(string born, string about, string firstLife, bool complete = false)
    {
        // Legacy UDP profiles limit About text to 512 bytes and First Life to
        // a one-byte-length field. A full capability reply supersedes them,
        // regardless of which request finishes first.
        if (_closing || _receivedCapability || (_received && !complete) || !_session.CanViewAvatarProfile(_avatar)) return;
        _received = true;
        _receivedCapability = complete;
        _born.Text = string.IsNullOrWhiteSpace(born) ? string.Empty : $"Born: {born}";
        _about.SetText(about ?? string.Empty);
        _firstLife.SetText(firstLife ?? string.Empty);
        _status.Text = string.Empty;
        RefreshPresentation();
    }

    public void CloseProfile()
    {
        if (_closing) return;
        Stop();
        Dispose();
    }

    private void Stop()
    {
        if (_closing) return;
        _closing = true;
        _session.Client.Avatars.AvatarPropertiesReply -= OnProperties;
        _session.AvatarNamesChanged -= OnNamesChanged;
        _session.FriendsChanged -= OnNamesChanged;
        _session.StateChanged -= OnNamesChanged;
        _session.BlockListChanged -= OnNamesChanged;
        _session.Rlv.Changed -= RefreshPresentation;
        _about.ProfileLinkActivated -= OnProfileLink;
        _firstLife.ProfileLinkActivated -= OnProfileLink;
        _about.Stop();
        _firstLife.Stop();
        _payment?.ClosePayment();
        _stop.Cancel();
        _stop.Dispose();
    }
}
