# Pawprint Viewer

This is the first GTK3 implementation checkpoint for a lightweight Linux
client. It uses the native GTK theme, font, and normal window-manager borders.
The main window title is always **Pawprint Viewer**, including when switching
accounts or logging out.
It references `Radegast.Core` for grid communication and does not start the
3D renderer or the FMOD audio engine.

## Build

The repository contains the GTK3 viewer, its shared core and their tests. From
its root, build the solution with:

```sh
dotnet build -c Release
```

The executable is `Radegast.Gtk/bin/Release/net10.0/PawprintViewer`. To collect
all runtime files in a separate directory:

```sh
dotnet publish Radegast.Gtk/Radegast.Gtk.csproj -c Release -o bin/PawprintViewer
```

## Run

Install the .NET 10 SDK and the GTK3 runtime, then run from the repository root:

```sh
dotnet run --project Radegast.Gtk/Radegast.Gtk.csproj
```

The add-account window supports Second Life and the grids listed in the
`Radegast.Gtk/grids.xml`, plus a custom login URI. An MFA challenge can be
answered in the same window. The account field is an editable dropdown: select
a remembered account to fill its password, or type a new name manually.
Successful logins remember names and update saved passwords automatically.
The list is specific to the selected grid, including custom login URIs.

Passwords use the Linux desktop Secret Service keyring through `libsecret`.
Install the `libsecret` runtime and run a Secret Service provider such as GNOME
Keyring in your desktop session. The keyring may ask you to unlock it.
Account names and grid URIs are kept in
`$XDG_CONFIG_HOME/pawprint-viewer/accounts.json` (normally
`~/.config/pawprint-viewer/accounts.json`); passwords and MFA codes are never
written to that file. MFA codes must still be entered for each challenge.
If the keyring is unavailable, manual login still works. The name is remembered
and a password-save failure is reported in that account's Nearby Chat.

## Implemented in this checkpoint

- One GTK3 window with a left account rail, top tabs, and a right nearby-avatar
  list for the selected account. Both side panes have a fixed share of 15% of
  the window width; the center uses the remaining 70%. Long names are ellipsized
  and available in tooltips.
- Multiple connected accounts in one process. Switching accounts preserves
  each session's selected tab and chat transcript. The account rail shows each
  avatar's current region and local coordinates across the full row, with its
  balance below (for example, `69,420 L$`). Balances update automatically. The
  square logout button sits beside the avatar name and matches its button height.
- Nearby chat send/receive, and a distance-sorted list of nearby avatars from
  the current simulator's coarse-location updates. Use `/9 testcommand on` to
  send `testcommand on` on channel 9; signed channel numbers are accepted, and
  `/0 message` sends public chat. Channel commands respect RLV channel locks.
- Account-specific logout and unread nearby-chat counts for background accounts.
- Account tabs are ordered Nearby Chat, Friends, IMs, Group Chats, Inventory,
  Attachments, Objects, Map, and Account Settings.
- Map displays the grid's world tiles in a square occupying the full tab height.
  It centers on the selected avatar whenever opened. Drag to pan and scroll to
  zoom around the pointer. Click to fill the region and local X/Y destination
  fields; the entered Z altitude is retained. Dragging never selects a destination,
  and clicks never teleport automatically. Green marks your avatar, blue marks
  other avatars, and gold marks the destination. The map has no hover popup.
  Each region has a bottom-left label such as `Region - 9`, using the grid's
  total avatar count (including you). Labels follow the system font, shorten
  long names while retaining the count, and hide when regions are too small
  to read. Region names/counts refresh every 30 seconds while the tab is open;
  RLV nearby-avatar restrictions hide the counts.
  Region and destination controls are on the left. The window's minimum width
  accommodates the square map and controls while retaining the 15% side panes.
- Enter a region name and click the magnifier (or press Enter) to find that
  region. X, Y and Z are **local coordinates within the entered region**, in
  meters; X/Y are normally 0–255. **Teleport** resolves the region if necessary,
  validates coordinates and waits for the server's confirmation. Failures appear
  below the button. Opening the map again restores your current location.
