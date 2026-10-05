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
  each session's selected tab and chat transcript. The account rail shows each
  avatar's current region and local coordinates.
- Nearby chat send/receive, and a distance-sorted list of nearby avatars from
  the current simulator's coarse-location updates.
- Account-specific logout and unread nearby-chat counts for background accounts.
- Inventory folder browsing, item details, and per-folder refresh. Folders load
  when opened. The Library is shown when the grid provides it.
- **Search all** fetches My Inventory folders on demand, then searches their
  names. Results are capped at 500 displayed matches.
- Create folders, rename ordinary items or folders, and move them to Trash.
- Worn wearables and attachments show a `(worn)` tag. Select an item to use
  **Add**, **Detach**, **Add To**, or **Add To HUD** at the bottom of Item details.
  Select the root Trash folder to use **Empty Trash**, with a confirmation prompt.
- Moving an item or folder to Trash uses the inventory move protocol and checks
  the server's inventory response before success is shown.
- The Attachments tab lists equipped objects and their attachment points, with
  a Touch button for touchable objects and a disabled button for other objects.
- Script menus and text prompts opened by touched objects appear in their own
  compact windows; button and text replies go to the correct account.
- Common system folders stay at the top of My Inventory; the Type column uses
  20% of the inventory list, and the fixed details pane uses 25% of the view.

The IMs, Group Chats, and Friends tabs currently show placeholders.
Inventory item content editors, previews, rez actions, desktop
notifications, and notification sounds are also pending.
The [GTK3 client plan](../docs/Gtk3ClientPlan.md) tracks the intended scope.
