using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed partial class AccountSession
{
    public event Action<AccountSession, AccountNotification>? NotificationReceived;

    private void Notify(NotificationCategory category, string title, string message, UUID target)
    {
        if (!_disposed) NotificationReceived?.Invoke(this, new(category, RedactText(title), RedactText(message), target));
    }

    private bool IsWornObjectMessage(ChatEventArgs chat)
    {
        if (chat.SourceType != ChatSourceType.Object || chat.Type is not (ChatType.OwnerSay or ChatType.RegionSayTo) ||
            chat.SourceID == UUID.Zero || chat.OwnerID != Client.Self.AgentID ||
            chat.Simulator != Client.Network.CurrentSim || Client.Self.LocalID == 0 || chat.Message.StartsWith('@')) return false;
        var prim = chat.Simulator.ObjectsPrimitives.Values.FirstOrDefault(prim => prim.ID == chat.SourceID);
        var seen = new HashSet<uint>();
        while (prim != null && prim.ParentID != Client.Self.LocalID && prim.ParentID != 0 && seen.Add(prim.LocalID))
            prim = chat.Simulator.ObjectsPrimitives.TryGetValue(prim.ParentID, out var parent) ? parent : null;
        return prim is { IsAttachment: true } && prim.ParentID == Client.Self.LocalID;
    }
}