- Map images load only while that account's Map tab is displayed, with four
  concurrent downloads and a small image cache. The service URL comes from the
  grid's login response. Grids without a tile service can still use region
  search and teleport. This is a 2D GTK/Cairo view, without a 3D renderer.
  RLV world-map/location restrictions hide the map, and teleport, local-distance
  and stand restrictions apply to the Teleport button and its requests.
- Other-avatar markers use live coarse positions from connected regions and the
  grid's approximate population positions for other regions in a close map view.
  Larger blue dots can represent a group at one location. Population queries are
  limited to 64 visible regions and four requests per second, refreshing after
  30 seconds; very wide views show available positions without scanning the
  entire grid. RLV nearby-avatar restrictions hide these markers.
  Failed image downloads retry quietly without an error overlay on the map.
- **Global Settings**, below **+ Add account**, controls silent desktop popups
  for IMs, Group Chats, Worn Objects, Menus, friends going online/offline,
  Teleport Offers, and Sim Restarts. All seven are enabled initially. Changes
  apply to every account and are saved in
  `$XDG_CONFIG_HOME/pawprint-viewer/settings.json` (normally
  `~/.config/pawprint-viewer/settings.json`). **Test notification** checks delivery.
  Linux desktop popups use the system's `libnotify` runtime and notification daemon.
- Messages already being viewed in the foreground stay quiet. Background menus
  notify without raising the viewer. Private IM popups show just the sender's
  name above the message preview. Group chats show just the group name above
  the message preview, and object chat/menus show just the object name above
  the message. Friend status popups show just the friend's name above
  `is online` or `is offline`. Message previews use a single line shortened
  with an ellipsis. They are measured with the system font, with a conservative
  width for Xfce popups;
  final popup sizing is controlled by the desktop notification theme.
  Popups have no action buttons.
  Notifications honor RLV name/location restrictions and replace earlier popups
  for the same conversation. Initial friend statuses and RLV commands
  do not produce notifications. Disabling a category or logging out clears its
  open popups; conversations and script menus continue to work normally.
  First presence replies during the first 10 seconds after login establish a
  silent baseline. Further changes for an observed friend notify immediately;
  friends initially offline can produce login notifications after that period.
- **Sim Restarts** popups show **Sim restart** above the remaining time (for
  example, **5 minutes remaining**). The first server warning notifies immediately,
  then reminders appear once per minute while the avatar remains in that region.
  Later warnings update the countdown without duplicating popups. Reminders stop
  when the countdown expires, the avatar leaves the region, or the account
  disconnects. This notification switch is independent of **Teleport on region
  restart** in Account Settings.
- Incoming teleport offers open an account-specific window saying
  **[sender] wants to teleport you to their location.** **Accept** starts the
  offered teleport; **Refuse** or closing the window declines it. Background
  offers wait until you bring the viewer forward, without taking desktop focus.
  Their desktop notifications show the sender's name above **Teleport offer**;
  disabling **Teleport Offers** notifications keeps the decision window available.
  RLV teleport-lure and seated stand locks apply at acceptance, and existing RLV
  automatic acceptance/refusal rules still apply. Disconnecting closes old offers.
- `PawprintViewer.png` supplies the application/window icon and desktop popup
  icon. It is copied alongside the executable for both builds and publishing.
- Private IMs have a vertical conversation list on the left and the selected
  resident's transcript and message field on the right. Send with **Send** or
  Enter. Incoming messages create conversations without switching away from
  the current conversation. Histories, selected conversations, and unsent drafts
  remain separate for each account and resident. Conversations, the IM tab, and
  the account rail show unread counts; displaying a conversation marks it read.
  Histories and drafts are kept in memory for the current login only.
- To keep long sessions lightweight, Nearby Chat retains the newest **2,000
  messages**, and each IM/group conversation retains up to **2,000 messages**.
  A **1,000,000-character** budget per history can shorten that further for long
  messages. Older messages leave the in-memory history; chat is not archived to
  disk. Unsent drafts are preserved. Profile text is displayed in full.
