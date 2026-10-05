# Radegast GTK

This is the first GTK3 implementation checkpoint for a lightweight Linux
client. It uses the native GTK theme, font, and normal window-manager borders.
It references `Radegast.Core` for grid communication and does not start the
3D renderer or the FMOD audio engine.

## Run

Install the .NET 10 SDK and the GTK3 runtime, then run from the repository root:

```sh
dotnet run --project Radegast.Gtk/Radegast.Gtk.csproj
```

The add-account window supports Second Life and the grids listed in the
repository's `grids.xml`, plus a custom login URI. An MFA challenge can be
answered in the same window. Passwords are held only for the active login
attempt; this version does not store credentials.

## Implemented in this checkpoint

- One GTK3 window with a left account rail, top tabs, and a right nearby-avatar
  list for the selected account.
- Multiple connected accounts in one process. Switching accounts preserves
  each session's selected tab and chat transcript.
- Nearby chat send/receive, and a distance-sorted list of nearby avatars from
  the current simulator's coarse-location updates.
- Account-specific logout and unread nearby-chat counts for background accounts.

The IMs, Group Chats, Inventory, Attachments, and Friends tabs currently show
placeholders. Desktop notifications and notification sounds are also pending.
The [GTK3 client plan](../docs/Gtk3ClientPlan.md) tracks the intended scope.
