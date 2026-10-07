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
  contains RLV controls and per-account automatic reconnect settings.
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

## Remembered logins checkpoint

The add-account name field is an editable dropdown of successful previous
logins for the selected grid. Selecting a name retrieves its password from the
desktop Secret Service keyring with libsecret; typing a different account or
changing grids clears the previous password. Grid endpoints and normalized
login names identify credentials, so `name`, `Name Resident`, and `name.Resident`
share one saved login on the same grid while other grids stay separate.
Password lookups run outside the GTK thread, and stale lookups cannot overwrite
later selections or passwords entered manually.

Only successful logins save or update credentials. Failed attempts and MFA
challenges do not save them, and MFA codes are not remembered. The settings
directory contains a names/grid-URI list with permissions limited to the user;
passwords only go to the keyring. Saving runs independently of connecting the
account. Keyring failures preserve manual login and report password-save errors
in Nearby Chat. Existing account lists survive restarts, and malformed metadata
does not prevent entering a new account manually.

Successful login, Cancel, window-manager close and viewer shutdown share one
login-popup cleanup path. It disposes the GTK window while its native reference
is still valid, cancels password lookups and ignores late login callbacks.
Credential saves may finish after the popup closes. The native GTK login check
exercises these lifetimes with garbage collection enabled.

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

Region labels appear at the bottom left as `Region - 9`. Counts sum the grid's
map population clusters, with map block counts used until population arrives;
this includes all avatars rather than only residents in the nearby list.
Counts and visible region metadata refresh every 30 seconds. Labels
use the system font on a translucent backing, shorten long names while keeping
the count visible, and disappear when zoomed out too far to read. Partial regions
keep their labels inside the visible portion. RLV nearby restrictions hide the
counts while leaving permitted region names visible.
The distance scale is at the top right to keep region labels clear.

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

## Account Settings and automatic reconnect checkpoint

Account Settings contains the RLV enable/diagnostic controls and an automatic
reconnect checkbox with a whole-number delay in seconds. The restriction list
and command status display have been removed. Reconnect is disabled by default,
with a 30-second default delay (1–86400 seconds). Reconnect preferences are saved
in separate files per normalized resident name and grid URI under the viewer's
`account-settings` config directory, with defaults for older or invalid files.
These files contain preferences only; the current session retains its login
credentials in memory, and passwords saved by the login dialog stay in the keyring.

Only a previously successful session can reconnect after an unexpected network,
server or simulator disconnect. It uses the same account/grid and MFA trust hash,
clears one-time MFA tokens, and requests the last location. Failed connection
attempts retry after the configured delay without overlapping an active attempt.
Authentication/TOS/update failures stop retries and require a fresh manual login.
Changing the delay reschedules pending retries. Disable, logout, viewer shutdown
and manual login invalidate timers and already queued GTK callbacks. Accounts
keep their widgets, conversations and drafts. Inventory replaces its store
subscriptions and clears stale folder request state after the new login.
Headless checks use a fake clock to cover retry ordering, cancellation, independent
accounts, authentication challenges and saving failures. The native settings
check also exercises GTK controls, inventory replacement and logout cleanup.
The **Disconnect to test reconnect** button closes the selected account's actual
network connections through the SDK timeout shutdown path, which raises the
normal disconnect event and schedules recovery if enabled. It retains the
account's widgets and preferences and is disabled during/disconnected after the
request, then re-enabled on connection. The logout action still cancels retries.

The GTK account disables LibreMetaverse 3.1.5's unused UDP scene texture pipeline
before login. Its shutdown cancels a token that startup never replaces; after
reconnect the master loop catches each cancelled delay and spins continuously,
even with no texture requests. An isolated SDK reproduction measured about
10 ms of CPU per second before reconnect and 1055 ms per second afterward,
returning to idle on shutdown. Map tiles use their separate HTTP service and
HTTP appearance textures remain enabled. A regression check exercises real SDK
login subscribers and three timeout shutdown/reconnect cycles, confirming that
the texture task stays absent and automatic reconnect still runs.

## Region restart teleport checkpoint

Account Settings now includes an optional **Teleport on region restart** switch,
a temporary region with local X/Y/Z coordinates, and a return delay in minutes
(default 5, range 1–1440). Settings persist per resident/grid; older preference
files retain their reconnect values and leave this feature disabled. The settings
panel scrolls when its controls exceed the available height.

