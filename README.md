# Chatter

> A cross‑platform chat sample built with .NET MAUI and ASP.NET Core, featuring real‑time rooms/DMs, typing indicators, presence, and emoji shortcodes.

![CI](https://github.com/AlexAhmanHV/Chatter/actions/workflows/ci.yml/badge.svg)
![Platforms](https://img.shields.io/badge/.NET%20MAUI-Android%20%7C%20iOS%20%7C%20MacCatalyst%20%7C%20Windows-512BD4)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Quick start (Windows, no coding required)

1. Download the latest Windows build from **[Releases](../../releases/latest)** - grab the `Chatter-Windows-*.zip` file under "Assets".
2. Unzip it anywhere and run `Chatter.Client.exe`. It's self-contained - no .NET install needed.
3. Windows SmartScreen will likely warn "Windows protected your PC" the first time, since this build isn't code-signed - click **More info** → **Run anyway**.
4. On the login screen, tap **Server address** and enter the address of a running Chatter server (e.g. `http://192.168.1.42:5291`) - either your own ([Configure](#configure) + [Run the backend](#run-the-backend-aspnet-core) below, optionally [exposed to the internet](#expose-your-server-to-the-internet-optional) via a tunnel) or one someone else is already hosting for you.
5. Register an account and start chatting.

This covers the Windows client only. The server, and every other platform (Android/iOS/macOS), still need to be built from source - see [Getting started](#getting-started) below.

---

## Table of contents

* [Quick start (Windows, no coding required)](#quick-start-windows-no-coding-required)
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
  * [Expose your server to the internet (optional)](#expose-your-server-to-the-internet-optional)
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
* **Voice messages with waveforms**: Record and play voice notes with a real per-message amplitude waveform, not a generic progress bar.
* **Video clips & documents**: Send several video clips or documents (PDF, Office, txt, zip) in one go.
* **Link previews**: Paste a URL into a message and it renders as a rich preview card (title, description, image).
* **Pin a chat**: Pin any chat to the top of the chat list, alongside the existing mute/block options.
* **Audio/video calls**: Call another DM participant, audio- or video-only, over WebRTC (STUN only, no TURN relay).
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
│  ├─ Views/                            # Pages (Login, Chat, Settings, Call)
│  ├─ ViewModels/                       # VM layer (Login, Register, Chat, Settings, Call)
│  ├─ Controls/                         # WaveformView (voice message amplitude rendering)
│  ├─ Converters/                       # EmojiDisplayConverter, InvertedBoolConverter, CallButtonConverters
│  ├─ Messages/                         # DisplayNameChangedMessage
│  ├─ Services/                         # ChatService client, ApiAuthService (calls /auth/*),
│  │  │                                 # ServerConfig (per-platform backend URL), EmojiCatalog
│  │  └─ Models/                        # ChatItem, ChatMessageItem, PresenceStatus, UserPresenceItem
│  ├─ Helpers/                          # UI helpers, etc.
│  └─ Resources/
│     ├─ Raw/wwwroot/                   # call.html - the WebRTC page hosted in CallPage's HybridWebView
│     └─ ...                            # Styles, images
├─ Chatter.Server/                      # ASP.NET Core backend + SignalR hub
│  ├─ Hubs/                             # ChatHub — [Authorize]'d, identity from the JWT's "sub" claim,
│  │                                    # incl. call signaling (CallInvite/Answer/Decline/IceCandidate/Hangup)
│  ├─ Auth/                             # JwtIssuer — signs the JWTs /auth/register and /auth/login return
│  ├─ Services/                         # LinkPreviewFetcher (SSRF-guarded URL metadata fetch + cache)
│  ├─ Data/                             # ChatDbContext (EF Core + SQLite + Identity): accounts, messages,
│  │  │                                 # reactions, group chats, blocks, mutes, pinned chats, read receipts
│  │  └─ Migrations/
│  ├─ Program.cs                        # Kestrel endpoints, Identity, JWT bearer auth, /auth/* endpoints
│  ├─ appsettings*.json                 # Jwt:SigningKey/Issuer/Audience, ConnectionStrings:Chatter
│  ├─ Dockerfile
│  └─ Properties/launchSettings.json
├─ Chatter.Server.Tests/                # ChatHub authorization/persistence/rate-limit tests,
│                                        # plus Identity/JwtIssuer tests (AuthTests.cs)
├─ Chatter.Client.Tests/                # Unit tests
│  ├─ ChatTextParserTests
│  └─ WavWaveformExtractorTests
├─ Chatter.Core/
│  └─ Services/                         # ChatTextParser (emoji shortcode parsing),
│                                        # WavWaveformExtractor (voice message waveform amplitudes)
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
* **ChatPage** – Chats list (Lobby/DMs/groups), messages with edit history/delete/react/seen/
  pinning/replies/attachments/voice notes/video/forwarding, in-chat search, composer with image/
  video-attach and record buttons, typing indicator, people panel with avatars, group admin
  management, "New group" toolbar action, "Load earlier messages" paging.
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
fetches and plays it, showing a real waveform and playback position (polled from the player -
Plugin.Maui.Audio doesn't push position updates itself). Requires microphone permission
(`RECORD_AUDIO` on Android, `NSMicrophoneUsageDescription` on iOS/macOS, the `microphone`
capability on Windows) - the app prompts for it on first use.

Playback renders a per-message amplitude waveform (`WaveformView`, a `GraphicsView`-backed
control) instead of a plain progress bar: amplitudes are extracted client-side from the recorded
WAV's 16-bit PCM samples once the audio is fetched (`Chatter.Core.Services.WavWaveformExtractor`,
one peak amplitude per bar, normalized so the loudest bar is full height), then rendered with
played/unplayed bars colored differently as playback advances. Extraction is pure C# with no
external decoding library, and falls back to a flat placeholder waveform if the audio isn't
16-bit PCM or fails to parse.

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
(`ChatMessageEntity.IsForwarded`). A forward never carries a reply link either, even if the
original was itself a reply - see Replies below.

### Replies

Swipe a message and choose "Reply" to attach it as a reply on your next message - a banner above
the composer shows what you're replying to, with an "✕" to cancel. The reply preview shown in a
bubble (`ReplyPreviewDto`) is resolved fresh on every fetch, not snapshotted at reply time: it
reflects a later edit, and turns into "Original message deleted" if the original is later
deleted. Tapping the preview jumps back to the original if it's already loaded (same "not loaded
yet" limitation as jumping to a search result - see Message search above). Replying only works
within the same chat as the original - `SendToChat`/`SendAttachment`/`SendVoiceMessage` all
validate that server-side, since without it a client could reference a message id from a chat it
has no access to and leak its snippet into a chat it does.

### Multiple image attachments

The 📎 button picks one or more images at once (`FilePicker.PickMultipleAsync`, not
`MediaPicker` - MAUI's `MediaPicker.PickPhotoAsync` only ever returns a single photo) and sends
each as its own message, sequentially. A pending reply is attached only to the first image sent.

### Delegated group admins & last seen

Group admin rights are no longer tied to whoever created the group: any admin can promote or
demote other members (`ChatHub.PromoteGroupAdmin`/`DemoteGroupAdmin`, from the chat's "Manage"
menu), and the last remaining admin can't be demoted. If the sole admin leaves or is removed, the
group automatically promotes another member rather than being left without one. Separately, "Last
seen" (from the same menu, DMs only) shows when an offline user was last connected
(`ApplicationUser.LastSeenUtc`, updated on disconnect); an online user has no last-seen entry.

### Group read receipts

A group chat's latest message from you shows "Seen by N/M" (`ChatHub.GetChatReadReceipts`
bootstraps who's read what when a group is first opened; live updates arrive the same way the DM
"Seen" marker does, via the `ReadReceipt` event) - the DM case stays a plain "Seen" checkmark
since there's only ever one other person to seen-check against.

### Pinned messages

Any current member can pin or unpin a message (swipe it, choose "Pin"/"Unpin" - same "no special
permission gate" approach as reactions and forwarding, rather than restricting it to group
admins). Pinned messages show in a horizontal strip under the chat header; tapping one scrolls to
it if it's already loaded. Capped at 20 pinned messages per chat (`ChatHub.MaxPinnedPerChat`) -
unpin something first once you hit it. Deleting a pinned message unpins it automatically.

### Video clips & documents

The 🎥 composer button picks one or more videos or documents at once
(`FilePicker.PickMultipleAsync` with a combined video+document file-type filter - `MediaPicker`
has no multi-select and no document support at all) and sends each as its own message,
sequentially, dispatched to `ChatHub.SendVideo` or `ChatHub.SendFile` based on the picked file's
guessed content type. A pending reply is attached only to the first file sent. Videos and
documents both use the same blob storage as an image or voice message - videos with a video
content-type allowlist, documents with an executable-extension denylist instead (`.exe`, `.dll`,
`.bat`, `.ps1`, etc.) - and a 20 MB cap either way.

Playback/opening is external either way: tapping a received video or document fetches it, writes
it to a cache file, and hands it to the OS's own handler via `Launcher.OpenAsync`, rather than
pulling in a dedicated media-playback control or document viewer. Duration isn't extracted
client-side for video (no reliable cross-platform way to read it from a picked file without a
media library) - videos just don't show a length, unlike voice messages.

### Message edit history

Every edit is recorded before it's overwritten (`MessageEditHistoryEntity`), not just the fact
that an edit happened. Tapping the "(edited)" label on a message shows its previous versions with
timestamps (`ChatHub.GetMessageEditHistory`). Editing to the exact same text doesn't add a history
entry.

### Link previews

Pasting a URL into a message shows a rich preview card (title, description, image) once it's
sent. The server fetches and parses the page (`Chatter.Server.Services.LinkPreviewFetcher`, plain
regex extraction of `<title>`/Open Graph meta tags, capped at 200 KB of HTML) rather than the
client doing it directly, both to keep the fetch off arbitrary client network paths and to cache
the result (in-memory, 1 hour) across everyone who links the same URL. The fetcher guards against
SSRF by resolving the URL's DNS and rejecting loopback/private/link-local addresses before
requesting it, so a message can't be used to probe the server's own internal network. A preview
that fails to fetch or parse (unreachable host, no metadata, blocked address) just doesn't show a
card - no error surfaces in the chat.

### Pinned chats

Pin any chat (Lobby, a DM, or a group) from its "⋮" menu to float it to the top of the chat list,
above everything except the Lobby, which always stays first regardless of pin state
(`ChatHub.SetChatPinned`, mirroring how muting a chat already works). This is a separate feature
from pinning a *message* within a chat - see [Pinned messages](#pinned-messages) above.

### Audio/video calls

Start an audio or video call with the 📞 button in a DM's header (calls are DM-only, not
available in group chats or the Lobby) - the other person sees an incoming-call alert with
Accept/Decline. Signaling (invite, answer, decline, ICE candidates, hangup) travels over the
existing SignalR hub connection as its own set of methods/events, rate-limited the same way as
other hub actions; the actual audio/video itself is a direct WebRTC peer connection negotiated
inside a `HybridWebView` page (`Resources/Raw/wwwroot/call.html`), using only a public STUN
server (`stun:stun.l.google.com:19302`) - no TURN relay.

This is the simplest option to start with, at a real cost: STUN alone can't establish a direct
connection through every kind of NAT/firewall (some mobile carriers and locked-down corporate
networks in particular), so some caller/callee pairs won't be able to connect at all - a known
limitation, not a bug, if a specific pair can't connect. A TURN relay server fixes that but needs
its own infrastructure (self-hosted, e.g. coturn, or a paid service) - a reasonable next step if
calls need to work reliably for everyone.

### Known simplifications

A few deliberate scope cuts, worth knowing about if you extend this:

* **Blocking only affects DMs** — it stops `CreateDm`/`SendToChat` between the two users, but doesn't remove either from a shared group or from seeing each other in the Lobby.
* **Display names aren't unique** (a pre-existing, documented tradeoff) — `CreateGroupChat`/`CreateDm` resolve a name to whichever user currently holds it in the server's directory. If two people share a name, starting a chat "by name" can resolve to the wrong one; this doesn't affect access to a chat you already have, since that's always checked by user id, not name.
* **No refresh tokens** — a login/register JWT is valid for 7 days flat (`Auth/JwtIssuer.cs`) with no rotation or revocation. Simple, but a compromised token stays valid until it expires, and there's no server-side "sign out everywhere" beyond changing the JWT signing key (which invalidates *every* session, not just one).
* **Attachments/avatars/voice messages as SQLite blobs** — see [Attachments & avatars](#attachments--avatars) above. Fine at this scale; a high-traffic deployment would want a real object store instead of growing the database file with binary data.
* **Forwarding doesn't cross a block** — forwarding into a DM still goes through the same block check as sending normally, but there's no separate "this content came from someone you've blocked" warning; it's just refused the same way a direct message would be.
* **Search is per-chat, not global** — `SearchMessages` only looks within one chat at a time; there's no "search across all my chats" view.
* **Calls are STUN-only, no TURN relay** — see [Audio/video calls](#audiovideo-calls) above; some caller/callee pairs behind restrictive NATs won't be able to connect at all.

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

**JWT signing key (nothing to do for local use — the server generates one itself)**

Chatter is its own identity provider: `/auth/register` and `/auth/login` check the password (ASP.NET Core Identity, hashed in the same SQLite database as everything else) and hand back a JWT the server signs itself with this key, then validates on every hub connection. There's nothing external to configure — no third-party account, no API keys.

For a plain `dotnet run`/first clone, you don't need to do anything: if `Jwt:SigningKey` isn't set, the server generates a random one on first startup and saves it next to `chatter.db` (`Chatter.Server/jwt-signing-key.txt`, git-ignored) so it's reused - not regenerated - on every later run. A console line on first startup tells you it did this and where.

**For any real deployment, set the key explicitly instead** via the `Jwt__SigningKey` environment variable (this is what `docker-compose.yml`'s template already requires) rather than relying on the auto-generated file:

1. Generate a random secret, e.g.:
   ```bash
   openssl rand -base64 48
   ```
   On Windows without `openssl`, PowerShell works just as well:
   ```powershell
   [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
   ```
2. Set it as `Jwt__SigningKey` in your deployment's environment, or put it in `Chatter.Server/appsettings.Development.json` (already git-ignored) as:
   ```json
   { "Jwt": { "SigningKey": "<paste your generated secret here>" } }
   ```

Anyone who obtains this key can forge a valid login as any user, so treat it like a database password: never commit it (auto-generated or not), and rotate it (which invalidates every existing session) if it ever leaks.

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

### Expose your server to the internet (optional)

Running the server as above only makes it reachable on your local network - someone on different
WiFi/mobile data can't reach `192.168.x.x`. Getting a real public address normally means
forwarding a port on your router (works, but opens your home network up) or renting a server
somewhere. For just playing around with friends, a **tunnel** is the easiest middle ground: it
gives you a temporary public HTTPS URL that forwards straight to your local server, with nothing
to configure on your router.

**Cloudflare Tunnel (recommended - no account needed for a quick one-off tunnel):**

1. Install `cloudflared`: [download](https://github.com/cloudflare/cloudflared/releases), or on
   Windows: `winget install --id Cloudflare.cloudflared`.
2. With the server already running locally, in a separate terminal:
   ```bash
   cloudflared tunnel --url http://localhost:5291
   ```
3. It prints a public URL like `https://random-words-here.trycloudflare.com`. Give that to
   whoever wants to connect - they enter it (with `https://`) in the app's "Server address"
   field instead of a LAN IP.

That URL is temporary: it changes every time you restart the tunnel, and stops working the
moment the `cloudflared` process exits - fine for a session with friends, not a permanent
address. For something longer-lived, either set up a named Cloudflare Tunnel (needs a free
Cloudflare account and a domain) or deploy the server somewhere with a stable address.

**ngrok** is a common alternative (`ngrok http 5291`) with the same idea, but it requires a free
account and an authtoken even for its most basic tunnel, where Cloudflare's quick tunnel needs
neither.

One thing to keep in mind: exposing the server publicly, even temporarily, means *anyone* with
the URL can register an account and use it - there's no invite system or access list (see
[Known simplifications](#known-simplifications)). Fine for a casual session with people you
trust with the link; don't post the URL somewhere public.

### Run the client (MAUI)

Just want to run the Windows app without building it? See [Quick start](#quick-start-windows-no-coding-required)
at the top of this README - it's a pre-built download from
[GitHub Releases](../../releases/latest), published by `.github/workflows/release-windows.yml`
(triggered on a version tag, or manually via the Actions tab).

Everything below is for building from source instead (any other platform, or if you want to
change the code):

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

* `Chatter.Client/Services/ServerConfig.cs` already handles this — the Android emulator gets `http://10.0.2.2:5291` automatically.

**Physical device (Android/iOS) can't reach the server**

* Open the app's login screen, tap "Server address ▼", and enter your dev machine's LAN IP (e.g. `http://192.168.1.42:5291`) - both devices need to be on the same network. This is a runtime setting (saved on-device via `Preferences`, see `ServerConfig.SetBaseUrl`), not a source-code edit - no rebuild needed, and it's remembered across app restarts.

**Server won't start: "Missing configuration value 'Jwt:SigningKey'"**

* This should no longer happen — if `Jwt:SigningKey` isn't configured, the server now generates one itself and saves it next to `chatter.db` (see `Auth/JwtSigningKeyResolver.cs` and [Configure → JWT signing key](#configure)), logging where it wrote it. If you still see this error, something is failing before that fallback runs; check the file permissions on the server's working directory.

**Hub connection fails with 401 Unauthorized**

* The server requires a valid JWT (from `/auth/login` or `/auth/register`) for every `/hub/chat` connection. Make sure you're actually signed in (LoginPage) before the app tries to connect, and that the client's `ServerConfig.BaseUrl` points at the same server instance you registered against - a token from one server run won't validate against another that started with a different `Jwt:SigningKey`.
* In Development, `Program.cs` deliberately skips `UseHttpsRedirection()` - redirecting the client's plain-HTTP negotiate call to the HTTPS port strips the `Authorization` header on that cross-origin redirect, which used to cause exactly this 401 out of the box. If you've re-enabled the redirect in Development, that's almost certainly why.

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
