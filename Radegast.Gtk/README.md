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
- Per-account RLV/RLVa support, with an RLV tab for enabling it, inspecting
  active restrictions, and optionally showing command diagnostics in chat.
- Script permission requests open separate Allow/Deny windows. RLV permission
  rules can deny requests or automatically accept animation, attachment, and
  control permissions; other permissions still require an explicit response.

## RLV / RLVa

RLV is enabled for each new account session. Open that account's **RLV** tab
to turn it off. Turning it off cancels queued commands and clears restrictions
immediately. Restrictions are not saved between logins. After turning it back
on, use the controlling attachment's RLV menu to have it resend its rules.

The GTK client uses the existing LibreMetaverse RLV engine for the common
[RLV protocol](https://wiki.secondlife.com/wiki/LSL_Protocol/RestrainedLoveAPI)
and [RLVa commands](https://docs.catznip.com/rlva/allinone/). Owned objects send
commands through owner-say chat; command messages are intercepted and replies
go back on the requested script channel. Each account has a separate engine,
command queue, shared inventory, and restrictions.

Supported integrations include attachment and wearable locks, shared-folder
locks, forced wear/remove actions, `#RLV` inventory and worn-state queries,
chat and emote restrictions/redirection, touch restrictions, hidden inventory,
names and location, forced sit/stand/rotation/teleport, active group changes,
teleport offer restrictions/automatic acceptance, and script permission rules.
Shared inventory loads on demand, including link targets, without opening the
Inventory tab. The shared root is the folder named exactly `#RLV` directly under
**My Inventory**. Paths in commands are relative to that folder: for example,
`@attachover:Avatar_Outfits/Casual/Accessories=force` uses
`My Inventory/#RLV/Avatar_Outfits/Casual/Accessories`. Leading and trailing `/`
separators are accepted; do not include `My Inventory` or `#RLV` in the path.
Folder listings load only the requested path. Outfit queries and actions load
the relevant contents and resolve inventory links in batches, while preserving
locks on the same item linked in another folder. Failed folder responses remain
retryable, and a missing shared root is checked against the server before an
empty listing is returned. Current Outfit link targets are fetched even when
the actual wearable is outside `#RLV`.

For troubleshooting, enable **Show RLV commands and replies in Nearby Chat**
in the RLV tab. Diagnostics include the actual command, discovered shared-root
UUID, cached folder/item counts, and the reply sent on the script's channel.
Commands from linked attachment prims apply to their root object.
Restrictions from missing objects are removed after a two-minute grace period;
attachment loading during region changes does not immediately clear locks.

This implements the protocol features connected to the current GTK interface,
not every RLV/RLVa feature. Camera, rendered environment, viewer debug settings,
content previews/editing, sharing, IM/group-chat restrictions, and teleport
requests are unavailable. The unavailable behaviors are listed through
`@getblacklist` / `@versionnumbl`; they do not start a renderer or audio engine.
An in-world RLV relay attachment can forward commands to the viewer. A built-in
relay is not included.

The adapter's regression checks run without a display or grid login:

```sh
dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -p:TargetFrameworks=net10.0
```

For a live check, use an RLV attachment to test detection, lock/unlock and
detachment through both the actual item and an inventory link. Then check its
shared inventory menu and test two accounts with different restrictions.

The IMs, Group Chats, and Friends tabs currently show placeholders.
Inventory item content editors, previews, rez actions, desktop
notifications, and notification sounds are also pending.
The [GTK3 client plan](../docs/Gtk3ClientPlan.md) tracks the intended scope.
