BROKEN GROUND LAN MOD {VERSION}
===============================

Play Broken Ground with your friends again, now that the official servers are closed.
One player hosts the server inside their own game; the others connect to their IP address.
Everyone needs their own copy of Broken Ground in their Steam library.


INSTALL (every player)
----------------------
1. Close Broken Ground.
2. Extract this zip anywhere (for example on your Desktop).
3. Double click BGLanPatcher.exe.
   It finds the game through your Steam libraries (if it can't, it asks you to paste
   the game folder: in Steam, right click Broken Ground > Manage > Browse local files).
4. When it says "Done!", press Enter. That's it.

The installer keeps a backup of the original game file (Assembly-CSharp.orig.dll).
To update to a newer version of the mod, just run the new BGLanPatcher.exe.
Everybody playing together should use the same mod version: it is shown in the title
of the "Broken Ground LAN" panel in the game.


PLAY
----
Host (the player who runs the server):
1. Start Broken Ground. In the "Broken Ground LAN" panel choose "I host" and press "Apply".
   The panel shows the IP addresses your friends can use.
2. Click "Connect" (the game's Steam login button), then Multiplayer > Create Game.

Friends:
1. Start Broken Ground. In the "Broken Ground LAN" panel choose "Join a friend",
   type the host's IP address and press "Apply".
2. Click "Connect", open the game list and join the host's game.

When everybody is in the lobby, tick "Ready": the match starts after a short countdown.
A match can start with 2 or more players even if the lobby is not full.


PLAYING OVER THE INTERNET
-------------------------
The host must be reachable on TCP ports 27080 and 27081. Easiest options:
- A virtual LAN such as Radmin VPN, Hamachi, ZeroTier or Tailscale: everybody joins the
  same network and friends use the host's VPN address (e.g. 26.x.x.x for Radmin).
- Or forward TCP ports 27080 and 27081 on the host's router to the host PC and give
  friends your public IP (does not work if your provider uses CGNAT).

If friends cannot connect, allow BrokenGround.exe through the Windows Firewall
(also on "Public networks", which is how Windows often classifies VPN adapters).


PROGRESS AND UNLOCKS
--------------------
- Every item, weapon, outfit and map is unlocked for everybody.
- Levels and stats are saved on the HOST's PC in BGLan_profiles.json (in the game folder).
  If someone else hosts, copy that file to their game folder to keep your progress.


FILES IN THE GAME FOLDER
------------------------
BGLan.cfg            mod settings (host/client, host IP, ports, unlock_all)
BGLan.log            mod log, useful when reporting problems
BGLan_profiles.json  player profiles (host only)


UNINSTALL
---------
Double click Uninstall.bat (or verify the game files in Steam).


This is an unofficial fan-made mod, not affiliated with Bad Viking.
Source code: https://github.com/JDReachDev/BrokenGround-LAN
