# GTK3 client plan

This document records the agreed direction for a Linux client with native GTK3
widgets. It uses `Radegast.Core` for grid communication and account services.
The GTK client is a separate front end; it does not load the WinForms or
Avalonia user interfaces.

## Window

The application has one normally decorated `Gtk.Window`. It uses the active
GTK3 theme and the system font; no application-wide font or theme override is
applied.

```text
+--------------+-------------------------------------------+------------------+
| Accounts     | Nearby Chat | IMs | Group Chats | ...      | Nearby avatars   |
|              +-------------------------------------------+ for the selected |
| Avatar A     |                                           | account, nearest |
| Avatar B     | Content for the selected account and tab  | first            |
|              |                                           |                  |
| + Add account|                                           |                  |
+--------------+-------------------------------------------+------------------+
```

- The left rail shows logged-in accounts and keeps the add-account button at
  its bottom. It shows connection state and unread activity per account. Each
  account has its name and logout button on one line, a full-width location
  line below, and a balance line formatted like `69,420 L$`.
- The central tab strip belongs to the selected account. Initial tabs are
  Nearby Chat, IMs, Group Chats, Inventory, Attachments, and Friends.
- The right pane always shows nearby avatars for the selected account, ordered
  by distance. It updates after movement, teleport, or account selection.
- Switching accounts preserves each connection, selected tab, open conversation,
  and unread state. Logging out one account leaves the others connected.
- The window remains open when all accounts are logged out, so the add-account
  button can start another login.

## Scope and resource use

- There is no 3D scene, avatar renderer, map renderer, or GPU viewport.
- There is no in-world sound, parcel stream, or voice UI. The client must not
  initialize the core FMOD sound engine. Notification sounds are a separate,
  optional desktop UI feature.
- Optional desktop notifications identify the receiving account. Their click
  action selects that account and the relevant conversation or notice.
- Image and asset previews load only when their content is opened.

## Implementation boundaries

1. Add a `net10.0` GTK3 executable that references `Radegast.Core` and uses
   GtkSharp. Keep GTK dependencies out of `Radegast.Core`.
2. Give each logged-in account its own `GridClient`, network adapter, and
   session state. Marshal network events to the GTK main loop before updating
   widgets. The current Avalonia `AgentSessionManager` demonstrates the
   multi-session lifetime, but cannot be referenced directly by the GTK app.
3. Put the selected session in the single main window, while session models
   retain the state needed to restore their tabs and conversations.
4. Implement login and nearby chat first, then concurrent logins and the
   account rail, then IM/group chat, inventory, attachments, and friends.
5. Add notification sounds and desktop notifications after the event routing
   works for both selected and background accounts.
6. Give each account a separate RLV/RLVa engine and command queue. Enforce rules
   in both GTK controls and outfit operations, including replacement of locked
   items. Load shared inventory only when queried, and advertise features that
   are unavailable in this client through the protocol blacklist.

## RLV checkpoint

The RLV tab shows each account's active restrictions and enable switch.
Inventory, attachment touch, nearby chat, and displayed names/location now use
that account's permissions. The shared command engine handles inventory queries
and forced outfit, sit, teleport, rotation, and group operations. Script
permission requests have separate GTK prompts. Furniture animations are cleaned
up on stand and direct seat changes using their source objects, preserving
attachment animations and the next seat's pose. The cleanup remains active
with RLV disabled.

Shared inventory is rooted at `My Inventory/#RLV`; folder listings and outfit
operations fetch the requested path, resolve item links, and retain locks from
other shared folders. Diagnostics show commands, the shared root, and channel
replies. See the
[GTK README](../Radegast.Gtk/README.md#rlv--rlva) for supported features,
limitations, and the regression-check command.

## First working checkpoint

Two accounts can log in concurrently in one window, switch without disconnecting,
send and receive nearby chat independently, and see the correct nearby-avatar
list for the selected account. The UI follows the current GTK3 theme, font, and
window decorations. No 3D or in-world audio subsystem starts.
