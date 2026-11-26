# Working in this repository

KawaiiVrc is a WPF app (`VrcCompanion/`, net8.0-windows10.0.19041.0) with its xunit suite in
`VrcCompanion.Tests/`. From the repository root:

    dotnet build VrcCompanion.Tests/VrcCompanion.Tests.csproj -c Release -v q --nologo
    dotnet test VrcCompanion.Tests/VrcCompanion.Tests.csproj -c Release --no-build --nologo

A full run is 987 tests and takes about 3 minutes 30 seconds. CI builds with SDK 8, while a
newer SDK may be the local default: to check SDK 8 compatibility, write a `global.json` with
`{"sdk":{"version":"8.0.427","rollForward":"latestPatch"}}`, build, then delete it.

## Where the app stands

`VrcCompanion/VrcCompanion.csproj` holds the only version. The latest shipped release is **1.0.111**,
and every release from 1.0.78 on has been performance work: on a weak machine the
window froze for five to six seconds at a time, and two log readers ran synchronously on the UI
thread. The changes below are load-bearing and several have no test under them, so read this before
touching polling, image decoding, popups, the section cache or a list's panel.

### What 1.0.78 - 1.0.85 changed

- **Freezes are measured, not guessed.** `Services/UiJobTrace` names the periodic job in flight;
  `Services/UiStallMonitor` logs any gap over 250 ms from a background-priority timer and reads the
  UI thread's own processor time (`GetThreadTimes(GetCurrentThread())`). `MainWindow` subscribes
  both in its constructor and disposes the monitor on close. It costs one stopwatch read and one
  thread-time read per 100 ms tick, and allocates only when it reports.
- **1.0.80** stopped calling `EmptyWorkingSet` on the background trim. Emptying the working set made
  the process report a small footprint and then fault every trimmed page back in. The trim now only
  drops the decoded-image cache (`ImageLoader.TrimMemory`), which is the memory that actually grows.
- **1.0.83** moved both log readers off the UI thread: `MainViewModel.PollInstanceHistoryAsync` and
  `InstanceViewModel.PollLogForChangesAsync` do the file work in `Task.Run`, stop overlap with a
  bool (`_instanceHistoryPolling`, `_logPolling`), re-check sign-in after the await, and apply the
  result back on the dispatcher. `VrChatLogPlayerService` and `InstanceHistoryRecorder` grew
  `_gate`/`_gameGate` locks because a pool thread and the dispatcher now read the same snapshot.
- **1.0.84 / 1.0.85** removed every `DropShadowEffect`. WPF rasterizes that blur in software and
  re-rasterizes it whenever the surface moves, so each popup paid for it on every drag and every
  layout pass. The panels keep the 1 px border and dimmed backdrop they already had. Nothing tests
  this, so the check is the grep: `DropShadowEffect` must not appear under `VrcCompanion/`.
- **1.0.84 lite mode** now also caps every picture decode at 256 px (`ImageLoader.LiteDecodeWidth`
  through `ClampDecodeWidth`, 32 px floor), drops the friend safety-net poll from 10 s to 30 s, and
  keeps one cached section view instead of two. It already stopped the aurora animation and the
  image fade (`Controls/AuroraBackdrop`, `AsyncImage`, `CosmeticImage`).
- **1.0.84** replaced the friend list's per-poll change detection. It used to concatenate every
  row's fields into one string every ten seconds to conclude nothing had changed - a six-figure
  allocation per poll on a large account. It is a 64-bit FNV-1a hash now
  (`MainViewModel.FriendsSignature`) and allocates nothing.
- **1.0.84** routed the activity rows through a `VirtualizingStackPanel`.

### What 1.0.86 changed

- **Image cache reduced in lite mode.** `ImageLoader` normal mode stays 24 MB / 48 entries; lite mode drops to 8 MB / 16 entries, animation cache 12 MB → 4 MB / 8 → 2 entries, 32 → 8 frames, 512 → 256 px max dimension. The ceilings are read from `AppPerformance.LiteMode` on each check rather than captured once, so the Settings toggle applies without a restart, and `MemoryCacheLimitBytes` stays the normal 24 MB the test pins.
- **Background trim more aggressive.** `MainWindow._backgroundTrimTimer` runs every 10 s (was 15 s). `ImageLoader.Shared.TrimMemory()` is called the moment the window is minimized, and deliberately not on `Deactivated`: deactivation fires on every click into another window, so clearing the decoded cache there makes the next focus re-decode everything on screen to save at most the ten seconds the trim timer would have waited.
- **Section cache unchanged.** `MaxCachedSectionViews` stays 2 normal / 1 lite. Do not reduce further: `MainWindowLayoutTests` pins focus-mode persistence across section switches, and `AvatarTabSwitchTests` pins the two-section leave/release behavior.
- **API cache ceilings reduced.** `VrcApiClient` bounded caches shrank: worlds 1000→400, profiles 2000→500, mutuals 1000→200, users 2000→500, cosmetics 32→16, group lists 200→60. These are ceilings, not budgets; `BoundedCache` evicts LRU.
- **Polling intervals slowed.** Activity poll 10 s → 30 s (lite 30 s → 60 s), bio poll 10 s → 30 s, instance history 5 s → 15 s, clock 20 s → 60 s. The clock skips updates while minimized; instance history and activity/bio polls already return early when minimized. Realtime websocket remains the primary path; polls are safety nets.
- **Theme brushes no longer leak.** `MainWindow.ApplyKawaiiTheme` tracks created brush keys, removes old brushes from `Resources` before adding replacements, and freezes all brushes. Previously each accent change leaked `SolidColorBrush`/`LinearGradientBrush` objects into the resource dictionary.
- **FriendAvatarStore caches parsed JSON.** `LoadFile()` keeps the deserialized `StoredFile` in memory with a last-write-time check, re-reading from disk only when the file changes. `WriteFile()` updates the cache in sync. Avoids re-parsing the entire friend-avatar JSON on every poll.
- **WebView2 suspends sooner.** `EmbeddedBrowser.SuspendDelayMilliseconds` dropped 7000 ms → 3000 ms. Losing focus only lowers the renderer's memory target, and `OnWindowActivated` restores it. Nothing is disposed on focus loss. `ReleaseForMemory` disposes the WebView, and `_pendingUrl` is already cleared once a page has navigated, so calling it there leaves a blank page on the next focus - 1.0.86 did this for a hidden cached section, and the visible-page version of it was caught before shipping. Section eviction is the only thing that releases a browser.
- **Timers pause when minimized.** Clock timer skips `OnPropertyChanged` while minimized; instance history poll returns early when minimized.

### What 1.0.87 changed

- **Alt-tab no longer costs the page.** `EmbeddedBrowser.OnWindowDeactivated` called `ReleaseForMemory()` for any page that was not visible, which disposes the WebView of a cached section, so returning to that section came back blank. `MainWindow.OnWindowDeactivated` also cleared the decoded-image cache on every focus loss, so the next focus re-decoded every picture on screen. Focus loss now only lowers the WebView's memory target, and `OnWindowActivated` restores it. Disposal stays with section eviction.
- **The chatbox tick stopped enumerating processes on the UI thread.** `ChatboxViewModel.RefreshOutputMode` called `VrModeDetector.IsVrRunning`, which walks the process list for `vrmonitor` and `OVRServer_x64`, inside the UI-thread scan tick. The 1.0.86 log named it: `ui stall 828ms cpu=687ms of 828ms burning cpu across chatbox-tick`. It now probes on the pool (at most every 15 s) and applies the answer on the dispatcher, the same shape as `PollNowPlayingAsync`.

### What 1.0.88 changed

- **A section switch no longer reloads Discord.** `ShouldKeepSectionView` never named `discord`, so the LRU loop - or the lite-mode limit of one cached view - evicted the Discord section, `ReleaseBrowsers` disposed its WebView, and coming back loaded the page from scratch, which drops a voice call. The method's own comment already promised Discord stayed cached; the predicate enforces it now, because a call is not always audible and `IsDocumentPlayingAudio` cannot stand in for it.

### What 1.0.89 changed

