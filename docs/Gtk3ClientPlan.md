# Pawprint Viewer GTK3 client plan

This document records the agreed direction for a Linux client with native GTK3
widgets. It uses `Radegast.Core` for grid communication and account services.
The GTK client is a separate front end; it does not load the WinForms or
Avalonia user interfaces.

## Window

The application has one normally decorated `Gtk.Window`. It uses the active
GTK3 theme and the system font; no application-wide font or theme override is
applied. Its title is always **Pawprint Viewer**, independent of the selected
account and connection state.

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
  account has its name and square logout button on one line, a full-width
  location line below, and a balance line formatted like `69,420 L$`.
- The central tab strip belongs to the selected account. Initial tabs are
  Nearby Chat, Friends, IMs, Group Chats, Inventory, Attachments, Objects, Map,
  and Account Settings. Account Settings currently
  contains only the existing RLV controls.
- **Global Settings** is below the add-account button. Notification category
  switches there apply to every account and persist between runs.
- The right pane always shows nearby avatars for the selected account, ordered
  by distance. It updates after movement, teleport, or account selection.
- The account rail and nearby pane each use 15% of the window width, with 70%
  for the central tabs. These proportions follow window resizing; there are no
  draggable dividers. Long account and nearby-avatar names are ellipsized, with
  their full names available in tooltips.
- Switching accounts preserves each connection, selected tab, open conversation,
  and unread state. Logging out one account leaves the others connected.
- The window remains open when all accounts are logged out, so the add-account
  button can start another login.
- New script menus and permission prompts wait while the viewer is in the
  background, then appear when the user returns to a viewer window. They do
  not raise the viewer over other applications. Logging out discards that
  account's pending prompts.

## Scope and resource use

- There is no 3D scene, avatar renderer, or GPU viewport. The world map is a
  2D GTK/Cairo drawing with tiles fetched only while its tab is displayed.
- There is no in-world sound, parcel stream, or voice UI. The client must not
  initialize the core FMOD sound engine. Notifications use silent desktop popups.
- Optional desktop notifications show the IM sender, group name, or object
  name above a message preview. Friend notifications show the friend's name
  above `is online` or `is offline`. Popups have no action buttons.
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
5. Add desktop notifications after the event routing
   works for both selected and background accounts.
6. Give each account a separate RLV/RLVa engine and command queue. Enforce rules
   in both GTK controls and outfit operations, including replacement of locked
   items. Load shared inventory only when queried, and advertise features that
   are unavailable in this client through the protocol blacklist.

## Map checkpoint

The Map tab has a square world map on the right, occupying the full tab height,
with destination controls in the remaining space on the left. GTK's minimum
width accommodates the map and controls without changing the 15% side panes.
Opening the tab or selecting an account whose Map tab is open centers on that
avatar. Dragging pans; wheel scrolling zooms around the pointer. A green marker
tracks the avatar, blue markers show other avatars, and a gold marker identifies
the entered destination. Clicking a point resolves its region and fills local
X/Y on the left while retaining the entered Z altitude. Clicks and drags are
distinguished using GTK's drag threshold, and no click teleports automatically.
New selections or field edits cancel older lookups. Clicking water leaves no
region selected. The map has no hover popup or failed-image overlay.

Connected simulators supply live coarse avatar coordinates. Other visible
regions supply approximate population positions, which can represent groups.
Population queries only run for the displayed account's map, have a limit of
64 visible regions and four requests per second, and refresh after 30 seconds.
Zoomed-out views display available markers without querying the entire world.
Disconnects, empty responses and stale positions remove obsolete dots; RLV
nearby-avatar restrictions hide other residents.

Region search uses the simulator's map lookup and centers on the matched region.
X/Y/Z occupy one row and are local coordinates in meters within that region.
Teleport can resolve an entered region itself, validates coordinates, and reports
server confirmation or failure. Each account owns its destination, view and tile
cache. Downloads use the grid's login-advertised map service, have bounded
concurrency, and cancel when the tab is hidden. Missing tiles render as ocean;
lookup and teleport remain usable when a grid has no tile service.

RLV `showworldmap` is now implemented rather than blacklisted. World-map and
location restrictions conceal the map and its fields. `tploc`, `tplocal`, and
seated `unsit` restrictions prevent forbidden manual teleports; pending requests
also observe new restrictions and disconnects.

## RLV checkpoint

The Account Settings tab shows each account's active RLV restrictions and enable
switch.
Inventory, attachment touch, nearby chat, private/group conversations, and
displayed names/location now use that account's permissions. The shared command
engine handles inventory queries
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

## Notifications checkpoint

Global Settings has switches for incoming private IMs, group chats, private
owner-say/directed chat from worn objects, object menus/text prompts, and friend
online/offline transitions. Preferences use the XDG configuration directory and
apply immediately. Initial friend presence, outgoing chat, typing, group echoes,
and RLV commands are excluded. Message content follows RLV receive and display
rules. A test button provides a manual desktop notification.

