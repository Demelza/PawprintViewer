using Gtk;
using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed class ScriptPermissionWindow : Window
{
    private readonly AccountSession _session;
    private readonly ScriptQuestionEventArgs _request;
    private bool _closed;

    public ScriptPermissionWindow(Window parent, AccountSession session, ScriptQuestionEventArgs request)
        : base($"Permissions — {session.Name}")
    {
        _session = session;
        _request = request;
        TransientFor = parent;
        DestroyWithParent = true;
        Resizable = false;
        WindowPosition = WindowPosition.CenterOnParent;
        DeleteEvent += (_, args) => { args.RetVal = true; ClosePrompt(); };
        var content = new Box(Orientation.Vertical, 10) { BorderWidth = 12 };
        Add(content);
        content.PackStart(new Label(session.RedactText($"{request.ObjectName} requests:\n\n{request.Questions.ToString().Replace(", ", "\n")}"))
        { Xalign = 0, LineWrap = true, MaxWidthChars = 48 }, false, false, 0);
        var buttons = new Box(Orientation.Horizontal, 6);
        var allow = new Button("Allow");
        var deny = new Button("Deny");
        allow.Clicked += (_, _) => Respond(request.Questions);
        deny.Clicked += (_, _) => ClosePrompt();
        buttons.PackStart(allow, true, true, 0);
        buttons.PackStart(deny, true, true, 0);
        content.PackStart(buttons, false, false, 0);
    }

    private void Respond(ScriptPermission permissions)
    {
        if (_closed) return;
        _closed = true;
        if (_session.IsConnected)
        {
            if (_session.Rlv.Enabled && _session.Rlv.Service.Permissions.IsAutoDenyPermissions()) permissions = ScriptPermission.None;
            _session.Client.Self.ScriptQuestionReply(_request.Simulator, _request.ItemID, _request.TaskID, permissions);
        }
        Dispose();
    }

    public void ClosePrompt() => Respond(ScriptPermission.None);
}
