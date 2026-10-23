# Broken Ground LAN

A fan-made mod that brings **Broken Ground** multiplayer back after the official servers were shut down.
A player hosts the server **inside their own game**, or runs a **dedicated server**;
everybody else connects to its IP address over LAN, a virtual LAN (Radmin VPN, Hamachi, ZeroTier,
Tailscale…) or the internet.

- Steam login, profiles, game list, lobby, chat, ready-up
- Turn-based matches with the original rules: teams, ammo draws, roulette, wind, gravity, walls, maps
- Kills, end of match, XP and stats saved on the server
- Everything unlocked for everybody (weapons, weapon packs, outfits, badges, maps, Pro options)

> Unofficial project, not affiliated with Bad Viking. You need your own copy of Broken Ground on Steam.

## Install

Get the latest zip from the [Releases](https://github.com/JDReachDev/BrokenGround-LAN/releases) page, extract it
and double click **`BGLanPatcher.exe`** (the game must be closed). It finds the game in your Steam libraries,
keeps a backup of the original files and also installs the dedicated server (`Start Dedicated Server.bat`).
Every player installs the mod, and everybody playing together should use the same version (shown in the panel).
To uninstall run `Uninstall.bat`.

## Hosting: two ways

Every game needs one server. It can run in two ways:

| | Host from the game | Dedicated server |
|---|---|---|
| How | Start the game, in the **LAN MULTIPLAYER** panel choose **HOST** → **APPLY** → **Connect** | Double click **`Start Dedicated Server.bat`** |
| The host plays? | Yes, the server runs inside their game | Not necessarily: it runs in a console window, without graphics |
| Server is up while | the host's game is open | the console window is open |
| Good for | a quick game with friends | a server that stays on |

The dedicated server shows its log live and accepts commands: `help`, `list` (games and players), `kick <name>`,
`ip` (addresses to share), `stop`. It stops when you type `stop` or close the window. You can also play on the same
PC: start the game normally and choose **HOST**, the game sees the local server and uses it.

Profiles, levels and stats are stored on the server PC in `BGLan_profiles.json` (game folder).

## Joining

1. Start the game, in the **LAN MULTIPLAYER** panel choose **JOIN**, type the **server IP**, press **APPLY**.
2. Click **Connect**, open *Multiplayer*, join a game from the list (or create one).
3. Everybody ticks **Ready**: the match starts with 2 or more players, the lobby does not need to be full.

## Connection modes: which IP to use

| Where the players are | What the server owner does | IP the others type |
|---|---|---|
| Same PC (dedicated server + game) | Nothing | `127.0.0.1` (or just choose **HOST**) |
| Same home network (LAN) | Nothing | the server's LAN IP, e.g. `192.168.1.10` |
| Different places, with a virtual LAN (Radmin VPN, Hamachi, ZeroTier, Tailscale) | Everybody joins the same virtual network | the server's VPN IP, e.g. `26.x.x.x` for Radmin, `25.x.x.x` for Hamachi |
| Different places, over the internet | Forward TCP ports **27080** and **27081** on the router to the server PC | the server's **public IP** (see e.g. whatismyip.com) or a dynamic DNS name |

The server panel / console (`ip` command) lists the server's local addresses. The virtual LAN is the easiest way
over the internet: no router setup, and it works even when the internet provider does not give you a public IP
(CGNAT), where port forwarding cannot work.

If players cannot connect, allow `BrokenGround.exe` through the Windows Firewall on the server PC (also on
"Public networks", which is how Windows often classifies VPN adapters).

Ports: **27080** is the account service (login, profiles, game list), **27081** is the game server.

## How it works

Broken Ground used two online services that no longer exist:

| Service | What it did | Replaced by |
|---|---|---|
| PlayFab (TitleId 448C) | Steam login, profiles, inventory, matchmaking, game list | `PlayFabEmulator` – a small HTTP server answering the PlayFab Client API calls the game makes |
| Authoritative TNet game server | Lobby, teams, turns, ammo, deaths, end of match | `LanServer` / `Lobby` – a TNet-compatible TCP server reimplementing that logic |

Both run inside the game (the host's game, or a headless copy for the dedicated server), so they can reuse the game's own data (weapon list, unlock rules, map data,
PlayFab JSON models, TNet serialization). The game's client code is reused untouched; the patcher only injects
a few calls:

| Injected into | Calls | Why |
|---|---|---|
| `GameManager.Awake` | `Hooks.Init` | starts the mod, points PlayFab to the LAN server, starts the servers when hosting |
| `Unlockable.IsUnlocked` | `Hooks.ForceUnlocked` | unlock everything |
| `HealthManager.HandleTakeDamage` (every return) | `Hooks.AfterHandleTakeDamage` | the acting player's game reports deaths; the server broadcasts `KillPlayer` |
| `GameManager.EndGame` (every return) | `Hooks.AfterEndGame` | saves the player's stats on the host |
| `TNetConnectionHandler.OnClientReceivedAmmo` | `Hooks.OnAmmo` | the original parser reads zero weapons when TCP packets arrive coalesced (common over VPNs) |

The protocol notes are in [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Repository layout

```
src/BGLan      the mod (net35, loaded by the game's Mono runtime)
  Config.cs, Mod.cs, Hooks.cs, MainThread.cs
  LanPanel.cs  the LAN MULTIPLAYER panel on the login screen
  Dedicated.cs dedicated server mode (headless game, console commands)
  PlayFabEmu/  HTTP server, PlayFab emulator, profile store
  Game/        TNet connections, lobby and turn logic, ammo calculation
src/Patcher    BGLanPatcher.exe (net472, Mono.Cecil): patches/unpatches the game
release/       files added to the release zip (player README, Uninstall.bat, dedicated server console script)
tools/         Python test clients: an end-to-end test and a dummy opponent bot
build.ps1      builds everything, patches the local game and creates the release zip
```

## Building

Requirements: Windows, .NET SDK 8+, Broken Ground installed.

```powershell
.\build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Broken Ground"
```

This compiles the mod against the game's assemblies, builds the installer, patches the given game install
(close the game first, or pass `-NoPatch`) and writes `dist\BrokenGround_LAN_v<version>.zip`.

Testing without a second player: run the game as host, then

```powershell
py tools\fake_clients_test.py      # two fake clients play a full match against the server
py tools\bot.py                    # a dummy opponent that joins your lobby and passes its turns
```

The game's own code is not included in this repository; the mod is compiled against your local installation.

## Support

If you enjoy playing Broken Ground again, you can support the project on Ko-fi:
[![Ko-fi](https://img.shields.io/badge/Ko--fi-support%20me-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/jdreach)

## License

[MIT](LICENSE) for the code in this repository. Broken Ground and its assets belong to their owners.
