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
  its bottom. It shows connection state and unread activity per account.
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

## First working checkpoint

Two accounts can log in concurrently in one window, switch without disconnecting,
send and receive nearby chat independently, and see the correct nearby-avatar
list for the selected account. The UI follows the current GTK3 theme, font, and
window decorations. No 3D or in-world audio subsystem starts.
