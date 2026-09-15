# Chatter

> A cross‑platform chat sample built with .NET MAUI and ASP.NET Core, featuring real‑time rooms/DMs, typing indicators, presence, and emoji shortcodes.

![CI](https://github.com/AlexAhmanHV/Chatter/actions/workflows/ci.yml/badge.svg)
![Platforms](https://img.shields.io/badge/.NET%20MAUI-Android%20%7C%20iOS%20%7C%20MacCatalyst%20%7C%20Windows-512BD4)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Table of contents

* [Overview](#overview)
* [Why it’s useful](#why-its-useful)
* [Architecture](#architecture)
* [Screens & features](#screens--features)
* [Project structure](#project-structure)
* [Getting started](#getting-started)

  * [Prerequisites](#prerequisites)
  * [Clone](#clone)
  * [Configure](#configure)
  * [Run the backend (ASP.NET Core)](#run-the-backend-aspnet-core)
  * [Run the client (MAUI)](#run-the-client-maui)
  * [Build & run cheat‑sheet](#build--run-cheat-sheet)
* [Troubleshooting](#troubleshooting)
* [Contributing](#contributing)
* [License](#license)
* [Maintainers](#maintainers)

---

## Overview

Chatter is a small but complete chat application that showcases a modern .NET stack:

* **Lobby + DMs + group chats**: Join the lobby, start direct messages, or create a named group with several members at once. The group creator can add/remove members and rename the group from the "⋮" menu on the chat header.
* **Block & mute**: Block a user to stop DMs both ways; mute any chat to keep receiving messages without the unread badge/notification bothering you.
* **Presence & typing**: Online/away/busy status and “Alice is typing…” indicators.
* **Emoji shortcodes**: `:smile:` → 😄 via a converter and parser.
* **Name aliases/renames**: Seamless display‑name updates.
* **Edit & delete your own messages**: With an "(edited)" marker; deleted messages show a placeholder instead of disappearing from the timeline.
* **Reactions**: Tap 👍/❤️/😂/🎉/😮/😢 on any message; tap an existing reaction to toggle it off, hover/long-press one to see who reacted.
* **Read receipts**: A "Seen" marker appears under your last DM message once the other person has viewed it.
* **Paginated history**: Only the most recent messages load at first — a "Load earlier messages" button pages further back.
* **Persisted history**: Messages, edits, reactions, read receipts, group membership, accounts, and unread counts all survive a server restart (SQLite via EF Core migrations).
* **Self-contained accounts**: Email/password sign-up and login are handled entirely by the server itself (ASP.NET Core Identity + a JWT it issues and validates) — no external identity provider to configure.
* **Cross‑platform UI**: .NET MAUI app for Android, iOS, macOS (MacCatalyst), and Windows.

## Why it’s useful

This repo demonstrates how to:

* Structure an **MVVM** MAUI app with `CommunityToolkit.Mvvm`.
* Use **compiled bindings** (`x:DataType`) to eliminate XamlC warnings and speed up the UI.
* Drive UI with **ObservableCollection** state and event streams from a chat service.
* Run your **own accounts system** with ASP.NET Core Identity, issuing your own JWTs (`/auth/register`, `/auth/login`) instead of depending on an external auth provider.
* Secure a **SignalR hub** with JWT bearer auth (including the query-string token workaround WebSockets need) and authorize actions server-side instead of trusting the client.
* Rate-limit plain HTTP endpoints (login/register) per client IP with ASP.NET Core's built-in rate limiter — a brute-force guard that a SignalR hub method can't get from the same middleware.
* Give a demo app **durable state** with EF Core + SQLite instead of only in-memory dictionaries.
* Host a simple **ASP.NET Core** backend with correct HTTP→HTTPS redirection.

## Architecture

High‑level flow: the client registers or signs in against this app's own `/auth/register`/`/auth/login` endpoints (ASP.NET Core Identity checks the password; a matching row lives in the same SQLite database as everything else) and gets back a JWT the server itself signed. That token is presented when opening the SignalR connection; the backend validates it on every connection, resolves identity from it (never from client-supplied text), and pushes chat/presence events that the MAUI client renders via MVVM.

```
Chatter.sln
├─ Chatter.Client/                      # .NET MAUI app (Android/iOS/MacCatalyst/Windows)
│  ├─ Views/                            # Pages (Login, Chat, Settings)
│  ├─ ViewModels/                       # VM layer (Login, Register, Chat, Settings)
│  ├─ Converters/                       # EmojiDisplayConverter, InvertedBoolConverter
│  ├─ Messages/                         # DisplayNameChangedMessage
│  ├─ Services/                         # ChatService client, ApiAuthService (calls /auth/*),
│  │  │                                 # ServerConfig (per-platform backend URL), EmojiCatalog
│  │  └─ Models/                        # ChatItem, ChatMessageItem, PresenceStatus, UserPresenceItem
│  ├─ Helpers/                          # UI helpers, etc.
│  └─ Resources/                        # Styles, images
├─ Chatter.Server/                      # ASP.NET Core backend + SignalR hub
│  ├─ Hubs/                             # ChatHub — [Authorize]'d, identity from the JWT's "sub" claim
│  ├─ Auth/                             # JwtIssuer — signs the JWTs /auth/register and /auth/login return
│  ├─ Data/                             # ChatDbContext (EF Core + SQLite + Identity): accounts, messages,
│  │  │                                 # reactions, group chats, blocks, mutes, read receipts
│  │  └─ Migrations/
│  ├─ Program.cs                        # Kestrel endpoints, Identity, JWT bearer auth, /auth/* endpoints
│  ├─ appsettings*.json                 # Jwt:SigningKey/Issuer/Audience, ConnectionStrings:Chatter
│  ├─ Dockerfile
│  └─ Properties/launchSettings.json
├─ Chatter.Server.Tests/                # ChatHub authorization/persistence/rate-limit tests,
│                                        # plus Identity/JwtIssuer tests (AuthTests.cs)
├─ Chatter.Client.Tests/                # Unit tests
│  └─ ChatTextParserTests
├─ Chatter.Core/
│  └─ Services/                         # ChatTextParser (emoji shortcode parsing)
├─ Chatter.Shared/
│  ├─ Helpers/                          # ServiceHelper
│  └─ Models/                           # ChatSummary, ChatMessageDto, ReactionDto,
│                                        # RegisterRequest/LoginRequest/AuthResponse — shared DTOs
├─ docker-compose.yml                   # Runs Chatter.Server standalone (see Getting started)
└─ .github/workflows/ci.yml             # Build + test on push/PR (build-and-test, docker-build)
```


## Screens & features

* **LoginPage** – Email/password login.
* **RegisterPage** – Create an account (optional display name).
* **ChatPage** – Chats list (Lobby/DMs/groups), messages with edit/delete/react/seen/attachments/
  voice notes/forwarding, composer with image-attach and record buttons, typing indicator, people
  panel with avatars, group admin management, "New group" toolbar action, "Load earlier messages"
  paging.
* **SettingsPage** – Update display name and avatar.

### Attachments & avatars

Images are stored as blobs directly in the SQLite database (`ChatMessageEntity.AttachmentData`,
`ApplicationUser.AvatarData`) rather than in a separate object store — there's no external
storage dependency to configure. A few consequences worth knowing:

* **Images only** — PNG/JPEG/GIF/WebP, enforced by content-type allowlist on the server.
* **Size caps**: attachments up to 5 MB, avatars up to 512 KB. Larger uploads are rejected.
* **Lazy loading** — attachment bytes are only fetched when a message's placeholder is tapped
  (`ChatHub.GetAttachmentData`), not eagerly with chat history, to keep scrolling cheap.
* **Avatars are served from a public, unauthenticated endpoint** (`GET /avatars/{userId}`) so
  `<Image>` controls can load them directly by URL. This is deliberate — avatars aren't sensitive —
  but it does mean anyone with a user id can fetch that user's avatar without logging in. The
  client never learns a raw user id itself; it only ever sees `/avatars/{id}` URLs resolved
  server-side from display names (`ChatHub.GetAvatarUrls`).
* **No thumbnailing/resizing** — the original uploaded bytes are stored and served as-is.

### Voice messages

Recorded client-side with [Plugin.Maui.Audio](https://github.com/jfversluis/Plugin.Maui.Audio)
and uploaded through `ChatHub.SendVoiceMessage` - same blob storage and lazy-fetch model as an
image attachment, just with an audio content-type allowlist and a smaller size cap (~2 minutes of
compressed audio). The 🎤 composer button toggles recording; while recording, everyone else in
that chat sees a "🎤 X is recording a voice message…" indicator (`ChatHub.SetRecordingVoiceMessage`,
the same mechanism as the typing indicator but its own event). Tapping a received voice message
fetches and plays it, with a live progress bar and position (polled from the player - Plugin.Maui.Audio
doesn't push position updates itself). Requires microphone permission (`RECORD_AUDIO` on Android,
`NSMicrophoneUsageDescription` on iOS/macOS, the `microphone` capability on Windows) - the app
prompts for it on first use.

### Message search

The 🔍 button in the chat header searches the currently open chat's history
(`ChatHub.SearchMessages`) with a debounced, case-insensitive substring match; deleted messages
are excluded. Tapping a result jumps to it if it's already loaded into the visible message list,
or asks you to load earlier history first if it isn't - there's no "load history around this
message" API, only "load older from the top" (`LoadMoreHistoryCommand`).

### Message forwarding

Swipe a message and choose "Forward" to copy it (text and/or attachment) into any other chat
you're a member of. A forward is a brand-new message sent as you, not the original sender -
editing or deleting the original never touches the copy - and is labeled "Forwarded" in the UI
(`ChatMessageEntity.IsForwarded`).

### Delegated group admins & last seen

Group admin rights are no longer tied to whoever created the group: any admin can promote or
demote other members (`ChatHub.PromoteGroupAdmin`/`DemoteGroupAdmin`, from the chat's "Manage"
menu), and the last remaining admin can't be demoted. If the sole admin leaves or is removed, the
group automatically promotes another member rather than being left without one. Separately, "Last
seen" (from the same menu, DMs only) shows when an offline user was last connected
(`ApplicationUser.LastSeenUtc`, updated on disconnect); an online user has no last-seen entry.

### Known simplifications

A few deliberate scope cuts, worth knowing about if you extend this:

* **Read receipts are DM-only** in the UI — the server tracks them for any chat, but only a DM's last message shows a "Seen" marker.
* **Blocking only affects DMs** — it stops `CreateDm`/`SendToChat` between the two users, but doesn't remove either from a shared group or from seeing each other in the Lobby.
* **Display names aren't unique** (a pre-existing, documented tradeoff) — `CreateGroupChat`/`CreateDm` resolve a name to whichever user currently holds it in the server's directory. If two people share a name, starting a chat "by name" can resolve to the wrong one; this doesn't affect access to a chat you already have, since that's always checked by user id, not name.
* **No refresh tokens** — a login/register JWT is valid for 7 days flat (`Auth/JwtIssuer.cs`) with no rotation or revocation. Simple, but a compromised token stays valid until it expires, and there's no server-side "sign out everywhere" beyond changing the JWT signing key (which invalidates *every* session, not just one).
* **Attachments/avatars/voice messages as SQLite blobs** — see [Attachments & avatars](#attachments--avatars) above. Fine at this scale; a high-traffic deployment would want a real object store instead of growing the database file with binary data.
* **Forwarding doesn't cross a block** — forwarding into a DM still goes through the same block check as sending normally, but there's no separate "this content came from someone you've blocked" warning; it's just refused the same way a direct message would be.
* **Search is per-chat, not global** — `SearchMessages` only looks within one chat at a time; there's no "search across all my chats" view.

## Project structure

See the [Architecture](#architecture) section for the solution tree and component overview.

## Getting started

### Prerequisites

* **.NET 9 SDK**

* **.NET MAUI workloads** (install per OS):

  ```bash
  dotnet workload install maui
  dotnet workload install maui-windows      # on Windows
  dotnet workload install maui-android
  dotnet workload install maui-ios          # on macOS
  dotnet workload install maui-maccatalyst  # on macOS
  ```

* **Dev certificate (once):**

  ```bash
  dotnet dev-certs https --trust
  ```

### Clone

```bash
git clone https://github.com/AlexAhmanHV/chatter.git
cd chatter
```

### Configure

**JWT signing key (required — the server won't start without it)**

Chatter is its own identity provider: `/auth/register` and `/auth/login` check the password (ASP.NET Core Identity, hashed in the same SQLite database as everything else) and hand back a JWT the server signs itself with this key, then validates on every hub connection. There's nothing external to configure — no third-party account, no API keys — but the key itself is a real secret and must never be committed:

1. Generate a random secret, e.g.:
   ```bash
   openssl rand -base64 48
   ```
   On Windows without `openssl`, PowerShell works just as well:
   ```powershell
   [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
   ```
2. Put it in `Chatter.Server/appsettings.Development.json` (already git-ignored) as:
   ```json
   { "Jwt": { "SigningKey": "<paste your generated secret here>" } }
   ```
   For any real deployment, set it via the `Jwt__SigningKey` environment variable instead of a checked-in file.

Without this, `dotnet run` throws immediately at startup with a clear error naming the missing config key — it never silently falls back to something insecure.

Anyone who obtains this key can forge a valid login as any user, so treat it like a database password: never commit it, and rotate it (which invalidates every existing session) if it ever leaks.

**Chat data & accounts (SQLite)**

The server stores accounts (via ASP.NET Core Identity), messages, group chats, reactions, and read receipts in `Chatter.Server/chatter.db`, created automatically on first run via EF Core migrations (git-ignored). Delete the file to reset everything, including registered accounts.

Changed `ChatDbContext`'s model? Add a migration before running:

```bash
cd Chatter.Server
dotnet ef migrations add <DescriptiveName> -o Data/Migrations
```

**Backend URLs**

The API is configured to listen on:

* **HTTP:** `http://localhost:5291`
* **HTTPS:** `https://localhost:7062`

Check `Chatter.Server/Properties/launchSettings.json`

```json
{
  "profiles": {
    "Dev": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5291;https://localhost:7062",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

### Run the backend (ASP.NET Core)

From the `Chatter.Server` directory:

```bash
# using launch profile
 dotnet run --launch-profile Dev

# or explicitly specify URLs
 dotnet run --urls "http://localhost:5291;https://localhost:7062"
```

**Or with Docker** — no .NET SDK/workloads needed, just the backend, useful if you only want to poke at the API/hub:

```bash
docker compose up --build
```

Serves plain HTTP on `http://localhost:8080` (no HTTPS inside the container — put a reverse proxy in front of it for that in a real deployment). Chat history/accounts persist in a named Docker volume (`chatter-data`) across `docker compose down`/`up`. `docker-compose.yml` requires `Jwt__SigningKey` to be set to a real secret - see [Configure](#configure).

Building the image directly, without Compose:

```bash
docker build -f Chatter.Server/Dockerfile -t chatter-server .
docker run -p 8080:8080 -v chatter-data:/app/data chatter-server
```

### Run the client (MAUI)

MAUI projects are multi‑targeted; prefer `-t:Run -f <TFM>`.

From the `Chatter.Client` directory:

**Windows (WinUI):**

```bash
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-windows10.0.19041.0
```

**Android:**

```bash
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-android
```

**iOS (simulator on macOS):**

```bash
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-ios
```

**MacCatalyst:**

```bash
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-maccatalyst
```

### Build & run cheat‑sheet

```bash
# Backend (API)
cd Chatter.Server
dotnet dev-certs https --trust                 # first time only
dotnet run --launch-profile Dev

# Client (MAUI)
cd ../Chatter.Client
# Windows
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-windows10.0.19041.0
# Android
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-android
# iOS
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-ios
# macOS (MacCatalyst)
dotnet clean
dotnet build
dotnet build -t:Run -f net9.0-maccatalyst
```

## Troubleshooting

**HTTPS trust / certificates**

* Run `dotnet dev-certs https --trust` (Windows/macOS). On iOS simulators, you may need to trust the dev cert in the simulator.

**Android emulator can’t reach `localhost`**

* `Chatter.Client/Services/ServerConfig.cs` already handles this — the Android emulator gets `http://10.0.2.2:5291` automatically. Running on a **physical** Android/iOS device instead needs your dev machine's real LAN IP: edit `DevMachineLanIp` in that file.

**iOS simulator network**

* The simulator uses the host’s network, so `localhost` works as-is (`ServerConfig.cs` uses it for the simulator). A physical iOS device needs `DevMachineLanIp` set the same way as Android above.

**Server won't start: "Missing configuration value 'Jwt:SigningKey'"**

* See [Configure → JWT signing key](#configure) above — set `Jwt:SigningKey` in `Chatter.Server/appsettings.Development.json` (git-ignored) or the `Jwt__SigningKey` environment variable.

**Hub connection fails with 401 Unauthorized**

* The server requires a valid JWT (from `/auth/login` or `/auth/register`) for every `/hub/chat` connection. Make sure you're actually signed in (LoginPage) before the app tries to connect, and that the client's `ServerConfig.BaseUrl` points at the same server instance you registered against - a token from one server run won't validate against another that started with a different `Jwt:SigningKey`.

**Login/register returns 503 Service Unavailable**

* You've hit the per-IP rate limit on `/auth/*` (5 requests/minute, see `Program.cs`) - wait a minute and try again. This is deliberate brute-force protection, not a bug.

**Windows app fails to deploy**

* Ensure Windows SDK 10.0.19041+ is installed and you’re targeting `net9.0-windows10.0.19041.0`.

**XAML compiled binding warnings**

* Check each `Page` has `x:DataType` set to its ViewModel and that the project is built with XamlC enabled.

**SignalR / WebSockets issues**

* Verify Kestrel is listening on both HTTP and HTTPS and that proxies/dev‑tunnels aren’t blocking WebSockets.

## Contributing

Issues and PRs are welcome! Please:

1. Open an issue describing the change/bug.
2. Fork the repo and create a feature branch.
3. Write tests where it makes sense (`Chatter.Client.Tests`).
4. Submit a PR with a clear description and screenshots for UI changes.

## License

This project is licensed under the **MIT License**.

## Maintainers

* Alexander Åhman
* Albin Holmström