The adapter recognizes structured `RegionRestartMinutes`/`RegionRestartSeconds`
simulator alerts, checking both the sending simulator and the named region.
Chat and neighbouring-region warnings cannot trigger recovery. The warning's
minutes/seconds countdown schedules departure when 60 seconds remain, or leaves
immediately if the warning arrives at/below that threshold. New warnings refine
or postpone the departure deadline. Monotonic elapsed time accounts for queued
UI callbacks; stale timer generations cannot depart after a reschedule. A single trip
captures the original region handle and the position when leaving, resolves the temporary region,
rejects a destination in the same region, and awaits server-confirmed teleport.
The return delay starts only after arrival. Return failure schedules one retry
per minute; duplicate warnings and outstanding requests never overlap. RLV
teleport/unsit restrictions apply, independently of world-map visibility.

Turning the feature off, leaving the restarting/temporary region, disconnecting, manual
login, or disposing the account cancels timers and active work. Trip identities
and timer generations prevent queued callbacks from reviving a cancelled return.
Return delay edits reschedule a waiting trip; destination edits affect the next
departure. Editing restart preferences leaves an existing reconnect deadline intact.
Headless checks cover persistence, malformed alerts, the 60-second departure
threshold, updated countdowns, queued warning delays, timing after arrival,
retry spacing, cancellation, independent accounts, SDK teleport packets and
server confirmation. The native account settings check covers the new GTK
controls and saving them alongside reconnect preferences.

## RLV checkpoint

The Account Settings tab contains each account's RLV enable switch and command
diagnostic checkbox.
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

## Teleport offers checkpoint

Incoming resident teleport lures have a separate GTK decision window with the
sender's name, recipient account, and Accept/Refuse buttons. Accept sends the
original lure ID using the receiving account; Refuse and the window close button
send a decline to the original sender. Responses are single-use, retransmitted
offers are deduplicated, and disconnect/logout clears pending decisions.
Acceptance rechecks RLV lure permissions and seated `unsit` locks. Existing RLV
automatic acceptance and refusal continue to work. Sender names honor RLV
display restrictions. Background windows wait for the viewer to regain focus.

Teleport Offers has its own global notification switch, enabled by default in
new and existing settings files. Popups show the sender's name on the first line
and `Teleport offer` on the second. Disabling notifications keeps the decision
window available, and foreground decisions follow normal notification suppression.

## Notifications checkpoint

Global Settings has switches for incoming private IMs, group chats, private
owner-say/directed chat from worn objects, object menus/text prompts, friend
online/offline transitions, teleport offers, and sim restarts. Preferences use
the XDG configuration directory and
apply immediately. Initial friend presence, outgoing chat, typing, group echoes,
and RLV commands are excluded. Message content follows RLV receive and display
rules. A test button provides a manual desktop notification.

Sim restart popups use `Sim restart` as the title and the remaining time as
the preview. Structured current-region alerts start per-account reminders,
independently of automatic restart teleports. The first warning notifies
immediately; one-shot timers repeat once per minute using monotonic elapsed time.
Later alerts refine the countdown, while timer generations reject stale queued
callbacks and prevent duplicate popups. Region changes, disconnects, new logins,
account disposal, and countdown expiry stop reminders. The global switch applies
immediately and clears existing restart popups. Headless checks cover timing,
updates, queued callbacks, account isolation and settings; the native settings
check covers the checkbox and persistence.

The login buddy list contains no online statuses. First status replies within
10 seconds of connecting establish a silent baseline, including multiple initial
packets. Later transitions for an observed friend notify immediately. After that
settling period, an initially offline friend's first login also notifies. Each
account/login keeps its own baseline; logout resets it.

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

## Chat profile links checkpoint

Nearby Chat, IMs and Group Chats share a GTK transcript renderer. Agent profile
SLURLs display an underlined resident name with the theme's link color. Missing
names are requested once per account and update existing transcript text when
the server replies; timestamps, punctuation, drafts and unread counts are kept.
Other URLs and malformed profile links retain their original text. The message
sent to the grid retains its original contents.