- **A backdrop is never decoded above its source, and an animation has a frame budget.** Measured on
the running 1.0.88: 756 MB private memory, of which only ~196 MB was the managed heap (169 MB of it
large object heap), so guessing at caches would have missed it. The custom backdrop was a 280x275,
66-frame GIF and `ImageBackdrop` asked for 1280 px; WPF upscales, so it decoded 1280x1257, and
WpfAnimatedGif holds every frame decoded at once - about 405 MB for a 0.9 MB file. `ImageBackdrop`
now reads the frame count and source size from the header (`ReadImageInfo`, no pixels decoded) and
decodes at most the source width; `AnimatedBackdropWidth` additionally caps the whole animation at
`AnimatedBackdropFrameBudgetBytes` (96 MB), and returns 0 - still frame - when even 320 px wide
cannot fit. The profile backdrop had the same shape and the same saving.

### What 1.0.90 changed

- **Skia decodes WebP straight to the size the UI needs.** The old `TryDecodeSkia` decoded at the
source resolution with `SKBitmap.Decode`, resized, then copied the result into a managed array with
`GetPixelSpan().ToArray()` - a large-object allocation per picture (7.7 MB for 1600x1200), which is
what left freed large-object space behind. `DecodeSkiaScaled` now asks `SKCodec.GetScaledDimensions`
for a supported size, decodes into a native buffer (`Marshal.AllocHGlobal`, freed in `finally`) and
lets `BitmapSource.Create` copy out of it; measured in `ImageDecodeTests`, 1600x1200 at 96 px is
96x72 and 2,048 managed bytes. `DecodeSkiaFullSize` is the old path kept as the fallback when a
codec refuses to scale. `ToBitmapSource` passes `SKBitmap.GetPixels()` and its stride to WPF instead
of copying the frame into a managed array, so animation frames stopped allocating one too.
- **The animation decoder's `SKMemoryStream` is disposed.** `DecodeAnimationSkia` created one per
file inline and never freed it.

### What 1.0.91 changed

- **The animated backdrop plays its own frames.** `ImageBackdrop` no longer uses WpfAnimatedGif: that library keeps a writeable bitmap of every frame on top of the decoded frames (the 1.0.89 heap dump counted 148 of them, about 45 MB, for the 76 frames of the two shipped wallpapers). The control now decodes frames with Skia at the width it draws them, freezes them, and steps them with a `DispatcherTimer` at the file's own frame times and the 75% speed the old playback used. Measured on the shipped 66-frame 280x275 GIF: 66 frames, 20,328,000 bytes, 5,510 ms loop, 32 ms to decode. The `WpfAnimatedGif` package reference is gone; nothing else used it.
- **A backdrop animation is capped at 96 frames** (`MaxAnimatedBackdropFrames`). A dropped frame adds its time to the frame before it, so the loop keeps the file's length instead of speeding up, and only kept frames count against `AnimatedBackdropFrameBudgetBytes` - a long GIF is thinned rather than shrunk to a blur.
- **Measured 1.0.90 before changing anything:** 786-797 MB private on the running copy, of which the managed heap was 460 MB at its peak and 99 MB live after a collection (661,754 objects); the large-object heap stayed committed at 226 MB, which a running process never gives back. The browsers and Discord are separate processes, so they do not explain the app's own number.

### What 1.0.92 changed

- **A JSON store is streamed to its file, not built in memory.** A 45-second allocation trace of the running 1.0.91 put `ActivityStore.WriteJson` first by a wide margin: 55.65 MB in that window (23.62 MB `System.String`, 31.94 MB `System.Byte[]`), because each save serialized the whole store into a JSON string and then copied that string into a byte array. `AtomicFile.WriteJson` serializes through a 64 KB `IBufferWriter` sink into the staging file and renames it as before; measured in `AtomicFileJsonTests`, writing a 2 MB document allocates 66,728 bytes. The other stores were not in the trace's hot path, so they still use `WriteAllText`.
- **The media gallery pager resets its scroll.** It was the only paged section with no `ScrollViewerBehavior.PageToken`, so Next left the gallery wherever the last page was scrolled to. `MediaLibraryViewModel.PageToken` moves whenever the page or a filter is published, and the gallery's `ScrollViewer` binds it.
- **A page reset survives the layout that follows it.** `ScrollViewerBehavior.OnPageTokenChanged` used to call `ScrollToTop` once, before the next page's rows were published; the list then laid out with the row it still had selected or focused and could pull the viewport back down. It now also resets on the dispatcher at `Background`, after that layout pass. `PagingScrollTests` pins the four orders (rows before the token, rows after it, a focused Next button, a plain page viewer).

### What 1.0.93 changed

- **History is read once and appended to.** The 1.0.91 allocation trace put `InstanceHistoryStore.ReadEvents` second: 20.7 MB of strings in 45 seconds. Every new log event cleared the entries cache, and the next refresh re-read and re-parsed all ~27k join/leave rows - six strings and a `created_at` parse each - only to regroup them. `ReadEvents` keeps the parsed `EventRow`s per account and checks `COUNT(*)`/`MAX(id)` against the table; a live append reads only rows above the cached id, so those strings are never rebuilt. Measured in `InstanceHistoryTests`: 1,385,272 B for a one-event refresh against 2,168,848 B for a cold full read of the same 1,200 events.
- **Nothing unsafe is appended.** A delete changes the count and an out-of-order import sorts before the last cached row, so either one falls back to a full read; `AssignPlayerUserId` edits rows in place and clears the parsed rows explicitly, because neither the count nor the highest id can see an UPDATE. `AppendedEventsProduceTheSameHistoryAsAFreshRead` compares the appended result with a fresh store reading the same database.
- **Every pager returns to the top.** `PagedList<T>` - friends, notifications, both group lists, continuity sessions/people/worlds, the context roster, friends here, recent people, the instance roster, friend locations and recent players - had no page token, so Previous/Next only swapped rows and left the list at the offset the reader had scrolled to. `PagedList.Publish` bumps `PageToken` when the visible page changes, and `Controls/ListPager` answers it with `ScrollViewerBehavior.ResetPageScroll`, which resets the list bound to that page's `Items` and every `ScrollViewer` above the pager. A presence refresh that keeps the page does not bump it, so live updates never yank the reader away.
- **The group popup's moderation roster has a token too.** It shared the popup scroll with the member list but never moved the scroll itself; `GroupModerationViewModel.MembersPageToken` now feeds `GroupViewModel.PopupPageToken`, which the popup binds, so both pagers open their next page at the top.

### What 1.0.94 changed

- **A history refresh reuses the grouping.** 1.0.93 stopped re-reading the parsed rows, but every new event still regrouped all of them and rebuilt every entry (players, visits, times), which was most of what was left of the refresh's allocation. Grouping is strictly sequential: a run of consecutive events at one location can only merge with the run before it, and once a newer run exists for that account nothing can join it again. `InstanceHistoryStore` keeps the `GroupState` list and each finished group's built entry in the same row cache, appends only the rows above `GroupedCount` (`GroupRows`/`AppendGroups`), and rebuilds just each account's last, open group, whose `LeftAt` and `LastSeenAt` still move. A cached entry is reused only while its stored `LocationRow` still equals the one just read, so metadata enriched after the first read still takes effect. Measured in `InstanceHistoryTests` with 1,200 events: a one-event refresh allocates 20,192 B, against 2,135,392 B for a cold read and 1,385,272 B for the same refresh in 1.0.93.
- **The reuse is checked, not assumed.** `RefreshingHistoryReusesFinishedGroupsAndRebuildsOnlyTheOpenOne` asserts the finished rows come back as the same objects while the open one is rebuilt; `AppendingAcrossGroupBoundariesProducesTheSameHistoryAsAFreshRead` compares nine appends (a self leave, a world change, two accounts) with a fresh store, players and visits included. Deletes, in-place edits and out-of-order imports still replace the rows and rebuild every group.
- **An invite VRChat has expired says so.** Invites and invite requests are persisted between launches, but VRChat only accepts a reply to recent notifications; an older one answers 404 "Notification not found" even though the row is still listed, and no client can send that reply. `MainViewModel.NotificationHasExpired` recognises exactly that answer: the reply composer closes with an explanation and the stale row is removed, and a plain Decline of the same row removes it quietly instead of showing the raw server text.

### What 1.0.95 changed

