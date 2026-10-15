# Broken Ground network protocol notes

Reverse engineered from the game client (version 1.1.2, Unity 2018.1, Mono / .NET 3.5).

## PlayFab

`PlayFabSettings.GetFullUrl` accepts a full `http://` URL, so setting `PlayFabSettings.ProductionEnvironmentUrl`
redirects every call. Requests are JSON POSTs to `/Client/<Api>`, authenticated with the `X-Authorization` header
(the session ticket). Responses use the envelope `{"code":200,"status":"OK","data":{...}}`; errors use
`{"code":400,"error":"...","errorCode":N,"errorMessage":"..."}`.

APIs used by the game: `LoginWithSteam`, `UpdateUserTitleDisplayName`, `GetPlayerCombinedInfo`, `GetUserInventory`,
`UpdateUserData`, `GetTime`, `GetTitleData`, `GetTitleNews`, `GetFriendsList`, `GetCatalogItems`, `GetStoreItems`,
`ExecuteCloudScript`, `GetLeaderboard*`, `GetAccountInfo`, `StartGame`, `Matchmake`, `GetCurrentGames`.

- The Steam auth session ticket holds the SteamID64 at byte 12 (after a 4 byte length and the 8 byte GC token).
- `StartGame.CustomCommandLineData` = `map,health,time,walls,ceiling,gravity,wind,roulette,bigExplosions,private`.
- `Matchmake` with `GameMode = "Service"` is the "service room" (invites); the mod accepts it and keeps it idle.

## TNet

TNet protocol version `20160822`. Every packet: `int32 size` (not counting itself) + `byte id` + payload, strings
as .NET `BinaryWriter` strings.

- Handshake: `RequestID(int version, string name, DataNode)` → `ResponseID(int version, int playerId, long time, long startTime)`.
- `RequestPing` → `ResponsePing` (empty).
- Game packets are ids 129–204 (`TNet.Packet`). The mod adds id 240 (`KillRequest`).
- `JoinData` contains `JoinGameData` and `SendableGameOptions` written with TNet's object serialization
  (`Serialization.WriteObject`).

### Client → server → other clients

Relayed unchanged to everybody except the sender: `Walk, StopWalk, ChangeAngle, ChangeWeapon, Teleport,
TeleportAfterHit, Shoot, CreateProjectile, RemoveProjectile, HitPlayer, HitObject, Explode, ExplodeWithVel,
SetPosVel, SetPosVelWithRot, SetHomingTarget, Steer, Shatter, ExplodeBarrel, PressButton, SetPosWithNetID,
SetVelWithNetID, CutHole`.

Transformed by the server:

| Client sends | Others receive |
|---|---|
| `Chat(msg)` | `Chat(playerId, msg)` |
| `Spawn(x, y)` | `Spawn(playerId, x, y)` |
| `CollectItem(netId, challenge, frame)` | `CollectItem(netId, frame)` |
| `ShootRemote(power, angle, seed, button, weapon, frame)` | `ShootRemote(angle, weapon, seed, button, frame)` |
| `DamagePlayer(target, damage, frame, weapon, projectile)` | `DamagePlayer(target, damage, electric, frame)`; friendly fire is redirected to the shooter |
| `Ready()` | `Ready(playerId, isReady)` |
| `ChangeTeam()` | `ChangeTeam(playerId, team)` (to everybody) |
| `Emblem(i)` / `Badge(i)` | `Emblem(i, playerId)` / `Badge(i, playerId)` |
| `Outfit(8 ints)` | `Outfit(playerId, 8 ints)` |
| `ChangeToggle(id)` / `ChangeOption(id)` (host) | `(id, new value)` to everybody |
| `TurnFinished(frame)` from the acting player | `TurnFinished(frame)`; the others reply with an empty `TurnFinished` |

### Sent by the server

`JoinData, NewPlayer, PlayerLeft, Host, Time (lobby countdown), MapSeed, AssignTeams ("id,team|..."),
SegmentIndex, StartGame(map), Ammo(soldier, xp, pairCount, (weapon, amount)...), SingleAmmo (roulette), Wind,
Gravity, SetTurn, TurnTime, KillPlayer(soldier, killer, weapon, headshot, frame), EndGame(team or -1)`.

Soldier ids are TNet player ids. Weapon ids are indexes into the sorted list of weapon ids.

### Simulation model

Lockstep: the acting player's game simulates the turn and sends events stamped with a frame number; the other games
replay them at that frame (`RPCQueue`). Direct hits are applied locally by every game; explosion damage is sent as
`DamagePlayer`. Online, soldiers only die when `KillPlayer` arrives, so the mod lets the acting player's game report
deaths and the server broadcasts them.

When the turn timer reaches 0 without a shot the acting game freezes the soldier and never sends `TurnFinished`,
so the server ends the turn itself.
