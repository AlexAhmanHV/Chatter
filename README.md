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

* **Lobby + DMs**: Join the lobby, start direct messages, see unread counts.
* **Presence & typing**: Online/away/busy status and “Alice is typing…” indicators.
* **Emoji shortcodes**: `:smile:` → 😄 via a converter and parser.
* **Name aliases/renames**: Seamless display‑name updates.
* **Persisted history**: Messages and display names survive a server restart (SQLite).
* **Authenticated by Supabase**: Every hub connection is validated against a real Supabase JWT — identity is the token's user id, not a name the client types in.
* **Cross‑platform UI**: .NET MAUI app for Android, iOS, macOS (MacCatalyst), and Windows.

## Why it’s useful

This repo demonstrates how to:

* Structure an **MVVM** MAUI app with `CommunityToolkit.Mvvm`.
* Use **compiled bindings** (`x:DataType`) to eliminate XamlC warnings and speed up the UI.
* Drive UI with **ObservableCollection** state and event streams from a chat service.
* Secure a **SignalR hub** with JWT bearer auth (including the query-string token workaround WebSockets need) and authorize actions server-side instead of trusting the client.
* Give a demo app **durable state** with EF Core + SQLite instead of only in-memory dictionaries.
* Host a simple **ASP.NET Core** backend with correct HTTP→HTTPS redirection.

## Architecture

High‑level flow: the client signs in with Supabase, gets a JWT, and presents it when opening the SignalR connection. The ASP.NET Core backend validates that JWT on every connection, resolves identity from it (never from client-supplied text), and pushes chat/presence events that the MAUI client renders via MVVM. Messages and display names are persisted to a local SQLite database so history survives a restart.

```
Chatter.sln
├─ Chatter.Client/                      # .NET MAUI app (Android/iOS/MacCatalyst/Windows)
│  ├─ Views/                            # Pages (Login, Chat, Settings)
│  ├─ ViewModels/                       # VM layer (Login, Register, Chat, Settings)
│  ├─ Converters/                       # EmojiDisplayConverter
│  ├─ Messages/                         # DisplayNameChangedMessage
│  ├─ Services/                         # ChatService client, SupabaseAuthService (auth),
│  │  │                                 # ServerConfig (per-platform backend URL), EmojiCatalog
│  │  └─ Models/                        # ChatItem, PresenceStatus, UserPresenceItem
│  ├─ Helpers/                          # UI helpers, etc.
│  ├─ SupabaseConfig.cs                 # Supabase project URL + anon key (public by design)
│  └─ Resources/                        # Styles, images
├─ Chatter.Server/                      # ASP.NET Core backend + SignalR hub
│  ├─ Hubs/                             # ChatHub — [Authorize]'d, identity from the JWT's "sub" claim
│  ├─ Auth/                             # SupabaseJwksRetriever (validates tokens against Supabase's JWKS)
│  ├─ Data/                             # ChatDbContext (EF Core + SQLite): messages, display names
│  ├─ Program.cs                        # Kestrel endpoints, JWT bearer auth, HTTPS redirection
│  ├─ appsettings*.json                 # Supabase:Url/Audience, ConnectionStrings:Chatter
│  ├─ Dockerfile
│  └─ Properties/launchSettings.json
├─ Chatter.Server.Tests/                # ChatHub authorization/persistence/rate-limit tests
├─ Chatter.Client.Tests/                # Unit tests
│  └─ ChatTextParserTests
├─ Chatter.Core/
│  └─ Services/                         # ChatTextParser (emoji shortcode parsing)
├─ Chatter.Shared/
│  ├─ Helpers/                          # ServiceHelper
│  └─ Models/                           # ChatSummary, ChatMessageDto — shared client/server DTOs
├─ docker-compose.yml                   # Runs Chatter.Server standalone (see Getting started)
└─ .github/workflows/ci.yml             # Build + test on push/PR
```


## Screens & features

* **LoginPage** – Email/password login.
* **RegisterPage** – Create an account (optional display name).
* **ChatPage** – Chats list, messages, composer, typing indicator, people panel.
* **SettingsPage** – Update display name.

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

**Supabase (required — the hub rejects unauthenticated connections)**

Chatter uses [Supabase](https://supabase.com) for email/password auth. You need a Supabase project either way:

1. Create a project (or use an existing one) and grab its **Project URL** and **anon/public key** from Project Settings → API.
2. Client: set `Url`/`AnonKey` in `Chatter.Client/SupabaseConfig.cs`. The anon key is meant to be public in client apps — Supabase enforces access with Row Level Security, not by keeping this secret.
3. Server: set `Supabase:Url` (and optionally `Supabase:Audience`, default `authenticated`) in `Chatter.Server/appsettings.json`. The server validates tokens against your project's JWKS endpoint automatically — no key needed there for newer Supabase projects.
   * Only if your project still uses the **legacy HS256 JWT secret** (Project Settings → API → JWT Settings): set `Supabase:JwtSecret` in `Chatter.Server/appsettings.Development.json` (git-ignored) instead of committing it.

Without this, `dotnet run` still starts, but every client connection to `/hub/chat` gets `401 Unauthorized`.

**Chat data (SQLite)**

The server stores messages and display names in `Chatter.Server/chatter.db`, created automatically on first run (git-ignored). Delete the file to reset all chat history.

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

Serves plain HTTP on `http://localhost:8080` (no HTTPS inside the container — put a reverse proxy in front of it for that in a real deployment). Chat history/display names persist in a named Docker volume (`chatter-data`) across `docker compose down`/`up`. To point the container at your own Supabase project instead of rebuilding the image, uncomment and set the `Supabase__*` environment variables in `docker-compose.yml` (`__` maps to the `:` in `Supabase:Url` etc.).

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

**Hub connection fails with 401 Unauthorized**

* The server requires a valid Supabase JWT for every `/hub/chat` connection — see [Configure → Supabase](#configure) above. Make sure `Chatter.Client/SupabaseConfig.cs` and `Chatter.Server/appsettings.json` point at the *same* Supabase project, and that you're actually signed in (LoginPage) before the app tries to connect.

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
