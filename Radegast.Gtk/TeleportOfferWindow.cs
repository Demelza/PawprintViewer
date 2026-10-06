using Gtk;

namespace Radegast.Gtk;

/// <summary>A teleport decision bound to the account and lure that received it.</summary>
internal sealed class TeleportOfferWindow : Window
{
    private readonly AccountSession _session;
    private readonly TeleportOffer _offer;
    private readonly Label _message = new() { Xalign = 0, LineWrap = true, MaxWidthChars = 48 };
    private readonly Label _status = new() { Xalign = 0, LineWrap = true, MaxWidthChars = 48, NoShowAll = true };
    private readonly Button _accept = new("Accept");
    private readonly Button _refuse = new("Refuse");
    private bool _closing, _responding;

    public TeleportOfferWindow(Window parent, AccountSession session, TeleportOffer offer)
        : base($"Teleport offer — {session.Name}")
    {
        _session = session;
        _offer = offer;
        TransientFor = parent;
        DestroyWithParent = true;
        WindowPosition = WindowPosition.CenterOnParent;
        Resizable = false;
        DeleteEvent += (_, args) => { args.RetVal = true; Respond(false); };
        var content = new Box(Orientation.Vertical, 10) { BorderWidth = 12 };
        Add(content);
        content.PackStart(new Label($"For {session.Name}") { Xalign = 0 }, false, false, 0);
        _message.SetSizeRequest(310, -1);
        content.PackStart(_message, false, false, 0);
        var buttons = new Box(Orientation.Horizontal, 6);
        buttons.PackStart(_accept, true, true, 0);
        buttons.PackStart(_refuse, true, true, 0);
        content.PackStart(buttons, false, false, 0);
        content.PackStart(_status, false, false, 0);
        _accept.Clicked += (_, _) => Respond(true);
        _refuse.Clicked += (_, _) => Respond(false);
        session.StateChanged += OnSessionChanged;
        session.TeleportOffersChanged += OnSessionChanged;
        Destroyed += (_, _) => Unsubscribe();
        UpdatePresentation();
    }

    private void OnSessionChanged(AccountSession session) => UpdatePresentation();

    private void UpdatePresentation()
    {
        if (_closing) return;
        if (!_session.IsConnected || !_session.IsTeleportOfferPending(_offer)) { ClosePrompt(); return; }
        _message.Text = _session.RedactText($"{_session.DisplayFriendName(_offer.SenderId)} wants to teleport you to their location.");
        var error = _session.TeleportOfferError(_offer);
        _accept.Sensitive = !_responding && error == null;
        _refuse.Sensitive = !_responding;
        _accept.TooltipText = error;
        _status.Text = error ?? string.Empty;
        if (error == null) _status.Hide(); else _status.Show();
    }

    private void Respond(bool accept)
    {
        if (_closing || _responding) return;
        _responding = true;
        try { _session.RespondToTeleportOffer(_offer, accept); }
        catch (InvalidOperationException ex)
        {
            _responding = false;
            UpdatePresentation();
            if (_closing) return;
            _status.Text = ex.Message;
            _status.Show();
            return;
        }
        ClosePrompt();
    }

    private void Unsubscribe()
    {
        _session.StateChanged -= OnSessionChanged;
        _session.TeleportOffersChanged -= OnSessionChanged;
    }

    public void ClosePrompt()
    {
        if (_closing) return;
        _closing = true;
        Unsubscribe();
        Dispose();
    }
}
