# VRChat Companion

An unofficial Windows desktop companion for [VRChat](https://vrchat.com), built with C# and WPF on .NET 8. It keeps your VRChat account, friends, worlds, Discord, and a normal web browser in one window, so you can stay in a call and look things up without alt-tabbing through a pile of browser windows.

> **Not affiliated with VRChat Inc.** This app uses VRChat's unofficial web API. Use it at your own risk and respect VRChat's Terms of Service and rate limits.

## Start here

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and make sure the Microsoft Edge WebView2 Runtime is installed (it is already present on most Windows 10/11 PCs).
2. From the repository root, build and run:

   ```powershell
   dotnet build VrcCompanion.sln -c Release
   dotnet run --project VrcCompanion -c Release
   ```

3. Sign in with your VRChat username and password. If your account uses two-factor authentication, enter the authenticator, email, or recovery code when prompted.
4. Use the left sidebar to open **Discord**, **Brave Browser**, **Friends**, **Worlds**, **Avatars**, **Groups**, **Chatbox**, or **Media**.
5. For Discord, sign in on Discord's own page inside the tab. Once it loads, you can join a voice channel and switch to another tab or minimize the app without dropping the call.
6. For general browsing, open **Brave Browser**, type a search or address, and press Enter.

That is the normal starting flow. You do not need to configure the low-memory behavior; it is automatic.

## Requirements

- Windows 10 or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build the project. Newer SDKs can also build it.
- .NET 8 Desktop Runtime for framework-dependent builds.
- Microsoft Edge WebView2 Runtime for the Discord and Brave tabs.
- A VRChat account with a username and password. Accounts that only sign in through Steam, Meta, or PICO without a VRChat password are not supported.

## What is in the app

- **Profile** — your avatar, display name, trust level, badges, bio, and account details.
- **Friends** — a searchable friend list with online filters and a detail pane for joining, inviting, requesting an invite, copying a location, or opening a profile.
- **Notifications** — unread counts, filters, mark-read actions, friend requests, and invite actions.
- **Worlds** — popular and searchable worlds, active instances, instance creation, and a quick-join box for launch links and short codes.
- **Avatars** — your VRChat avatars, favourites, and avatar search.
- **Groups** — browse and manage your VRChat groups.
- **Activity** — recent friend activity.
- **Chatbox** — send and rotate VRChat chatbox messages over OSC.
- **Media** — your local VRChat pictures, grouped by world and month.
- **Creator Resources** — links to creator marketplaces and official VRChat tools.
- **Accounts** — switch between saved VRChat accounts.
- **Brave Browser** — a general in-app browser backed by Brave Search, with cached page and search state.
- **Discord** — Discord's web app with quick actions for friends, servers, and settings, plus voice calls that survive tab switches and minimizing.

## Browser tabs and low-memory mode

Brave and Discord are real WebView2 tabs, not screenshots or embedded shortcuts. Each visited tab stays alive when you switch away, so its page, scroll position, search text, and Discord voice connection remain intact.

When a browser tab is hidden or the whole app is minimized, the app asks WebView2 to use its **Low** memory target. Hidden tabs keep their scripts, network connections, and Discord voice session alive; Chromium releases what it can and swaps memory back in when the tab is shown again. The selected tab returns to **Normal** when the window is restored.

On a five-minute idle test with both Discord and Brave cached, the app's total working set dropped from about **955 MB** to **614 MB** while minimized. After restoring, it settled around **642 MB** — it stays trimmed instead of immediately re-inflating. This is a resident-memory saving, not a promise that committed memory will shrink; Chromium may page data to disk and load it again later.

The important behavior is:

- Hidden tabs and a minimized window use `Low`.
- The selected tab uses `Normal`.
- Discord voice stays connected while hidden or minimized.
- No replay or reload is required when you switch back.

## Build, run, and test

Build the whole solution:

```powershell
dotnet build VrcCompanion.sln -c Release
```

Run tests:

```powershell
dotnet test VrcCompanion.sln -c Release
```

The built executable is at:

```text
VrcCompanion\bin\Release\net8.0-windows10.0.19041.0\VrcCompanion.exe
```

You can also open `VrcCompanion.sln` in Visual Studio 2022 and run the `VrcCompanion` project.

## First-run notes

- **Remember this device** stores only the VRChat auth cookie, encrypted with Windows DPAPI. Your password is never written to disk.
- Discord sign-in happens on Discord's own page inside the tab. The app does not read or store your Discord password.
- If VRChat asks for a code, use the links under the code field to switch between authenticator, email, and recovery codes.
- Sign out from the header or the session card to remove the saved VRChat session from this PC.

## Data and security

- Login credentials go directly to `https://api.vrchat.cloud` over HTTPS.
- The remembered VRChat session is stored at:

  ```text
  %LOCALAPPDATA%\VrcCompanion\session.dat
  ```

  It is encrypted with Windows DPAPI for the current Windows account.
- Delete that file, or sign out in the app, to forget the session.
- Crash reports are written to `%LOCALAPPDATA%\VrcCompanion\crash.log`.
- The app has no telemetry. It contacts VRChat, Discord, and the sites you open in the browser tabs.
- Do not use the app for automation, scraping, spam, or running multiple clients against one account.

## Troubleshooting

- **The app does not start or shows an unexpected error:** check `%LOCALAPPDATA%\VrcCompanion\crash.log`.
- **The Discord or Brave tab is blank:** install the Microsoft Edge WebView2 Runtime and restart the app.
- **Discord voice drops:** check that the app is not being force-closed and that Windows is not blocking Discord's microphone or network access.
- **"VRChat refused the request (403)":** verify your email and complete any account checks on the VRChat website.
- **"VRChat's rate limit was reached":** wait a minute or two, then refresh.
- **A saved session stops working:** the auth cookie expired or was revoked. Sign out and sign in again.
- **2FA codes are rejected:** make sure the code is current; authenticator codes rotate every 30 seconds.
- **World images do not load:** the image host may be blocked by your network or firewall.
- **Join or Launch VRChat does nothing:** install VRChat and make sure it is registered for `vrchat://` links. Launching it once from the official website or Steam usually registers the handler.
- **No active instances are listed:** VRChat only exposes public and friends-visible instances to this API; create one from the world popup instead.

## Project layout

```text
VrcCompanion/
  App.xaml                   Theme, styles, and converters
  MainWindow.xaml            Shell, sections, login, and popups
  Controls/
    EmbeddedBrowser.xaml.cs  WebView2 host and low-memory behavior
  Models/                    VRChat JSON models
  Services/                  API, session storage, images, launches, OSC, lyrics
  ViewModels/                MVVM state and commands
  Converters/                UI converters
VrcCompanion.Tests/          Unit tests
```

## Known limitations

- The app polls VRChat; it does not use VRChat's realtime websocket. Presence can be up to five minutes stale unless you refresh manually.
- World search returns a limited set of public results.
- Active instance lists only include what VRChat exposes to the API and exclude private instances you cannot see.
- The UI is English-only.
- The app is intended for personal, interactive desktop use, not unattended automation.
- The low-memory mode is best-effort. Chromium may keep or release different amounts of memory depending on the pages and call state.
