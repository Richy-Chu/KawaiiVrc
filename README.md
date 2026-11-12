# VRChat Companion

An unofficial Windows desktop companion for [VRChat](https://vrchat.com), built with C# and WPF on .NET 8. It is a focused side app, not a game client replacement: sign in, browse friend profiles, catch up on notifications, update your status, inspect worlds, and create or join instances without leaving your desktop.

> **Not affiliated with VRChat Inc.** This app uses VRChat's unofficial web API. Use it at your own risk and please respect VRChat's Terms of Service and rate limits.

## Features

- Sign in with a VRChat account, including authenticator-app, email, and recovery-code two-factor authentication.
- Optional "remember this device" session that stores only the auth cookie, encrypted with Windows DPAPI. Your password is never written to disk.
- Profile overview with avatar, display name, trust level, badges, bio, and account details.
- Friends workspace with a searchable, online-only-filtered list and a full detail pane for the selected friend:
  - Loads the friend's public profile, bio links, groups, badges, trust level, pronouns, and mutual friend/group counts.
  - Join their world, invite them to your current instance, request an invite when they are in a private world, copy their location, or open their world in the world-details view.
  - One-click link to their profile on vrchat.com and a refresh action that reloads the profile.
- Notifications with unread counts, per-item and bulk "mark read", plus inline actions to accept friend requests and join invite instances.
- World discovery: popular public worlds on first visit plus name search, with thumbnails and a link to open the world on vrchat.com.
- World details popup, opened by clicking any world card:
  - Hero image, description, tags, capacity, visits, favourites, release status, supported platforms, and creation/update dates.
  - List of active public/friends-visible instances with occupancy and region, each with a **Join** button.
  - **Create instance** with Public, Friends+, Friends, Invite+, and Invite access modes and US West, US East, Europe, and Japan regions.
  - Launch the created instance immediately or copy a share link (vrch.at short code when available).
  - Paste a launch URL, vrch.at short link, full `world:instance` location, or instance ID to join a specific instance.
- Quick join card on the Worlds page for pasting VRChat launch links or short codes without opening a world.
- Status editor for presence and a short status message.
- Manual refresh and an automatic refresh every five minutes that pauses while the window is minimized or hidden.
- Dark purple/blue VRCX-inspired interface with no third-party UI dependencies.

## Requirements

- Windows 10 or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build the project. Newer SDKs can also build it.
- .NET 8 Desktop Runtime for framework-dependent builds.
- A VRChat account that has a username and password. Accounts that only sign in through Steam, Meta, or PICO without a VRChat password are not supported by this app.

## Build and run

From the repository root:

```powershell
dotnet build VrcCompanion\VrcCompanion.csproj -c Release
dotnet run --project VrcCompanion
```

You can also open `VrcCompanion.sln` in Visual Studio 2022 and run the `VrcCompanion` project.

The built executable is placed at:

```text
VrcCompanion\bin\Release\net8.0-windows\VrcCompanion.exe
```

## Using the app

1. Start the app and sign in with your VRChat username and password.
2. If your account has two-factor authentication enabled, enter the code from your authenticator app. Use the links under the code field to switch to an email code or a recovery code if needed.
3. Leave **Remember this device** checked if you want the app to restore your session the next time it starts.
4. Use the sidebar to switch between **Profile**, **Friends**, **Notifications**, and **Worlds**.
5. On **Friends**, click a friend to open their profile pane. Use the action row to:
   - **Join** their instance (launches VRChat through the `vrchat://` handler).
   - **Invite** them to the instance you are currently in.
   - **Request invite** when they are in a private/invite instance.
   - Open their current **World details**, copy the location, or open their vrchat.com profile.
6. On **Worlds**, click a world card to open the details popup. From there you can:
   - Join any listed active instance.
   - Choose an access level and region, then **Create instance**.
   - **Launch VRChat** into the new instance or **Copy share link** to share it.
   - Paste a launch URL, short code, or `world:instance` location and join it directly.
7. Use **Quick join** on the Worlds page to paste a `vrchat://launch?...` link, a `vrch.at` short link, an instance ID, or a full location and launch immediately.
8. Use **Sign out** in the header or the session card to remove the saved session from this PC.

## Performance notes

The app is intended to stay open alongside VRChat, so it avoids background work it does not need:

- Friend and notification lists use WPF virtualization, so large friend lists do not create a visual per row until it is scrolled into view.
- Friend avatars are not downloaded for the list. Profile images and world images load on demand and are cached.
- World details and friend profiles use short-lived API caches to avoid duplicate requests when reopening the same item.
- Switching friends or worlds cancels the previous in-flight detail request.
- Automatic refresh runs every five minutes and is skipped while the window is minimized or hidden.
- API requests are serialized with a one-second gap, which also keeps bursts of image or detail loading from competing with game traffic.
- The app does no polling faster than the refresh interval and opens no game process handles; launching is delegated to Windows and the `vrchat://` protocol.

## Data and security

- Login credentials are sent directly to `https://api.vrchat.cloud` over HTTPS. The app never stores your password.
- If you choose to remember your session, only the VRChat `auth` cookie is saved. It is encrypted with Windows DPAPI for the current Windows account at:

  ```text
  %LOCALAPPDATA%\VrcCompanion\session.dat
  ```

- Delete that file (or sign out in the app) to forget the saved session.
- The app has no telemetry and does not send data anywhere other than VRChat's API and image hosts.
- Avatar and world images are downloaded from VRChat-provided image URLs.

## API and Terms of Service notes

- VRChat does not officially document or support this API. Endpoints, response shapes, and rate limits can change without notice.
- API calls are serialized and spaced at least one second apart to stay inside VRChat's rate limits. World and avatar images are downloaded separately with a small concurrency limit.
- Instance creation uses `POST /instances` and may be rejected if your account lacks permission for the world or has hit a rate limit. The app surfaces the API error in the popup.
- Launching uses the `vrchat://launch?ref=vrchat.com&id=<world>:<instance>` URL scheme. VRChat must be installed and registered as the handler for `vrchat://` links.
- Automatic refresh runs every five minutes; manual refreshes and actions are also throttled by the request queue.
- Do not use this app for automation, scraping, spam, or running multiple clients against one account.
- Some responses use the legacy notifications endpoint. If VRChat retires it, notifications may become unavailable until the app is updated.
- Your use of the API is subject to VRChat's Terms of Service. This project is not endorsed by or affiliated with VRChat Inc.

## Troubleshooting

- **The app fails to launch or shows an unexpected error:** check `%LOCALAPPDATA%\VrcCompanion\crash.log` for details.
- **"VRChat refused the request (403)":** verify your email address and complete any account checks on the VRChat website, then try again.
- **"VRChat's rate limit was reached":** the API is throttled. Wait a minute or two before refreshing.
- **A saved session stops working:** the auth cookie expired or was revoked. Sign out and sign in again.
- **2FA codes are rejected:** make sure the code is current. Authenticator codes rotate every 30 seconds. Recovery codes can be used from the switch link under the code field.
- **World thumbnails do not load:** the image host may be blocked by your network or firewall. The gradient placeholders remain usable.
- **Nothing happens when I press Join or Launch VRChat:** make sure VRChat is installed and the `vrchat://` link handler is registered. Launching VRChat once from the official website or Steam usually registers it.
- **Instance creation is refused:** the world may not allow your account to create instances, or the request hit VRChat's rate limit. Wait a moment and try another access level or region.
- **A world shows no active instances:** VRChat only returns public/friends-visible instances to this API, and a world may genuinely have none. Create one from the popup instead.
- **Forgot VRChat password:** use the link on the sign-in card, or visit [vrchat.com/home/password](https://vrchat.com/home/password).

## Project layout

```text
VrcCompanion/
  App.xaml                   Theme resources, styles, and converters
  MainWindow.xaml            Login, 2FA, friends split view, world popup
  Models/                    VRChat JSON models
  Services/
    VrcApiClient.cs          Auth, friends, notifications, status, worlds, instances
    SessionStore.cs          DPAPI-encrypted session cookie storage
    ImageLoader.cs           Avatar and world thumbnail cache
    LaunchService.cs         vrchat:// launch and location helpers
    InstanceLinkParser.cs    Parses launch URLs, short links, and instance IDs
  ViewModels/                MVVM state, commands, friend/world detail view models
  Converters/                Visibility converter
```

## Known limitations

- The app polls; it does not use VRChat's realtime websocket. Presence can be up to five minutes stale unless you refresh manually.
- World search returns up to 24 results from the public catalog.
- Active instance lists are limited to the summary VRChat returns for the world (up to 12 rows shown) and exclude private instances you cannot see.
- The world detail popup was designed for a 1200×800+ window. At the minimum window size, use its scrollbar to reach the instance and create controls.
- Avatar changes, group management, and moderation tools are not included.
- The UI is English-only.
- The app is intended for personal, interactive desktop use, not unattended automation.
