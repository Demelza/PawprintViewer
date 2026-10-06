# Pawprint Viewer

This is the first GTK3 implementation checkpoint for a lightweight Linux
client. It uses the native GTK theme, font, and normal window-manager borders.
The main window title is always **Pawprint Viewer**, including when switching
accounts or logging out.
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
- Private IMs have a vertical conversation list on the left and the selected
  resident's transcript and message field on the right. Send with **Send** or
  Enter. Incoming messages create conversations without switching away from
  the current conversation. Histories, selected conversations, and unsent drafts
  remain separate for each account and resident. Conversations, the IM tab, and
  the account rail show unread counts; displaying a conversation marks it read.
  Histories and drafts are kept in memory for the current login only.
- IMs request offline delivery and retrieve stored offline messages after login.
  Private messages keep channel prefixes such as `/9` as literal text; channel
  commands belong in Nearby Chat.
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
- Script menus and text prompts opened by touched objects appear in their own
  compact windows; button and text replies go to the correct account.
- New menus and permission prompts wait while another application has focus.
  They appear when you manually return to a viewer window, without bringing
  the viewer forward in the background. Logging out discards that account's
  pending prompts.
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
in the RLV tab. Diagnostics include the actual command, discovered shared-root
UUID, cached folder/item counts, and the reply sent on the script's channel.
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
dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj -p:TargetFrameworks=net10.0
```

For a live check, use an RLV attachment to test detection, lock/unlock and
detachment through both the actual item and an inventory link. Then check its
shared inventory menu and test two accounts with different restrictions.
For furniture, use two seats with different animations and test both an RLV
furniture switch and standing. Check from another viewer that the old pose
stops, the new pose plays, and attachment/AO animations remain active.

Conference chats, group notices, and group membership/role administration are
not implemented. Group and conference messages do not become private conversations.
Inventory item content editors, previews, rez actions, desktop
notifications, and notification sounds are also pending.
The [GTK3 client plan](../docs/Gtk3ClientPlan.md) tracks the intended scope.
