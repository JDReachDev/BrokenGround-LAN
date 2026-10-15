# Broken Ground LAN

A fan-made mod that brings **Broken Ground** multiplayer back after the official servers were shut down.
One player hosts a server **inside their own game**; friends connect to the host's IP address over LAN,
a virtual LAN (Radmin VPN, Hamachi, ZeroTier, Tailscale…) or the internet.

- Steam login, profiles, game list, lobby, chat, ready-up
- Turn-based matches with the original rules: teams, ammo draws, roulette, wind, gravity, walls, maps
- Kills, end of match, XP and stats saved on the host
- Everything unlocked for everybody (weapons, weapon packs, outfits, badges, maps, Pro options)

> Unofficial project, not affiliated with Bad Viking. You need your own copy of Broken Ground on Steam.

## Download and install

Get the latest zip from the [Releases](https://github.com/JDReachDev/BrokenGround-LAN/releases) page, extract it
and double click **`BGLanPatcher.exe`** (the game must be closed). The full player guide is in the zip's
`README.txt`; the short version:

1. **Host:** start the game, choose **I host** in the *Broken Ground LAN* panel, press **Apply**, click **Connect**,
   then *Multiplayer → Create Game*.
2. **Friends:** choose **Join a friend**, type the host's IP, press **Apply**, click **Connect**, join the game from the list.
3. Everybody ticks **Ready**.

Over the internet the host must be reachable on TCP ports **27080** (account service) and **27081** (game server).

## How it works

Broken Ground used two online services that no longer exist:

| Service | What it did | Replaced by |
|---|---|---|
| PlayFab (TitleId 448C) | Steam login, profiles, inventory, matchmaking, game list | `PlayFabEmulator` – a small HTTP server answering the PlayFab Client API calls the game makes |
| Authoritative TNet game server | Lobby, teams, turns, ammo, deaths, end of match | `LanServer` / `Lobby` – a TNet-compatible TCP server reimplementing that logic |

Both run inside the host's game, so they can reuse the game's own data (weapon list, unlock rules, map data,
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
  PlayFabEmu/  HTTP server, PlayFab emulator, profile store
  Game/        TNet connections, lobby and turn logic, ammo calculation
src/Patcher    BGLanPatcher.exe (net472, Mono.Cecil): patches/unpatches the game
release/       files added to the release zip (player README, Uninstall.bat)
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
