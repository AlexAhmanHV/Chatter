# Chatter

> A cross‑platform chat sample built with .NET MAUI and ASP.NET Core, featuring real‑time rooms/DMs, typing indicators, presence, and emoji shortcodes.

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
* **Cross‑platform UI**: .NET MAUI app for Android, iOS, macOS (MacCatalyst), and Windows.

## Why it’s useful

This repo demonstrates how to:

* Structure an **MVVM** MAUI app with `CommunityToolkit.Mvvm`.
* Use **compiled bindings** (`x:DataType`) to eliminate XamlC warnings and speed up the UI.
* Drive UI with **ObservableCollection** state and event streams from a chat service.
* Host a simple **ASP.NET Core** backend with correct HTTP→HTTPS redirection.

## Architecture

High‑level flow: the ASP.NET Core backend exposes chat endpoints and pushes events; the MAUI client subscribes and renders them via MVVM.

```
Chatter.sln
├─ Chatter.Client/                      # .NET MAUI app (Android/iOS/MacCatalyst/Windows)
│  ├─ Views/                            # Pages (Login, Chat, Settings)
│  ├─ ViewModels/                       # VM layer (Login, Register, Chat, Settings)
│  ├─ Converters/                       # EmojiDisplayConverter
│  ├─ Messages/                         # DisplayNameChangedMessage
│  ├─ Services/                         # ChatService client, SupabaseAuthService (auth), EmojiCatalog
│  │  └─ Models/                        # ChatItem, PresenceStatus, UserPresenceItem
│  ├─ Helpers/                          # UI helpers, etc.
│  └─ Resources/                        # Styles, images
├─ Chatter.Server/                      # ASP.NET Core backend (SignalR or custom endpoints)
│  ├─ Hubs/                             # ChatHub
│  ├─ Program.cs                        # Kestrel endpoints + HTTPS redirection
│  ├─ appsettings*.json
│  └─ Properties/launchSettings.json
├─ Chatter.Client.Tests/                # Unit tests
│  └─ ChatTextParserTests
└─ Chatter.Shared/
   └─ Helpers/                          # ServiceHelper
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

**Client base URL**

In `Chatter.Client/ViewModels/ChatViewModel.cs`, set:

```csharp
private const string BaseUrl = "https://localhost:7062";
```

> Make sure this matches the HTTPS port you actually run on.

### Run the backend (ASP.NET Core)

From the `Chatter.Server` directory:

```bash
# using launch profile
 dotnet run --launch-profile Dev

# or explicitly specify URLs
 dotnet run --urls "http://localhost:5291;https://localhost:7062"
```

### Run the client (MAUI)

MAUI projects are multi‑targeted; prefer `-t:Run -f <TFM>`.

From the `Chatter.Client` directory:

**Windows (WinUI):**

```bash
dotnet build -t:Run -f net9.0-windows10.0.19041.0
```

**Android:**

```bash
dotnet build -t:Run -f net9.0-android
```

**iOS (simulator on macOS):**

```bash
dotnet build -t:Run -f net9.0-ios
```

**MacCatalyst:**

```bash
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
dotnet build -t:Run -f net9.0-windows10.0.19041.0
# Android
dotnet build -t:Run -f net9.0-android
# iOS
dotnet build -t:Run -f net9.0-ios
# macOS (MacCatalyst)
dotnet build -t:Run -f net9.0-maccatalyst
```

## Troubleshooting

**HTTPS trust / certificates**

* Run `dotnet dev-certs https --trust` (Windows/macOS). On iOS simulators, you may need to trust the dev cert in the simulator.

**Android emulator can’t reach `localhost`**

* Use `http://10.0.2.2:5291` (HTTP) or set your BaseUrl to the machine IP on your LAN with HTTPS properly trusted. For local HTTPS on emulator, ensure the emulator trusts the dev cert.

**iOS simulator network**

* The simulator uses the host’s network; if you stick with `https://localhost:7062`, ensure the dev certificate is trusted. Alternatively, use your Mac’s LAN IP.

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
