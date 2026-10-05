using Gtk;

namespace Radegast.Gtk;

/// <summary>A separate window for an in-world llDialog or llTextBox menu.</summary>
internal sealed class ScriptDialogWindow : Window
{
    private readonly AccountSession _session;
    private readonly ScriptMenu _menu;
    private readonly Label _status = new("") { Xalign = 0, NoShowAll = true };

    public ScriptDialogWindow(Window parent, AccountSession session, ScriptMenu menu)
        : base($"{(string.IsNullOrWhiteSpace(menu.ObjectName) ? "Script menu" : menu.ObjectName)} — {session.Name}")
    {
        _session = session;
        _menu = menu;
        TransientFor = parent;
        DestroyWithParent = true;
        WindowPosition = WindowPosition.CenterOnParent;
        Resizable = false;

        var content = new Box(Orientation.Vertical, 10) { BorderWidth = 12 };
        Add(content);

        var objectName = string.IsNullOrWhiteSpace(menu.ObjectName) ? "Scripted object" : menu.ObjectName;
        var source = string.IsNullOrWhiteSpace(menu.OwnerName)
            ? objectName : $"{objectName} · {menu.OwnerName}";
        content.PackStart(new Label(source)
        {
            Xalign = 0, Selectable = true, LineWrap = true, MaxWidthChars = 52
        }, false, false, 0);

        var message = new Label(menu.Message)
        {
            Xalign = 0,
            Yalign = 0,
            LineWrap = true,
            Selectable = true,
            MaxWidthChars = 52
        };
        message.SetSizeRequest(310, -1);
        content.PackStart(message, false, false, 0);

        if (menu.Buttons.Count == 1 && menu.Buttons[0] == "!!llTextBox!!")
        {
            var input = new Entry { PlaceholderText = "Enter a reply…" };
            content.PackStart(input, false, false, 0);
            var send = new Button("Send");
            send.Clicked += (_, _) => Reply(0, input.Text);
            input.Activated += (_, _) => Reply(0, input.Text);
            content.PackStart(send, false, false, 0);
            Shown += (_, _) => input.GrabFocus();
        }
        else if (menu.Buttons.Count > 0)
        {
            var buttons = new global::Gtk.Grid { RowSpacing = 6, ColumnSpacing = 6, ColumnHomogeneous = true };
            var rows = (menu.Buttons.Count + 2) / 3;
            for (var index = 0; index < menu.Buttons.Count; index++)
            {
                var buttonIndex = index;
                var label = menu.Buttons[index];
                var button = new Button(label);
                button.Clicked += (_, _) => Reply(buttonIndex, label);
                buttons.Attach(button, index % 3, rows - 1 - index / 3, 1, 1);
            }
            content.PackStart(buttons, false, false, 0);
        }

        var dismiss = new Button("Dismiss");
        dismiss.Clicked += (_, _) => Destroy();
        content.PackStart(dismiss, false, false, 0);
        content.PackStart(_status, false, false, 0);
        _status.Hide();
    }

    private void Reply(int index, string label)
    {
        try
        {
            _session.ReplyToScriptDialog(_menu, index, label);
            Destroy();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not send reply: {ex.Message}";
            _status.Show();
        }
    }
}