- **Location metadata is read once, not on every refresh.** With the grouping cached, `ReadLocations` was the biggest allocation left in a history refresh: it built a dictionary of every visited world (name, thumbnail, creator, owner) on every poll. Measured in `InstanceHistoryTests` with 300 stored locations: 249,768 B per one-event refresh, against 18,824 B now. `ReadLocationsCached` keeps the dictionary per account and validates it with `_locationVersion` (bumped by every write that touches `gamelog_location`) plus a `COUNT(*)`/`MAX(last_updated_at)` check for another store on the same database. The dictionary is shared and never mutated.
- **Clear notifications one by one or all at once.** `ClearNotificationCommand` drops one row; `ClearAllNotificationsCommand` drops the list. Both add the ids to `_clearedNotificationIds` and persist, so a refresh does not re-add what the user cleared. Clearing is local: it does not hide or answer anything on VRChat.

### What 1.0.96 changed

- **Each notification filter can be marked read or cleared on its own.** The header keeps `MarkAllReadCommand`/`ClearAllNotificationsCommand` for the whole list; the filter row adds `MarkFilteredNotificationsReadCommand` and `ClearFilteredNotificationsCommand`, which work from `_notificationsView` - the selected filter tab plus the search box - so they touch exactly the rows on screen. Clearing still adds the ids to `_clearedNotificationIds` and persists; marking read still goes through VRChat one row at a time.
- **The filter row shows its own counts.** `HasVisibleNotifications`/`HasUnreadVisibleNotifications` are raised from `UpdateNotificationFilterState`, and both buttons hide when the filter has nothing (or nothing unread) to act on.

### What 1.0.111 changed

- **Escape steps out of a session detail before it closes the history popup.** 1.0.110's
  `IsOpen` branch closed everything, so reading a session and pressing Escape lost the list. The detail
  is its own layer (`Panel.ZIndex="33"` over the list's `32`), so `IsDetailVisible` is now checked
  first and runs the list's `BackCommand`; the `IsOpen` branch then closes the popup on the next
  press. `InstanceHistoryDetailOverlay` is the detail grid's `x:Name` for the test.
- **Tests: 987.** `EscapeLeavesTheSessionDetailBeforeItClosesInstanceHistory` opens a real row from a
  temp store, presses Escape once (detail collapsed, popup open, no selected session) and again (popup
  closed).

### What 1.0.110 changed

- **Escape closes instance history and avatar details.** Both are full-window popups but neither was in
  `OnPreviewKeyDown`'s chain. Avatar details (`Panel.ZIndex="40"`) now closes through
  `CloseAvatarDetailsCommand` before the launch chooser and the friend detail, and instance history
  (`32`) closes through `InstanceHistoryViewModel.CloseCommand` after the friend detail (a profile
  opened from one of its rows sits at `34`, above it) and before the friend popups.
- **Both overlays are named.** `AvatarDetailsOverlay` and `InstanceHistoryOverlay` are the root grids'
  `x:Name`s, so `PopupEscapeTests` finds them in a real window and checks them collapsed.
- **Tests: 986.** `PopupEscapeTests` covers all five popups, and `OnePressClosesThePopupOnTopOnly`
  opens all five and closes them one press at a time in z-order: avatar details (40), instance history
  (32), recent players (25), friend changes (24), friend locations (23). Escape closes history as a
  whole, so an open session detail goes with it - the list's Back button still steps back to the list.

### What 1.0.109 changed

- **Escape closes the friend changes popup too.** 1.0.108 added friend locations and recent players to
  the chain, but friend changes (`Panel.ZIndex="24"`) sits between them in the same family and was
  still missing, so the key left it open and the window behind it answered. `IsFriendChangesOpen`
  now closes through `CloseFriendChangesCommand`, between recent players (25) and friend locations
  (23), and marks the event handled. One press closes the popup on top, the next the one under it.
- **The overlay is named.** `FriendChangesOverlay` is the root grid's `x:Name`, so `PopupEscapeTests`
  finds it in a real window and checks it collapsed after the press.
- **Tests: 984.** `EscapeClosesTheFriendChangesPopup` joins the two other popup tests, and
  `OnePressClosesThePopupOnTopOnly` now opens all three and closes them in z-order, one press each.

### What 1.0.108 changed

- **Escape closes the friend locations and recent players popups.** Both fill the window with a dim
  backdrop and a Close button, but `MainWindow.OnPreviewKeyDown` had no branch for either, so the key
  left them open and the window behind them still answered it. Their branches now sit after the
  friend detail and before browser focus, in z-order: recent players (`Panel.ZIndex="25"`) close
  through `CloseRecentPlayersCommand`, friend locations (`23`) through `CloseFriendLocationsCommand`,
  and each marks the event handled. One press closes the popup on top, the next closes the other.
- **The two overlays are named.** `FriendLocationsOverlay` and `RecentPlayersOverlay` are `x:Name`s on
  their root grids, so `PopupEscapeTests` can find them by name in a real window and check the
  collapsed state after the press.
- **Tests: 983.** `PopupEscapeTests` pins both popups and `OnePressClosesThePopupOnTopOnly` pins the
  order. `TestHooks.AttachRecentPlayersViewModel` attaches that view model the way
  `AttachInstanceHistoryViewModel` attaches history, because the shell builds it only inside
  `InitializeAsync` and the suite skips startup restore.

### What 1.0.107 changed

- **Escape closes the sign-out card and never signs out.** `OnPreviewKeyDown` already answered Escape
  for the composers, the world dialog, the friend detail and browser focus, but not for the card
  1.0.106 added, so the press fell through to the window behind it. The `IsLogoutConfirmationOpen`
  branch is now first in the chain (the card is the topmost overlay); it runs `CancelLogoutCommand`
  and marks the event handled. `EscapeCancelsTheConfirmationAndNeverSignsOut` raises a preview
  `Key.Escape` on a real window and pins the handled flag, the collapsed card and `IsLoggedIn`.

### What 1.0.106 changed

- **Sign out asks first.** `LogoutCommand` used to sign out on the click, and it sits beside Refresh in
  the top bar and inside the Settings account card. It now only opens `IsLogoutConfirmationOpen`,
  which shows a bordered card over the window; `ConfirmLogoutCommand` is the one path to
  `LogoutAsync` and `CancelLogoutCommand`/the backdrop close it. `SignOutLocally` closes the card too,
  so a session that expires while it is open does not leave it up. The expiry and two-factor-cancel
  paths still sign out (or cancel) without asking, because there is no signed-in session to protect.
- **The overlay is `Panel.ZIndex="30"`**, above the friend-locations (23) and recent-players (25)
  popups, so it cannot be clicked through. `SignOutConfirmationTests` pins the commands and finds
  `SignOutConfirmationOverlay` in a real window, hidden before and visible after.

### What 1.0.105 changed

- **Every switch settles.** 1.0.104 released memory only when a page was given up - leaving Media or the avatar library, or pushing a page out of the two-view cache. Two light tabs both stay cached, so Worlds -> Friends evicted nothing and settled nothing. `ShowSelectedSection` now schedules the settle on any real switch (selecting the open section again is not one; startup is not one), still coalesced into one collection a second after the last hop.
- **The quiet-heap cost is 10-12 ms** (three runs in the probe), against 21 ms and 267 MB handed back for the 256 MB-of-garbage probe. `MainWindow.SettleCount` exists for `SwitchingBetweenTwoLightSectionsStillSettles` and `ReturningToTheSectionThatIsAlreadyOpenDoesNotSettle`.
- **A light tab still keeps its rows for the 90 s grace**; what the settle returns there is its garbage and the decode cache, not its data. Only Media and the avatar library release data on leave.

### What 1.0.104 changed

- **A tab switch gives the memory back.** `MainWindow.ScheduleSettle` used to send `GC.Collect(2, GCCollectionMode.Optimized, blocking: false)`, a hint the GC may ignore, and skipped any switch within 20 s of the last one. The log showed Media at `683 -> 1095 -> 1178 MB` working set and `1178 MB` still there after switching to Worlds. `Services/MemorySettle` now drops the decoded-image cache, collects with `GCCollectionMode.Aggressive`/`Compacting` plus `LargeObjectHeapCompactionMode.CompactOnce`, waits for finalizers and collects again, then logs `memory: settled in Xms freed=YMB`. Every switch that hands a page back schedules one (coalesced, one second after the switch); 1.0.105 extended that to every switch.
- **Aggressive is the mode that matters.** Probe with the metadata pass's buffer pattern (256 MB of dropped 256 KB/512 KB buffers, 48 MB library live): peak 339 MB private -> old hint 340 MB (0 freed) -> forced collect only 327 MB (12 freed, managed 310 -> 55 MB but 307 MB still committed) -> `MemorySettle` 72 MB (267 MB freed, 55 MB committed), 21 ms for both passes. A plain forced `GC.Collect` reclaims the heap but leaves the segments committed, which is exactly the reported "it stays where it is".
- **Media is released on leave, not after a grace period.** `MediaDataGrace` is zero like avatars. `MediaLibraryViewModel.ReleaseData` now returns bool, closes the viewer, cancels the metadata pass and drops `_allItems`, `_itemsByFolder`, `_photoCache`, the 48 reusable `PageCards` (through `RebuildPage`) and the filter rows; it refuses (false) while a scan runs, so `MainWindow` retries via the renamed `SectionReleaseRetryDelay`. `DataGraceFor` in `MainWindow` decides which sections are released immediately.
- **`MediaThumbnailRenderTests` no longer pins the `BitmapImage` instance** after a hidden page is shown again - the settle drops the shared decode cache, so a card may decode again; the render check still asserts the same picture draws.

### What 1.0.103 changed

- **No image decodes wider than twice the box it draws in.** `ImageDecodeBudgetTests` parses every `AsyncImage` in every `.xaml`, takes the nearest ancestor that names a numeric `Width`, and fails when `DecodeWidth` is above twice it (96 px floor for the tiny avatars). Twelve sites were over: the enlarged boop card decoded 640 px into a 160 px box (921,600 B -> 230,400 B for a 1080p source), the group card 256 into 64 (262,144 -> 65,536), the notification chips 220 into 52 (193,600 -> 43,264, twice), the invite world 360 into 92 (292,320 -> 76,544), the media viewer's world chip 240 into 76 (157,440 -> 63,232), the avatar detail 300 into 122 (360,000 -> 238,144), the avatar grid 256 into 112, and four avatar chips 128/112 into 52-56. About 1.5 MB less decoded pixel data for one screen holding one of each, and the same 24 MB loader cache now holds roughly four times as many of them.
- **`MediaItem.Thumbnail` is gone.** Nothing called it; the grid decodes through `AsyncImage` at 160 px, so the per-item cache only kept a second, 320 px decode of every file the library holds for the session. `LoadBitmap` stays, because the viewer uses it.
- **Decode the size you draw at 200% scaling.** `DecodePixelWidth` is physical pixels: a 52 DIP box is 104 px on a 200% display, so twice the box is the last useful width. Do not "improve" a small box back to a round 256 - it costs bytes and buys nothing on screen.

### What 1.0.102 changed

- **A media card decodes at the size it draws.** `ImageLoader.LoadAsync(path, 160)` was measured returning a 1920x1080 frame (8,294,400 B) instead of 160x90 (57,600 B): forty-eight cards held about 398 MB of pixels, which is the gigabyte the Media tab reached on a real library. Two bugs stacked. `TryDecodeWpf` set `BitmapCreateOptions.IgnoreImageCache`, and WPF's `ImagingCache` is keyed by URI - a `StreamSource` has none, so `EndInit` threw `ArgumentNullException` and every decode fell back to Skia. Then `DecodeSkiaScaled` accepted the size `SKCodec.GetScaledDimensions` offered, and PNG (VRChat's format) answers with the source size, so it decoded ten megapixels and reported success; the resize fallback never ran.
- **Neither fix alone is enough.** Without `IgnoreImageCache` WPF decodes PNG/JPEG at `DecodePixelWidth` again; `DecodeSkiaScaled` now returns null when the codec's answer is the source width, so `DecodeSkiaFullSize` resizes WebP-class formats that cannot scale, and JPEG still uses its whole-factor scale. `AScreenshotIsNotDecodedAtItsSourceResolution` and `ALocalScreenshotLoadsAtTheWidthTheMediaGridAsksFor` pin both halves through the path the grid uses.
- **The open history row is the newest-starting group the log never saw you leave.** After 1.0.101 grouped by `(account, location)`, "open" was still decided by position, so an import recorded after the live session took it, and the live row came back with a leave time it never had. `BuildEntries` now prefers a group with no self leave and, among those, the newest start; a leave-less row older than another leave-less row stays closed, so a crash's missing leave does not keep growing forever. `AnImportedSessionRecordedAfterTheLiveOneDoesNotFinishIt` and `OnlyTheNewestLeaveLessSessionStaysOpen` pin both cases.
- **Media model data is not the problem.** 4,000 pictures with 400 sessions of 25 players retain 1.4 MB; do not move work off the model to chase a memory report before checking the decoded pixels.