Clicking a name opens a read-only GTK profile window for that resident and
originating account, containing Second Life/First Life text and birth date.
Both profile text tabs use the same clickable-name renderer, including delayed
name resolution, paragraph layout, theme colors and RLV redaction.
They also parse `[URL label]` markup and display the supplied label, preserving
spaces and Unicode. HTTP/HTTPS links open the exact URL in the default browser,
including map SLURLs. Agent `/about` labels open the linked profile and `/pay`
labels open the shared payment prompt for the linked UUID, which can differ from
the profile owner. A link click never sends money: the user enters an amount and
clicks Pay in the prompt. Activating another recipient replaces the existing
prompt; repeated clicks for the same recipient reuse it. Malformed markup and
unsupported schemes/actions remain text. Live name/location and connection
permissions are checked while rendering and again on click. Labeled markup
applies to profile text; chat messages keep their existing rendering and content.
The text area is about 50% taller, with automatic vertical scrolling and wrapped
lines. Full text starts at the top; name updates preserve the reading position.
Profile retrieval uses UDP with the AgentProfile capability when available.
The first legacy reply can populate a fallback while the full profile loads.
A successful matching AgentProfile reply supersedes legacy text in either reply
order, preserving all paragraphs and Unicode, including legitimately empty or
shorter full fields. Late UDP replies cannot replace a full profile. Grids without
AgentProfile (or with failed capability requests) retain the legacy fallback.
Dismissal/logout cancels pending
requests and ignores late replies. These windows use the existing presentation
policy so they do not bring a background viewer to the foreground. Hidden names
become non-clickable `Resident` text; a name lock closes an open profile. Location
hiding continues to redact location links without concealing profile links.

Profile actions use two rows: IM/Pay/Offer TP, then Add Friend (or Remove Friend)
and Block (or Unblock), followed by Dismiss. The account that opened the profile
owns every action. IM selects its conversation/tab; Pay also accepts non-friends
and uses the shared validated payment prompt; Offer TP uses the same RLV location
rules as Friends. Friendship offers do not add a friend before acceptance, and
duplicate pending requests are disabled. Acceptance/removal updates open profiles
and the friend list. Self/disconnected actions are disabled.

The grid mute list is requested at login and updated by Block/Unblock. Muted
resident text is filtered before creating messages, unread counts, desktop
notifications or teleport prompts; private, group and nearby chat all apply it.
Existing transcripts remain intact. Blocking is independent per account and
also respects resident text blocks imported from the grid's mute list. Dismissing
a profile or disconnecting disposes any of its open payment prompts.

## Private IM checkpoint

The IMs tab has a vertical list of conversation buttons on the left, with the
selected conversation's transcript, message field, and Send button on the right.
Enter also sends. Friends' IM buttons open/select their conversations; incoming
resident messages create conversations without changing the selected one.
Each account keeps its own conversations, histories, selection, and per-resident
drafts while switching tabs and accounts. This state stays in memory for the
current login; persistent message logs are not implemented. Each conversation
retains the newest 2,000 messages within a 1,000,000-character text budget.
Nearby Chat uses the same count/text limits; profile text remains complete.
Stream indices keep the renderer advancing after old messages are removed.

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

## Memory checkpoint

Nearby updates retain one GTK row per resident and update its name/distance
instead of replacing the entire list. Departures dispose the row and its
children. Friends, Attachments, inventory search and conversation removal use
the same subtree cleanup, including button signal handlers and managed/native
toggle references. Logout stops subscriptions before releasing all three account
widget trees.

Chat models and visible buffers have count/text budgets. Removing old lines
also removes unused link tags and name references. GTK character offsets come
from the rendered text, preserving multiline messages and Unicode. Profile
`SetText` remains complete. The native memory check exercises repeated updates,
row disposal, friend actions, link-tag eviction, rolling group messages, and
logout with fatal GTK reference warnings enabled. Headless checks cover count
and character limits, drafts and Unicode.

The synthetic 40-resident/400-update reproduction retained about 111 MiB before
the fix even after collection, compared with about 5–6 MiB of growth afterwards.
These measurements cover the reproduced UI leak, rather than a live multi-hour
grid session.

## First working checkpoint

Two accounts can log in concurrently in one window, switch without disconnecting,
send and receive nearby chat independently, and see the correct nearby-avatar
list for the selected account. The UI follows the current GTK3 theme, font, and
window decorations. No 3D or in-world audio subsystem starts.
