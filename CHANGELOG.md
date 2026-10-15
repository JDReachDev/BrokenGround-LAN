# Changelog

## 1.2
- Everything is now in English (in-game panel, installer, documentation).
- The release ships an installer (`BGLanPatcher.exe`) instead of modified game files: it finds the game through
  your Steam libraries, backs up the original files and patches your own copy. It runs on any Windows 10/11
  without installing .NET.
- `Uninstall.bat` restores the original game files.

## 1.1
- Fixed: a player connected through a VPN (Radmin, Hamachi, ...) could end up with no weapons, which broke the
  match (soldier sliding instead of walking, desync).
- Fixed: when the turn timer ran out without a shot, the match stayed stuck at 0.
- Teleporting now stops the turn timer like a shot.
- After pressing "Apply" the panel shows: Settings applied! Now click "Connect".
- Removed an error in the news screen.

## 1.0
- First version: LAN server running inside the host's game, Steam login, game list, lobby, turns, ammo,
  stats saved on the host, everything unlocked.