### What 1.0.101 changed

- **A session is grouped by account and location, not by "the previous row".** Rows are read in timestamp order (`ORDER BY created_at_ts, id`), but a row recorded later can carry an older timestamp - an import, a replay, two writes a minute apart. Landing between a session's join and its leave, it used to end the run, so the session's leave started a second row at the same location (a leave-only row whose start and end are the same moment). With 500 stored sessions plus one such row, the store returned 502 rows with two repeated locations and the popup showed 498. `GroupAll`/`AppendGroups` now keep the open `GroupState` per `(account, location)` in `EventRowCache.OpenGroups` and hand a row to its own session wherever it sorted; `AddRowInTimeOrder` keeps a group's rows in timestamp order because `BuildEntry` reads `Rows[0]` and `Rows[^1]`.
- **The cap was not the cause.** `MaximumEntries = 500` takes the newest groups in reverse order and never splits one. Do not "fix" a short list by raising it.
- **`ALateRowStampedInsideASessionDoesNotSplitIt`** pins the two-row result and each row's real times. `ViewModelCollapsesRepeatedVisitsToTheSameInstanceButKeepsOtherInstances` used to expect three source sessions for two visits; that expectation was the bug written down, and it is two now.
- **Do not go back to grouping by `groups[^1]`.** The 1-hour tolerance and the self-left rule stay in `StartsNewGroup`; only *which* group a row is offered to changed.

### What 1.0.100 changed

- **A history refresh reuses the rows that did not change.** 1.0.98 made a poll with no new events free, but the first poll after any change still rebuilt all 500 `InstanceHistoryItem`s. `RefreshAsync` now keeps the shown item per location and reuses it when `SameFinishedEntry(shown.Entry, entry)` says the merged entry is the same: world name/thumbnail/creator/owner, join/leave times, `IsPhotoSnapshot`, the source session ids and every player's key, display name, avatar and visits. Measured with 200 finished sessions plus one live join: 1,354,840 B per refresh with reuse disabled, 159,640 B with it.
- **The open row is never reused.** Its `LastSeenAt`, duration and its players' open visits move every poll, so `SameFinishedEntry` returns false for a row with `LeftAt is null`; that one row is still rebuilt. Add a field to the comparison only after checking the row actually displays it - a missing field freezes it.
- **The collection is only republished when the item sequence changes** (`SameItems`), so a poll that reuses everything leaves the `ObservableCollection` alone. A single changed row still replaces the collection, which rebuilds the realised containers; the row objects are kept.
- **`ARenamedHistoryRowIsRebuiltAndTheRestAreReused`** pins both halves: a metadata rename produces a new item with the new name, and the other location keeps the same object. Do not reuse `InstanceHistoryItem`s across a metadata change to save the allocation; the row would keep the old name.

### What 1.0.99 changed

- **The activity feed skips an unchanged friend list.** `ApplyFriends` runs on every presence refresh with the complete list. It now computes `FriendSignature.Compute(friends)` and returns after updating `_previousFriends`, unless a bio change was observed - the same rule the online poll (`MainViewModel`/`ApplyOnlineFriends`) already used. Measured with 2,000 friends: 1,445,328 B per unchanged poll before, 81,736 B after. What is left is `FindProfileAvatarChanges` and `ObserveFriendBios`, which must still see every snapshot.
- **The feed fingerprint covers the avatar id.** It hashes id, display name, username, location, status, status description, `CurrentAvatar` and `CurrentAvatarImageUrl`. Dropping `CurrentAvatar` makes the avatar-change rows disappear (the two existing avatar tests caught exactly that); dropping the display name freezes a rename in old rows. `FindProfileAvatarChanges` fills its previous-lookup dictionary in a loop instead of `GroupBy().ToDictionary()`.
- **The notification store does not rewrite an unchanged file.** `PersistNotifications` saves on every notification refresh. `NotificationPersistenceStore.Save` now hashes the account's JSON through a hashing `IBufferWriter` (FNV-1a, no document string) and returns when it matches the last write for that account. Measured with 300 boops + 400 cleared ids: 1,502,080 B and a 110 KB rewrite per unchanged save, 80,848 B and no write after. A changed account writes the whole file through `AtomicFile.WriteJson`, so the file is compact (110 KB -> 65 KB there). `_lastSavedAccounts` is dropped on a failed save so a cache entry can never mask a write.
- **World search rows were measured and left alone**: 24 `WorldItem`s cost 1,536 B, because the strings are computed on bind and the page owns no commands.