The GTK main loop delivers silent libnotify popups without raising the viewer.
Events already visible in the foreground stay quiet. IM, group, and object/menu
notifications show only the sender, group, or object name above the message
preview. Friend status notifications show only the friend's name on the
first line and `is online` or `is offline` on the second. Text is measured
with the system font and truncated at complete Unicode text elements, leaving
room for the desktop theme's popup sizing. Popups have no action buttons and
use `PawprintViewer.png`, which also provides the default GTK window icon and
is included in builds and publishing.
Each conversation replaces its previous popup, and the number of open popups
is bounded. Disabling a category, disconnecting, or logging out closes the
affected popups.

## Objects checkpoint

The Objects tab shows each rezzed object once within a 50 m sphere around the
selected avatar, ordered by distance. Attachments and linked child prims are
excluded. Names load in batches with bounded retries, and the full-height list
updates while that account's Objects tab is visible. Connected neighboring
regions use global coordinates so objects near a border have the correct range.

Each row has **Touch** and **Sit** buttons to the left of its name and distance.
Touch targets a touchable root or linked prim and is disabled for objects without
touch handlers or when RLV prevents touching them. A **Stand** button above the
list becomes available when seated. Actions recheck the live
object and RLV sit, unsit, and sit-distance restrictions. Sit requests wait for
the appropriate object response and avatar seat update; logout cancels pending
requests. Standing and switching furniture use the existing animation cleanup.

## Friends checkpoint

The Friends tab uses the same scrolling row layout as Attachments. Each row has
**IM**, **Pay**, and **Offer TP** buttons before the resident's name. Online
friends appear first with a small indicator; both online and offline groups
are ordered alphabetically. Name replies, status notifications, and friendship
changes refresh the appropriate account's list.

IM opens the friend's conversation in the IMs tab. Pay opens a compact separate
window bound to its original account and friend, with a digits-only amount field
and Pay/Dismiss buttons. Positive whole amounts are checked against the known
balance, and stale recipients/disconnected accounts cannot send payments.
Submission sends once, closes the window, and reports subsequent server payment
replies in that account's Nearby Chat. Closing a payment window or logging out
does not send money. Offer TP uses the originating account's current location
and respects RLV location restrictions and the friend's granted map rights.

## Private IM checkpoint

The IMs tab has a vertical list of conversation buttons on the left, with the
selected conversation's transcript, message field, and Send button on the right.
Enter also sends. Friends' IM buttons open/select their conversations; incoming
resident messages create conversations without changing the selected one.
Each account keeps its own conversations, histories, selection, and per-resident
drafts while switching tabs and accounts. This state stays in memory for the
current login; persistent message logs are not implemented.

Unread counts appear beside conversations, on the IM tab, and in the account
rail. Only the displayed conversation of the selected account is marked read.
Sending uses the correct avatar session and requests offline delivery. Stored
offline messages are retrieved after login. IM text is private even if it starts
with a Nearby Chat channel prefix.

Private IM start/send/receive restrictions and resident exceptions use the
account's RLV permissions. Restricted sends keep the draft, blocked incoming
messages are not displayed, and hidden names/location are redacted in the UI.
Group/conference messages, script messages, and other IM protocol events do not
become private conversations.

## Group chat checkpoint

Group Chats shares the IM tab's layout and fills the available height. The left
list contains the account's existing group memberships, ordered alphabetically.
Selecting a group requests its chat session and waits for server confirmation
before enabling Send; Enter also sends. Refresh groups reloads the membership
list. Failed or timed-out joins can be retried by selecting the group again.

Incoming messages use their group session IDs and retain the sending resident's
name. Messages arriving before membership loading completes wait for the roster,
so the initial group message is retained and conference sessions remain separate.
Each account/group has an independent history, selection, draft, and unread count.
The conversation buttons, Group Chats tab, and account rail show unread counts;
only the selected account's displayed group is marked read. State stays in memory
for the current login. Group membership changes disable sending to former groups
while preserving their message history.

Sending uses the group session protocol and avoids displaying the server's echo
as a second copy of a sent message. Long messages split at Unicode boundaries.
Expired sessions report server rejection and rejoin; messages are not automatically
resent. A rejected message returns to an empty composer so the user can resend it
after joining. Group chat send/receive rules use RLV group UUID/name exceptions,
including `allgroups`; sender names and locations follow the display restrictions.
Conference chats, group notices, and group membership/role administration remain
outside this checkpoint.

## First working checkpoint

Two accounts can log in concurrently in one window, switch without disconnecting,
send and receive nearby chat independently, and see the correct nearby-avatar
list for the selected account. The UI follows the current GTK3 theme, font, and
window decorations. No 3D or in-world audio subsystem starts.