- IMs request offline delivery and retrieve stored offline messages after login.
  Private messages keep channel prefixes such as `/9` as literal text; channel
  commands belong in Nearby Chat.
- Avatar profile URLs such as `secondlife:///app/agent/<uuid>/about` appear as
  clickable resident names in Nearby Chat, IMs, Group Chats and both profile
  text tabs. Unknown names load automatically for the originating account and
  update existing text.
  Clicking opens a GTK profile window with Second Life/First Life text, birth
  date and **Dismiss**. Names and profile access respect RLV name restrictions.
  Both text tabs wrap long lines and scroll vertically in a text area about
  50% taller than before. The full AgentProfile reply takes priority over the
  shorter legacy reply, including when it arrives later. Profiles start at the
  top; resolving names preserves the reading position. Grids without that
  capability use the legacy text supplied by the server.
- Nearby Chat, IMs, Group Chats and both profile text tabs render `[URL label]`
  links using the supplied label, including spaces and Unicode. HTTP/HTTPS links
  (including map SLURLs) open
  in the default browser. Agent `/about` links open that resident's profile;
  `/pay` links open a payment prompt for the linked resident on the originating
  account. Money is sent only after entering an amount and clicking **Pay**.
  Name and location restrictions also apply to labeled links. Unsupported or
  malformed markup stays as text.
  For example, `[https://example.org Wishlist ♥]` displays the clickable text
  `Wishlist ♥`. Formatting changes the display; messages sent to the grid and
  stored in conversation histories retain their original text.
- Profiles have **IM**, **Pay** and **Offer TP** on the first action row,
  **Add Friend**/**Remove Friend** and **Block**/**Unblock** on the second,
  with **Dismiss** underneath. IM selects that resident's conversation in the
  originating account's IM tab. Pay uses the same whole-number L$ prompt as
  Friends and also works for non-friends. Offer TP invites the resident to this
  account's location and honors RLV location restrictions. Friendship requests
  stay pending until a reply; accepted/removed friends update both views.
  Blocking updates the account's grid mute list, which is fetched at login,
  and suppresses that resident's incoming private, group and nearby chat,
  unread counts, notifications and new teleport prompts. Earlier messages stay
  in history; Unblock restores future messages. Profile payment prompts close
  when the profile is dismissed or the account disconnects.
- Group Chats uses the same full-height layout as IMs. Existing group memberships
  appear alphabetically on the left; select a group to join its chat, then use
  **Send** or Enter. **Refresh groups** reloads memberships. Incoming group
  messages go to their own transcripts and show the sending resident's name.
  Each account and group keeps its own history, selection, draft, and unread count.
  Viewing a group marks only that conversation read. Joining waits for server
  confirmation; failed joins can be retried by selecting the group again.
  Rejected messages restore the draft when the message field is still empty.
  Group chats and private IMs remain separate, including their unread tab counts.
- Friends are listed with online residents first, alphabetically within each
  group, and a small online indicator. Names and online status update as replies
  arrive; friendship additions/removals also update the list.