### What 1.0.98 changed

- **A history poll with no new events keeps its rows.** `InstanceHistoryStore.Revision` counts every write (add, merge, delete, delete-many, metadata, player events, id repair). `InstanceHistoryViewModel.RefreshAsync` returns early when the revision is unchanged and rows exist, so it no longer rebuilds 500 `InstanceHistoryItem`s (players, timeline, seven commands each) for nothing. Measured with 500 sessions: 3,224,536 B per idle poll before, 40 B after. `InstanceHistoryItem.RefreshOpenDuration` keeps the running session's time honest when the rebuild is skipped.
- **`Close()` releases the built rows** and resets `_lastRevision`, so reopening rebuilds from SQLite. About 1.9 MB freed for a 500-session history. Any code that reads `Entries` after closing must refresh first.
- **A real change still rebuilds**: a write bumps the revision and the next poll rebuilds (11.1 MB for 500 sessions, only when something changed). Do not make the store's reads bump the revision, or the skip never fires; do not drop `BumpRevision` from a write path, or that change never reaches the list.

### What 1.0.97 changed

- **A bulk clear confirms before it removes anything.** `ClearAllNotifications`/`ClearFilteredNotifications` now only open `IsClearConfirmationOpen` with a `ClearConfirmationText` naming the count; `ConfirmClearConfirmationCommand` runs `PerformClearAllNotifications`/`PerformClearFilteredNotifications`, `CancelClearConfirmationCommand` closes the panel. A single row's Clear stays one click. Changing the filter closes the panel, because a pending "this filter" clear belongs to the filter it was opened on.
- **The filter commands follow the selected filter.** `UpdateNotificationFilterState` raises `ClearFilteredNotificationsCommand` and `MarkFilteredNotificationsReadCommand`, so picking a filter re-evaluates its buttons instead of leaving them disabled until the next clear.

### Do not undo these

1. **No `DropShadowEffect`.** If a panel now reads as too flat, give it a baked edge - a second
   `Border` or a thicker `BorderBrush` - rather than restoring the blur.
2. **Lite mode stays one switch.** `AppPerformance.LiteMode` is the single truth, persisted as
   `CompanionSettings.LiteMode` (`bool?`, null means auto-detect from `RenderCapability.Tier` below
   tier 2) and applied through `AppPerformance.Apply`/`Set`. `Set` raises `Changed` on the calling
   thread; `MainWindow.OnPerformanceChanged` marshals to the dispatcher and evicts pages down to the
   new `MaxCachedSectionViews`, and `MainViewModel.ApplyPerformanceProfile` restarts the activity
   timer with the new interval. Do not add a second "low spec" flag: two switches will disagree and
   the one nobody wired is the one a user turns on.
3. **Every new decode path clamps.** Call `ImageLoader.ClampDecodeWidth` before decoding, or lite
   mode's biggest memory win is lost for that call site. `AppPerformanceTests` pins the clamp.
4. **Log, file and network reads stay off the UI thread.** Follow the 1.0.83 shape: `Task.Run` the
   blocking part, a bool to stop overlap, re-check the account after the await, apply on the
   dispatcher. A job that runs on the UI thread and can exceed `UiJobTrace.SlowJobMilliseconds`
   (200 ms) belongs in `UiJobTrace.Shared.Begin("name")`, so a freeze names it.
5. **Do not re-add `EmptyWorkingSet` or `SetProcessWorkingSetSize`.** They shrink the reported
   working set and slow the app down. Memory is given back by `MemorySettle.Run` on a section
   switch: drop the decoded-image cache, then `GC.Collect(GC.MaxGeneration,
   GCCollectionMode.Aggressive, blocking: true, compacting: true)` with
   `LargeObjectHeapCompactionMode.CompactOnce`, `WaitForPendingFinalizers`, and one more pass. Do
   not put it back on a hint (`Optimized`, `blocking: false`) or on a plain `Forced` pass: the hint
   is allowed to do nothing (0 MB freed in the 1.0.104 probe), and a forced pass collected the heap
   but kept 307 MB committed against a 55 MB heap. `ShowSelectedSection` schedules it on every
   switch (1.0.105), so a light tab that stays in the two-view cache still settles; do not move it
   back inside `EvictSectionView` alone.
6. **`FriendsSignature` stays a hash**, over the same fields as before.
7. **Page cache changes go through `MaxCachedSectionViews` and `EvictSectionView`.** Eviction calls
   `ReleaseBrowsers` to drop the page's WebView2s; skipping it leaves a hidden browser running.
   `ShouldKeepSectionView` pins an open Discord page (and a focused or busy Brave/Resources page)
   above the limit, because evicting a browser view disposes its WebView and the reload that follows
   drops a Discord voice call. `SectionCacheTests` pins that predicate; do not narrow it back to the
   cache limit alone.
8. **A backdrop decode never exceeds the source width.** `ImageBackdrop.ClampBackdropWidth` exists
because `DecodePixelWidth` above the file's own width upscales it (WPF really produces that size),
and an animated GIF pays for it on every frame; `BackdropDecodeTests` pins the no-upscale rule, the
   frame budget and the still fallback. Do not raise the budget without measuring the frame set: 66
   frames at 1280 px was 405 MB, not a few tens of megabytes. The frames come from
   `ImageBackdrop.DecodeAnimation` now (rule 11), which clamps the same way.
9. **Skia decoding goes through `DecodeSkiaScaled` first.** Do not reach for `SKBitmap.Decode` on a
full image and then resize it: that materialises the source resolution, and `GetPixelSpan().ToArray()`
adds a managed copy of it on the large object heap. Decode through `SKCodec` at a supported scale,
write into native memory, and let `BitmapSource.Create` copy once; `DecodeSkiaFullSize` exists only as
the fallback, and `ImageDecodeTests` pins both the scaled size and the allocation bound.
10. **WebView2 suspension already exists.** `Controls/EmbeddedBrowser` suspends a hidden page with
   `TrySuspendAsync` after 7 seconds (now 3), sets a low memory target while hidden, and releases the
   whole WebView when the section is evicted. Do not add a second suspension mechanism.

11. **The backdrop animation is ours.** Do not put WpfAnimatedGif back to save code: it decoded and then copied every frame, which cost about 45 MB over the frames themselves. `ImageBackdrop.DecodeAnimation` composites through Skia (passing the previous frame index so disposal and partial frames are right), clamps to the source width, and holds only the frames it keeps; `BackdropDecodeTests` pins the width, the frame cap, the loop-time rule and a real GIF round trip.

12. **Store JSON goes to the file as it is written.** `AtomicFile.WriteJson` exists because serializing to a string first costs a second copy of the whole document (a 6 MB store became a 12 MB UTF-16 string plus a 6 MB byte array per save, the largest large-object churn in the process). Do not swap it back to `JsonSerializer.Serialize(value)` + `WriteAllText`; `AtomicFileJsonTests` pins the allocation bound and the staged rename.

13. **A pager moves its page token when the page changes, and the pager resets the scroll.** The hand-written pagers bind `ScrollViewerBehavior.PageToken` on the element that owns their scroll (`WorldsPageToken`, `AvatarPageToken`, `ResultsPageToken`, `ActivityFeedViewModel.PageToken`, `MediaLibraryViewModel.PageToken`, `GroupViewModel.PopupPageToken`). Lists on `PagedList<T>` bind nothing: `PagedList.Publish` bumps `PageToken` only when the visible page changes, and `Controls/ListPager` calls `ScrollViewerBehavior.ResetPageScroll`, which resets the list bound to the page's `Items` and every `ScrollViewer` above the pager. Do not bump the token on a refresh that keeps the same page, or every presence poll would yank the reader to the top. The reset runs twice - immediately and after the layout pass - because rows published after the token, or a row left selected, can otherwise pull the viewport back down. `PagingTests`, `PagerLayoutTests`, `MediaPagingTests` and `PagingScrollTests` pin the token rules and the real-window resets.

