using Gtk;
using LibreMetaverse;
using System.Globalization;

namespace Radegast.Gtk;

/// <summary>A payment prompt bound to its originating account and recipient.</summary>
internal sealed class ResidentPaymentWindow : Window
{
    private readonly AccountSession _session;
    private readonly UUID _recipient;
    private readonly bool _requireFriend;
    private readonly Label _recipientLabel = new() { Xalign = 0, LineWrap = true, MaxWidthChars = 40 };
    private readonly Label _balance = new() { Xalign = 0 };
    private readonly Entry _amount = new() { InputPurpose = InputPurpose.Digits, PlaceholderText = "Amount in L$", WidthChars = 16 };
    private readonly Button _pay = new("Pay");
    private readonly Label _status = new("") { Xalign = 0, LineWrap = true, MaxWidthChars = 40, NoShowAll = true };
    private string _acceptedText = string.Empty;
    private bool _filtering;
    private bool _submitted;
    private bool _closing;

    public event System.Action? Closed;

    public ResidentPaymentWindow(Window parent, AccountSession session, UUID recipient, bool requireFriend = false) : base($"Pay — {session.Name}")
    {
        _session = session;
        _recipient = recipient;
        _requireFriend = requireFriend;
        TransientFor = parent;
        DestroyWithParent = true;
        WindowPosition = WindowPosition.CenterOnParent;
        Resizable = false;
        DeleteEvent += (_, args) => { args.RetVal = true; ClosePayment(); };
        Destroyed += (_, _) => Stop();
        var content = new Box(Orientation.Vertical, 10) { BorderWidth = 12 };
        Add(content);
        content.PackStart(_recipientLabel, false, false, 0);
        content.PackStart(_balance, false, false, 0);
        content.PackStart(_amount, false, false, 0);
        content.PackStart(_pay, false, false, 0);
        var dismiss = new Button("Dismiss");
        dismiss.Clicked += (_, _) => ClosePayment();
        content.PackStart(dismiss, false, false, 0);
        content.PackStart(_status, false, false, 0);
        _amount.Changed += (_, _) => OnAmountChanged();
        _pay.Clicked += (_, _) => Pay();
        Shown += (_, _) => _amount.GrabFocus();
        session.StateChanged += OnSessionChanged;
        session.FriendsChanged += OnSessionChanged;
        session.AvatarNamesChanged += OnSessionChanged;
        session.Rlv.Changed += UpdatePresentation;
        UpdatePresentation();
    }

    private void OnAmountChanged()
    {
        if (_filtering || _closing) return;
        var text = _amount.Text;
        if (text.Any(character => character is < '0' or > '9'))
        {
            // Reject the edit rather than turning a pasted amount like "1.5"
            // into a different payment ("15"). Overflow stays visible but invalid.
            _filtering = true;
            var position = _amount.Position;
            _amount.Text = _acceptedText;
            _amount.Position = Math.Min(position, _acceptedText.Length);
            _filtering = false;
        }
        else _acceptedText = text;
        _status.Hide();
        UpdatePresentation();
    }

    private void OnSessionChanged(AccountSession session)
    {
        if (_closing) return;
        if (!CanPay) { ClosePayment(); return; }
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        if (_closing) return;
        _recipientLabel.Text = $"Pay {_session.DisplayChatAvatarName(_recipient)}";
        _balance.Text = _session.Balance is { } balance
            ? $"Your balance: {balance.ToString("N0", CultureInfo.InvariantCulture)} L$" : "Your balance: … L$";
        var valid = AccountSession.TryParsePaymentAmount(_amount.Text, out var amount);
        var affordable = _session.Balance is not { } available || amount <= available;
        _pay.Sensitive = !_submitted && CanPay && valid && affordable;
        _pay.TooltipText = valid && !affordable ? "This amount exceeds your current balance." : "Pay this amount to the named resident";
    }

    private bool CanPay => _requireFriend ? _session.CanPayFriend(_recipient) : _session.CanPayResident(_recipient);

    private void Pay()
    {
        if (_closing || _submitted || !_pay.Sensitive || !AccountSession.TryParsePaymentAmount(_amount.Text, out var amount)) return;
        _submitted = true;
        _pay.Sensitive = false;
        try
        {
            if (_requireFriend) _session.PayFriend(_recipient, amount);
            else _session.PayResident(_recipient, amount);
        }
        catch (Exception ex)
        {
            _submitted = false;
            _status.Text = $"Payment failed: {ex.Message}";
            _status.Show();
            UpdatePresentation();
            return;
        }
        ClosePayment();
    }

    public void ClosePayment()
    {
        if (_closing) return;
        Stop();
        Dispose();
    }

    private void Stop()
    {
        if (_closing) return;
        _closing = true;
        _session.StateChanged -= OnSessionChanged;
        _session.FriendsChanged -= OnSessionChanged;
        _session.AvatarNamesChanged -= OnSessionChanged;
        _session.Rlv.Changed -= UpdatePresentation;
        Closed?.Invoke();
    }
}