- Each friend has **IM**, **Pay**, and **Offer TP** buttons to the left of their
  name. IM opens that friend's conversation in the IMs tab. Pay opens a separate
  **Pay**/**Dismiss** window with a digits-only amount field. Only positive whole
  amounts are accepted; amounts over the known balance and invalid/stale
  recipients are rejected. A payment window sends once and closes after the
  request; server confirmations or failures appear in that account's Nearby
  Chat, and the balance updates from the server's reply.
- Offer TP sends that account's current location to the friend. When RLV hides
  location, offers are available only to friends already granted map rights.
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
- The Objects tab lists rezzed objects within a 50 m radius, nearest first,
  with their names, distances, and **Touch** and **Sit** buttons on the left.
  Touch is disabled when neither the root nor its linked parts are touchable,
  or when RLV prevents touching it. Linked objects appear once; attachments
  are excluded. The list updates while displayed,
  including objects in connected neighboring regions. **Refresh** retries
  missing names. **Stand** becomes available while seated, including ground
  sits. Sit/stand actions respect RLV locks and sitting-distance restrictions;
  sitting waits for server confirmation, and standing uses the existing
  furniture animation cleanup.
- Script menus and text prompts opened by touched objects appear in their own
  compact windows; button and text replies go to the correct account.
- New menus and permission prompts wait while another application has focus.
  They appear when you manually return to a viewer window, without bringing
  the viewer forward in the background. Logging out discards that account's
  pending prompts.
- Common system folders stay at the top of My Inventory; the Type column uses
  20% of the inventory list, and the fixed details pane uses 25% of the view.
- **Auto sit one minute after login**, directly below the RLV controls in
  **Account Settings**, is off by default. Enable it and enter the furniture's
  root-object UUID. It waits 60 seconds after each successful login or automatic
  reconnect, then requests a sit and waits for the server to confirm. Preferences
  are saved per account and grid. If already seated, it leaves the current seat
  unchanged. Disabling it, disconnecting, logging out or closing the account
  cancels pending requests. The furniture must be loaded in a connected region;
  RLV sitting and distance restrictions apply. Editing the UUID during the first
  minute keeps the original deadline; enabling it later waits for the next login.
  Invalid UUIDs show a hint, and failed sits appear in Account Settings and Nearby Chat.
- **Account Settings** contains settings only: RLV/RLVa enable and chat diagnostics, Auto Sit,
  plus **Automatically reconnect after a disconnection** and **Reconnect delay
  (seconds)**. The active restriction list has been removed. Reconnect is off by
  default; its default delay is 30 seconds, adjustable from 1 to 86400 seconds.
  Preferences are saved per account and grid in
  `$XDG_CONFIG_HOME/pawprint-viewer/account-settings/` (normally
  `~/.config/pawprint-viewer/account-settings/`).
  After an unexpected disconnect, the account stays in the left list and retries
  using its current login credentials at its last location. Failed connection
  attempts wait for the same delay before retrying; attempts never overlap.
  Changing the delay reschedules a pending retry. Disabling reconnect, logging
  out or closing the viewer cancels pending retries, including callbacks already
  queued for the UI. Initial failed logins do not start reconnects. Authentication
  challenges or rejected credentials stop retries and ask for a fresh login;
  one-time MFA codes are never replayed. Reconnect keeps the current account's
  conversations/drafts and refreshes its inventory cache. These preference files
  contain no passwords; reconnect uses the credentials already held in memory.
  **Disconnect to test reconnect** closes that account's connections while
  leaving it in the left list. Enable automatic reconnect and set the delay
  before clicking it to test recovery. With reconnect disabled, it stays
  disconnected. The button is unavailable while disconnected or disconnecting.
  The unused UDP scene texture worker is disabled: LibreMetaverse 3.1.5 otherwise
  restarts it with a cancelled delay token after reconnect, causing an idle CPU
  spike. World-map tile downloads and HTTP appearance textures stay enabled.
- **Teleport on region restart** in Account Settings is off by default. Set a
  temporary **region**, **X/Y/Z**, and **Return delay (minutes)** (default 5,
  range 1–1440). Preferences are saved for this account and grid. Structured
  restart warnings from the current region's server schedule departure when
  **60 seconds remain**. Later warnings refine that countdown; a warning with
  60 seconds or less remaining triggers immediate departure. The avatar
  teleports to that destination, which must be in a different region. The return
  timer starts after confirmed arrival and requests the original region and
  exact position captured when leaving. Failed return attempts retry once a
  minute without overlap.
  The status below the controls reports progress or failures. RLV teleport and
  seat restrictions apply. Changing the return delay reschedules a waiting
  return; destination edits apply to the next departure. Turning the
  setting off, moving to another region, disconnecting, logging out, or closing
  the viewer cancels pending departures and returns, including queued UI callbacks.
- Script permission requests open separate Allow/Deny windows. RLV permission
  rules can deny requests or automatically accept animation, attachment, and
  control permissions; other permissions still require an explicit response.

## RLV / RLVa

RLV is enabled for each new account session. Open that account's **Account Settings**
tab to turn it off. Turning it off cancels queued commands and clears restrictions
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
chat and emote restrictions/redirection, private IM start/send/receive restrictions,
group chat send/receive restrictions and resident/group exceptions, touch
restrictions, hidden inventory,
names and location, forced sit/stand/rotation/teleport, active group changes,
teleport offer restrictions/automatic acceptance, and script permission rules.

When the avatar stands or moves directly to different furniture, the client
stops animations started by the previous furniture and its linked prims.
Animations from attachments and the new furniture are preserved. This cleanup
also works with RLV disabled and does not interrupt moves between seats in the
same furniture linkset.

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
in the Account Settings tab. Diagnostics include the actual command, discovered
shared-root UUID, cached folder/item counts, and the reply sent on the script's channel.
Commands from linked attachment prims apply to their root object.
Restrictions from missing objects are removed after a two-minute grace period;
attachment loading during region changes does not immediately clear locks.

This implements the protocol features connected to the current GTK interface,
not every RLV/RLVa feature. Camera, rendered environment, viewer debug settings,
content previews/editing, sharing, and teleport
requests are unavailable. The unavailable behaviors are listed through
`@getblacklist` / `@versionnumbl`; they do not start a renderer or audio engine.
An in-world RLV relay attachment can forward commands to the viewer. A built-in
relay is not included.

The adapter's regression checks run without a display or grid login:

```sh
dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj
```

The map's native GTK scroll check requires a desktop display, but no login. It
checks smooth scrolling, regular mouse-wheel events, click/drag separation, and
avatar-marker drawing through GTK callbacks:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --map-scroll-smoke
```

The login window's native GTK check also requires a display. It simulates
successful logins, asynchronous password saving, Cancel and window-manager
closure, and forces garbage collection to catch native reference errors.
It uses temporary account metadata and a fake keyring; no grid login is needed:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --login-smoke
```

The Account Settings check verifies Auto Sit's placement below RLV, checkbox,
UUID validation and saving. It also simulates disconnects and advances a fake clock to
test the reconnect and restart destination/delay controls, preference saving,
reconnect status, inventory cache replacement and logout cleanup without a grid login:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --account-settings-smoke
```

The native memory check simulates repeated nearby updates, row departures,
Friends/Attachments refreshes, rolling chat retention, and account removal.
It reports resident/managed memory, verifies native widget cleanup, and checks
that group chat continues displaying new messages after history reaches its
limit. It requires a display and does not log in to a grid:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --memory-smoke
```

The native chat-link check tests name resolution and labeled web/pay/about links
in all three chat tabs, profile text links, clicks through GTK events, delayed
profile/name replies, selection, reconnect and payment/window cleanup. It
captures browser requests without opening a browser, uses simulated packets
and does not connect to a grid:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --chat-links-smoke
```

The full-profile check uses simulated HTTP replies to test long Second Life and
First Life text, capability/legacy reply order, fallback failures, the larger
text area and scrolling through the final paragraphs:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --profile-text-smoke
```

The labeled-profile-link check clicks web, payment and avatar links in both
profile tabs, checks payment recipients, selection and window cleanup, and
captures browser requests without launching a browser or paying anyone:

```sh
G_DEBUG=fatal-criticals dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -- --profile-links-smoke
```

For a live check, use an RLV attachment to test detection, lock/unlock and
detachment through both the actual item and an inventory link. Then check its
shared inventory menu and test two accounts with different restrictions.
For furniture, use two seats with different animations and test both an RLV
furniture switch and standing. Check from another viewer that the old pose
stops, the new pose plays, and attachment/AO animations remain active.

Conference chats, group notices, and group membership/role administration are
not implemented. Group and conference messages do not become private conversations.
Inventory item content editors, previews, and rez actions are also pending.
The [GTK3 client plan](../docs/Gtk3ClientPlan.md) tracks the intended scope.