14. **Instance history rows are parsed once and appended to.** `InstanceHistoryStore.ReadEvents` keeps the parsed `EventRow`s per account in `_eventCaches` and trusts them only when `COUNT(*)` and `MAX(id)` still match; a live append reads just the rows above the cached id. That removed the second-biggest allocation in the 1.0.91 trace. Anything that edits rows in place (`AssignPlayerUserId`) or removes them must call `InvalidateEventRows()`; appends must not clear the cache, because that is the whole saving. `InstanceHistoryTests` pins the allocation bound, append-versus-fresh accuracy and the out-of-order import fallback. 1.0.94 keeps the grouping and the finished entries on that same cache entry (`GroupRows`, `GroupState`), so a refresh regroups only the appended rows and rebuilds only the open group; a cached entry is reused only while its `LocationRow` is unchanged.

15. **Invite replies only work on recent notifications.** `POST invite/{id}/response` is the documented route for both `invite` and `requestInvite`, but VRChat drops old notifications from the set it will answer and replies with 404 "Notification not found". Invites are persisted on purpose, so a row can outlive that set. Treat the 404 as expiry (`NotificationHasExpired`): explain it, remove the row, and never report it as a successful decline. Do not "fix" it by trying another endpoint; there is nothing left on the server to answer.

16. **Stored location metadata is cached per account.** `ReadLocationsCached` hands out one shared dictionary; nothing may mutate it. Any path that inserts, updates or deletes a `gamelog_location` row must bump `_locationVersion` (the continuity importers only read the table, and the `COUNT(*)`/`MAX(last_updated_at)` check covers a store from elsewhere), or a rename will keep showing the old world name. `ARefreshDoesNotReReadEveryStoredLocation` pins both the allocation bound and the rename-after-warm accuracy.

17. **Clearing a notification is local and persistent.** `ClearNotification`/`ClearAllNotifications` add the ids to `_clearedNotificationIds`, remove the rows and call `PersistNotifications`; they do not call VRChat. Decline (`DeclineNotificationAsync`) is the one that hides or answers on the server, and Accept/reply are unchanged. Do not clear `_clearedNotificationIds` on a refresh - that is what stops a cleared invite from coming back until a new notification with a new id arrives. The per-filter versions (`ClearFilteredNotifications`, `MarkFilteredNotificationsReadAsync`) read `_notificationsView` rather than `Notifications`; do not switch them to the full collection, or the buttons would repeat the header's Clear all. `NotificationPersistenceTests.MarkingOneFilterReadOnlyMarksWhatItShows` and `ClearingOneFilterLeavesTheOtherFiltersAlone` pin both rules. The bulk clears go through `ConfirmClearConfirmationCommand`; do not call the `Perform*` methods from a button, or the confirmation is bypassed.

18. **The Media tab hands its library back when it is left.** `MediaLibraryViewModel.ReleaseData`
   clears `_allItems`, `_itemsByFolder`, `_photoCache` and the open viewer, rebuilds `PageCards` so
   the 48 reusable cards stop holding rows, and cancels the background metadata pass; it returns
   false while a scan is running so the window retries. Leaving Media releases it at once
   (`MediaDataGrace` is zero, like the avatar library) instead of after the 90 s grace, because the
   rows plus cards plus the pass came to hundreds of megabytes while the disk index still paints the
   grid on return. `LeavingTheMediaSectionHandsItsLibraryBackStraightAway` and
   `ReleasingTheLibraryDropsTheRowsTheCardsAndTheViewer` pin it.

19. **A decode width stays within twice the drawn width.** `ImageDecodeBudgetTests` fails the suite for any `AsyncImage` whose `DecodeWidth` exceeds 2x the nearest ancestor `Width` (96 px floor). It skips a picture with no sized ancestor - a full-width hero or lightbox - so those still need a look by eye. When adding an `AsyncImage`, copy    the width from the box it sits in rather than inventing one.

20. **Sign out goes through its confirmation.** `LogoutCommand` opens the card; only
   `ConfirmLogoutCommand` calls `LogoutAsync`. Do not bind a Sign out button straight to `LogoutAsync`
   or to a new command that signs out, and do not remove the `SignOutLocally` close: the saved
   cookie is deleted with no backup, so an accidental click is unrecoverable. The expiry paths
   (`SignOutLocally` from auth failures) must stay prompt-free. Escape must cancel the card through
   `CancelLogoutCommand` and never reach `LogoutAsync`; keep its branch first in
   `OnPreviewKeyDown` so it runs before the overlays underneath. `SignOutConfirmationTests` pins all
   of it, including `EscapeCancelsTheConfirmationAndNeverSignsOut`.

21. **A full-window popup needs an Escape branch.** The chain in `OnPreviewKeyDown` currently runs
   the sign-out card, the composers, avatar details, the launch chooser, the world dialog, the friend
   detail, instance history, recent players, friend changes, then friend locations, then browser
   focus. A popup that covers the window and ignores Escape leaves the key live behind its backdrop;
   give it a branch that runs its own close command and sets `e.Handled`, and keep the order so the
   topmost popup is what closes first. A popup with layers of its own checks the inner one first: the
   instance history detail runs `BackCommand` before the popup's `CloseCommand`, so Escape steps back
   to the list and closes the popup only on the next press. `PopupEscapeTests` pins all five popup
   branches, the detail step and the order.


### Virtualization: what is real and what is not

- `Controls/VirtualizingWrapPanel` is a real virtualizing panel for fixed-width card grids. It finds
  the page's `ScrollViewer`, keeps only the cards in the viewport plus `CacheViewports`, and reports
  the full height so the page scrollbar stays honest. The worlds list and all three avatar grids use
  it, and `AvatarTabSwitchTests` finds it there. Its `EstimatedItemHeight` is the card's measured
  height: if a card's layout changes, update that number or the scrollbar and the realized band
  drift.
- The **activity feed is not actually virtualized**. `ActivityFeedViewModel.PageSize` is 25 and the
  list uses a `VirtualizingStackPanel`, but it sits in a `StackPanel` inside the page's
  `ScrollViewer`, so it is measured at infinite height and realizes all 25 rows. That is safe at 25
  rows and is why the panel choice changed nothing on screen; it is not a claim that a much longer
  feed would stay cheap.
- The **media library is paged, not virtualized**, on purpose: 48 fixed `MediaCard` slots in
  `MediaLibraryViewModel.PageCards`, reused across pages. Do not turn it into an unbounded
  collection.
- Every other `WrapPanel` in the window is a short chip, tag or filter list. Leave it: a virtualizing
  panel there buys nothing and changes focus order and hit-testing.

### Open work

1. **Confirm the freezes are gone.** The 1.0.80 log had 26 stalls of 5.2-6.1 s in about nine
   minutes, `activity-poll` at an average 1480 ms, and `instance-history` at 238-299 ms on the UI
   thread. No 1.0.85 log has been read yet. Ask the user for
   `%LOCALAPPDATA%\VrcCompanion\lifecycle.log` - reading it is safe; launching the app is not, it
   writes the real profile - then compare against those numbers.
2. **The activity feed could be truly virtualized**, but only by restructuring the page: give the
   list its own bounded scroll host (a `*` row in the page grid, with the header and filters outside
   it) or a row-oriented viewport-aware panel. Paging must survive: `ActivityFeedViewModel.PageToken`
   and `Behaviors/ScrollViewerBehavior` are what reset the scroll when the page changes, and the
   pager drives `PreviousPageCommand`/`NextPageCommand`. Ship it as its own change, with a test that
   paging still resets scroll, and check the wheel and the nested scrolling by hand. Do not do it as
   a drive-by while fixing something else.
3. **Look at the six panels that lost their shadow** - notification detail, boop preview, group
   details, friend detail, avatar detail, and the browser shields panel. None were inspected on
   screen after the change, because that needs the GUI. If one reads as detached from its backdrop,
   add a baked border rather than the blur.

The records of the pass are in `artifacts/`: `test-run-1083.log` to `test-run-1085.log`, and
`release-notes-1.0.83.md` to `release-notes-1.0.85.md`, which say what each release claimed and what
it left undone. Read those instead of trusting a summary.

## Reading a freeze from the lifecycle log

`%LOCALAPPDATA%\VrcCompanion\lifecycle.log`, written through
`UserDataPaths.Combine("lifecycle.log")`. The lines that matter:

    ui stall 5420ms cpu=5310ms of 5420ms burning cpu across activity-poll
    ui stall 1180ms cpu=21ms of 1180ms blocked across realtime
    ui job instance-history 274ms
    window-ready 812ms

`cpu=` is the UI thread's own processor time inside the gap: at or above 75 % of the gap the line
says `burning cpu`, at or below 15 % `blocked`, between the two `mixed`, and `unknown` when the
clock could not be read. A slow `ui job` line is wall-clock across its awaits, not UI-thread time: a
1480 ms `activity-poll` whose parse is in `Task.Run` is mostly waiting, so the `cpu=` reading on the
stall line is what decides whether the thread was doing the work.

## Releasing

Bump `<Version>` in `VrcCompanion/VrcCompanion.csproj` in the change that ships, write
`artifacts/release-notes-<version>.md` (it becomes the release body, so keep its "honest notes"
honest about what is still paged or unverified), commit, push, wait for green, then:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/pack-release.ps1 `
        -Version 1.0.86 -NotesFile artifacts/release-notes-1.0.86.md

The script needs a clean tree, a pushed `HEAD` and a csproj version that matches, and it builds,
packages a delta against the previous release and uploads. It takes about three minutes and outlives
a single foreground command: run it in the background to a log and poll the log. Afterwards
`-VerifyOnly` re-checks feed/asset parity and the delta chain without publishing.

Known failure modes: a transient `gh` 401 during publish leaves a packed version in
`artifacts/releases`, which blocks `vpk pack` on the retry - move that output aside first; a
transient `gh` failure on an older release makes verification exit 3, which `-VerifyOnly` clears
when the release itself is fine.

Checks on a commit:

    gh api "repos/Richy-Chu/KawaiiVrc/actions/runs?head_sha=<sha>" \
        --jq '.workflow_runs[] | "\(.name):\(.status):\(.conclusion)"'

`test-suite.yml` runs on every push; `lint-workflows.yml` only on paths under `VrcCompanion.Tests/`,
`build/` or `.github/workflows/`, so a commit that touches only app XAML gets no lint run - that is
the path filter, not a failure. `release-verify.yml` runs nightly against the published releases.
Never weaken a test to get a workflow green.

## Never write to the real profile

The installed app keeps the user's session in `%LOCALAPPDATA%\VrcCompanion`. A test that
reaches it can clear the real saved session and sign the user out. Test stores go through the
seams in `VrcCompanion/Services` (`UserDataPaths`, `VrcLogPaths`), which the module initializer
points at a scratch folder before any test runs. Do not add a store that builds the profile
path itself: `ProfileIsolationTests` scans every production source file for that.

The same rule covers a manual check: launching the app writes that folder, so a visual check of the
window is the user's to run, not the agent's. Reading `lifecycle.log` and `crash.log` from there is
safe and is how a freeze is diagnosed.

`RealProfileWatch` records the real profile in the module initializer and compares it when the
suite ends. The verdict is printed from
`%TEMP%\VrcCompanionTests\logs\guard\<run>\profile-guard-note.txt` by MSBuild after VSTest,
because the runner drops a passing test's output. A filtered run hands its baseline to a second
guard pass (`VRCC_PROFILE_GUARD_REPLAY=1`), so a filtered run still goes red on a leak. A run
that changes the profile while the installed app is running is reported as inconclusive rather
than failed: the app writes those files too.

Each run gets its own `<run>` folder, so two suites running at once cannot read each other's
baseline and pass a leak. The project makes the folder when it is evaluated and hands the test
host the path in a generated runsettings file (`VRCC_PROFILE_GUARD_DIR`); a run started without
the project - the test explorer, a bare `dotnet vstest` - names its folder after its test host's
pid instead. `RealProfileWatch.RunRetention` is the one place that day is written: the guard
deletes run folders past it at the next run's start, so the folder does not grow with every run,
and the post-run check reads the same value out of `RealProfileWatch.cs` rather than keeping a
copy. A folder still inside the day is named with its age, so a crashed run's baseline is not lost
between the run that left it and the next prune, and one past it is taken by the check itself,
because no run has started since to prune it - which is what a crash after the last run in a job
leaves. A name `PruneOldRuns` cannot
read is one it never prunes, so the check refuses that folder and keeps it as evidence.

A run that leaves no baseline at the path MSBuild gave it fails the run
(`FailOnMissingGuardBaseline`), so a hand-off that silently stops working cannot pass: without
it the replay pass would skip and the note would never print. Listing tests is the one run
exempt, since it never starts the guard.

## One copy at a time

`SingleInstance` keeps one KawaiiVrc per signed-in Windows user. `Program.Main` claims a `Local\`
named mutex before any WPF work; a launch that finds it taken asks the running copy to come
forward and returns without building a window or a view model, which is why `App.xaml` names no
`StartupUri` and the one window is made in `App.OnStartup`, after the claim is settled. The
running copy answers later launches on a named pipe and calls `MainWindow.BringToFront` on its
dispatcher. Two copies must never run: they share the one saved VRChat session and VRChat
rotates the auth cookie on nearly every request, so the copy that loses the race 401s and clears
the saved session for both - and they would both write chatbox input and the same settings file.
`SingleInstanceTests` drives the claim, the hand-off, a hand-off nobody answers and the refusal
to let a second copy serve the pipe, each with its own mutex and pipe name. `AppStartupTests`
drives the startup wiring itself on the host's one application: with a second claim in place,
`App.RunStartup` returns false, stops once, and never calls the shell factory, so no window and
no view model exist to touch the saved session.

## When something throws

A UI-thread failure is logged and the window stays up, because a layout or render error can fire
on every measure pass and one dialog per pass would bury the app. `CrashLog` writes one entry per
kind of failure per run - type, throwing method and message - and keeps the file to its cap,
rotating the previous generation to `crash.log.1`, so a failure that repeats costs one line
rather than one per pass and the log cannot fill the disk. The same log takes an unobserved task
failure (`TaskScheduler.UnobservedTaskException`), which would otherwise stay silent until exit.
A command's failure is not a crash and must not reach the dispatcher: `AsyncRelayCommand` reports
it to the lifecycle log, or to the `onFailure` a test hands it, and the button comes back either
way (`AsyncCommandTests`).

## Long sessions

`VrcApiClient` caches user, profile, mutual, world, group-list and cosmetic responses, and each
of those caches is a `BoundedCache` with a ceiling: a read refreshes the entry's place in the
order, and the least recently read entry goes when the cap is reached. Freshness stays the
caller's rule - an entry carries the moment it was fetched - and `ClearCaches` on sign-out still
drops everything. No cache in the client is unbounded, because the app is expected to run for
days; `BoundedCacheTests` decides the cap and the order it evicts by.

## The scratch sweep

`TestProfile` (`VrcCompanion.Tests/TestProfile.cs`) gives each run a folder of its own and
points the process's temp at it: `TMP` and `TEMP` are set to `%TEMP%\vrc-companion-tests-<pid>`
in the type's static constructor. `Path.GetTempPath()` reads those variables on every call, so
that one assignment is the seam for the whole suite - store folders, media caches, loose files
named after a GUID, the WebView2 profile, and any path a test builds itself all land inside the
run's folder, with no call site changed. Never build a test path from the system temp: read
`TestProfile.RealTemp`, which was captured before the redirect.

Because everything is in one folder, the sweep has nothing to weigh: it deletes folders whose
process is gone (including a one-hour rule for pids Windows has since reused) and leaves
everything else alone. `SweepFinishedRuns` takes the moment it judges against, so that hour is
decided by cases a minute either side of it, and on it, rather than by how fast the runner is. The
post-run check reads that same hour, so neither calls a folder litter the other would take, and
neither fails one for a process that is not its run. Two suites at once write in different folders and never touch each
other. The run deletes its own folder at the end of its last collection (`ScratchCleanup`),
not at process exit: a test host that has reported every result is not obliged to run its exit
handlers, and one CI run left its whole scratch behind that way. The exit handler stays only as
a backstop for runs that never reach the last collection.

What survives are Chromium's own temp files: zero-byte `<guid>.tmp` files WebView2's browser
processes write into `%TMP%`, which is now the run's folder. They come and go while the browser
runs, and the last two are still open when the cleanup deletes the folder, because the browser
process lives as long as the host. No test or product call site writes that name, and nothing
can close the browser first: `CoreWebView2Environment` is one process-wide `Lazy` with no
shutdown, and WebView2 has no per-browser temp directory, so the files have to land in `%TMP%`.
A file held that way cannot be deleted from inside the host at all, so the run does not try: it
leaves the folder where it is and writes `cleanup-deferred.txt` inside it, naming its own pid,
the moment it gave up and every file an exclusive open found still held - a read, so a
read-only file is not mistaken for a lock. A folder can also be what stopped the delete with
nothing held inside it - an empty one whose own handle is still open - so the note names every
folder the trim could not take with a `dir:` line of its own, and the check needs at least one
file or folder in the note, because one that names neither shows nothing about what was in the
way. The next run's sweep takes the folder once that pid is gone. The post-run check accepts a
leftover folder only when that note names the folder's own dead pid, every file it names was a
zero-byte `.tmp`, and the folder holds nothing else it does not name: cleanup deletes every file
it can before writing the note, so a file left out of it is one the run could not delete and did
not explain. A folder with no note - a run whose cleanup never ran - fails the job, and so does
one whose note names a locked store file or whose folder still holds a store file the note never
mentions. The note's own `utc:` line has to be recent too: the sweep takes a deferred folder on
the next run's startup, so one older than two
hours was not swept, and the check takes it itself instead of letting it stand - locally that
usually just means nobody has run the suite since. A note dated ahead of the clock is the same
folder from the other side: its age is negative and only walks towards the bound, so the folder
would stand for as long as the stamp is wrong. More than five minutes ahead is not skew any clock
leaves, so such a note is due now and the check takes it, while a note a few minutes ahead ages
into the past on its own. Age is the one thing forgiven: a stale note, or one dated hours ahead,
that names a locked store file, or sits beside a file it does not name, still fails and keeps
the folder, because that folder is evidence rather than litter. A stale folder the check cannot
delete fails too, so a take never turns into a silent skip - including a delete that reports no
error and leaves the folder where it was. The check ages a note against `-Now` when it is given,
so the two-hour bound is decided by one pinned clock one minute either side of it, and on it,
rather than by whatever the machine's clock says while the test runs. A failure also lands on the
run page, where a red push is actually read: one `::error` annotation naming the first folder with
its age and reason, and a step summary table carrying every folder. One annotation rather than
one per folder, because GitHub renders ten per step and drops the rest while the summary has no
such cap. A passing run writes neither, and the planted cases assert both channels. A path the
check cannot list fails the job in its own right: a missing `%TEMP%`, or a file where the folder
should be, is not an empty folder, so it is reported as `Scratch could not be checked` - one
annotation and one summary row naming the path and why nothing was judged - instead of passing.
A pid accounts for a folder only while its process runs and the folder has been touched inside the
hour - the hour the sweep retires one by - so a folder nothing has touched for that long is a
finished run's however live the pid is, and is judged by its note exactly as a dead pid's folder
is. Without that hour the check failed such a folder for a process that is not its run, and kept
failing it while the sweep would have taken it.

The WebView2 profile is the one thing that never lives in the run's folder: its browser holds it
open for the host's whole life, so it gets `%TEMP%\vrc-companion-browser-<pid>` and the next
run's sweep reclaims it once that pid is gone. The check judges those folders rather than listing
them: a profile may wait for the sweep only while its name parses to a pid which no process holds,
or which has not touched the folder for the sweep's hour. A name no sweep can read, or a pid still
alive and still touching the folder when the run is over, fails the job.
A run therefore leaves nothing but a browser profile and a deferred folder that says why,
and both are held to the rules above. The guard's own run folders, under
`%TEMP%\VrcCompanionTests\logs\guard`, are outside the run's temp and are judged by their one
rule: a folder inside the day the guard keeps one for waits, named with its age, and one past
that day is taken, since nothing was left to prune it.

`DeferralNoteContractTests` exercises the writer and the reader as a pair by running the real
script over notes this project wrote, so CI decides the accepting path too. It plants both halves
of the stale rule by asking `RecordDeferral` for a note dated four hours back: over files nobody
holds any more, which the check must take, and over a locked store file, which it must refuse and
leave standing. It is also the one place the two-hour bound is planted - a minute inside, exactly
on it, a minute past - because the note and the check have to be aged against the same clock, and
only here does a clock reach both. `build/test-assert-temp-clean.ps1` therefore plants no case
near the bound: its notes are written by hand, so an age it chose itself would pin the reader
while the writer stayed free to disagree with it. It does plant the
skew grace itself - a note exactly five minutes ahead waits for the sweep, one a second past it
is taken, a note six hours ahead is taken and one six hours ahead that names a locked store file
is refused and kept - because that rule lives only in the reader, and each case pins the same
clock on the check. The hour a live pid accounts for a folder is planted on both sides instead,
because both read it: `TestScratchTests` decides the sweep's edge with a handed-in clock, and the
harness plants the check's on the same edge, one case a second inside it and one a second past,
for a run folder and for a browser profile. The guard's day is planted in the harness instead: the
age is stamped on the planted folder, so a case on the day and one a second past decide that edge
without a second clock. Its value is not repeated in the harness either: it is written once, in
`RealProfileWatch.RunRetention`, the check reads it from that source, and a case that lands on the
wrong side because the value moved turns red and has to be re-planted on purpose. The folder a run
could not delete is planted in the pair's tests as well, and for the same reason it is not in the
harness: only a real open handle makes a delete refuse an empty folder, so that case holds one
while the project's note names the folder and the check accepts it.

The guard's files are the exception: they live under `%TEMP%\VrcCompanionTests\logs` (real
temp, via `TestProfile.RealTemp`) because MSBuild reads them after the run from outside this
process.

`TestScratchTests` keeps both rules honest: that `Path.GetTempPath()` resolves inside the run's
folder, and that the sweep takes a finished run's folder while leaving a live one alone until it
has gone untouched for the hour, which is how a reused pid stops keeping litter. Set
`VRCC_KEEP_TEST_SCRATCH=1` to keep a failing run's files: the whole folder stays at
`%TEMP%\vrc-companion-tests-<pid>`, and the next run sweeps it at startup.

## CI

`lint-workflows.yml` runs on pushes and pull requests that touch `VrcCompanion.Tests/`,
`build/` or `.github/workflows/`: `verifier` runs the release-verifier self-tests
(`build/test-exit-codes.ps1`, `test-feed-parity.ps1`, `test-drift-report.ps1`,
`test-assert-temp-clean.ps1`, `test-retry-failed-tests.ps1`), `actionlint` lints the workflows, the pre-push hook and
`build/*.ps1` with PSScriptAnalyzer, and `profile-guard` on `windows-latest` runs
`build/test-profile-guard-red.ps1`, which requires one filtered run to exit 1 on a fabricated
baseline.

`test-suite.yml` runs on every push and is the only job that runs all 940 tests. When a test
fails, `build/retry-failed-tests.ps1` re-runs only the failed tests once: it plans the filter
from the run's `trx`, reports what passed on retry as a warning and in the step summary, and the
job stays green - a flaky test must not redden an otherwise good commit, but it must not be
hidden either. A test that fails twice fails the job, and a first run that fails more than five
test cases is not retried at all: a broad failure is the commit's, not the runner's. Cases, not
method names, are counted, so a theory that fails most of its rows reads as broken rather than as
one flake. Every run is bounded per test by `--blame-hang-timeout 2m`: a hung test aborts the run
in two minutes and leaves a blame sequence naming it, instead of running out the job's clock with
nothing recorded. An abort keeps no results at all, so for a hang the retry re-runs the whole
suite rather than a filtered set - everything after the hang never ran - and it applies the same
bound, so the re-run cannot hang either. The sequence files ride along in the artifact. The job finishes with
`build/assert-temp-clean.ps1`, which fails it when a `vrc-companion-tests-*` folder is still in
`%TEMP%` without a `cleanup-deferred.txt` naming that folder's own pid - the only litter a run
can leave, and the only folder allowed to wait for the next run's sweep. A `%TEMP%` it cannot list
- missing, unreadable, or a file - fails the step before any folder is judged. The step runs even when
the tests fail, so a red run still reports what it left. Run the script by hand after a local run
to get the same verdict.

## Commits

Imperative subject, then a short body that says why. No AI attribution and no trailers.
