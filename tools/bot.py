"""A dummy opponent for testing alone: joins the first open lobby, readies up, spawns and always passes its turn.

Usage: python tools/bot.py [host] [name]
"""
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
import fake_clients_test as t  # noqa: E402

if len(sys.argv) > 1:
    t.HOST = sys.argv[1]
    t.HTTP = f"http://{t.HOST}:27080"
NAME = sys.argv[2] if len(sys.argv) > 2 else "Bot"


def main():
    bot = t.Client(NAME, 76561198099999000 + (abs(hash(NAME)) % 999))
    while True:
        games = [g for g in t.api("GetCurrentGames", {"BuildVersion": "1.1.2"}, bot.session)["Games"]
                 if g["Tags"]["InGame"] == "False" and g["Tags"]["Full"] == "False"]
        if games:
            break
        print("Waiting for a lobby... (create a game in Broken Ground)")
        time.sleep(3)
    mm = t.api("Matchmake", {"LobbyId": games[0]["LobbyID"]}, bot.session)
    bot.connect(mm["ServerHostname"], mm["ServerPort"], mm["Ticket"])
    bot.wait("JoinData")
    bot.send(t.P["Chat"], t.wstr("Hi, I am a bot. Press Ready!"))

    def ready():
        bot.send(t.P["Ready"])
        print("Ready sent")

    ready()
    while True:
        b = bot.next_packet()
        name = t.NAMES.get(b[0], b[0])
        if name == "StartGame":
            print("Game starting")
            spawned = False
        elif name == "Spawn" and not spawned:
            pid, x, y = struct.unpack_from("<iff", b, 1)
            bot.send(t.P["Spawn"], struct.pack("<ff", -x, y + 150))
            spawned = True
            print(f"Player {pid} spawned at {x:.0f},{y:.0f}; bot spawns at {-x:.0f}")
        elif name == "SetTurn":
            soldier, = struct.unpack_from("<i", b, 1)
            if soldier == bot.player_id:
                print("Bot's turn: passing in 3 s")
                time.sleep(3)
                bot.send(t.P["TurnFinished"], struct.pack("<i", 0))
        elif name == "TurnFinished":
            bot.send(t.P["TurnFinished"])
        elif name == "KillPlayer":
            print("KillPlayer", struct.unpack_from("<ii", b, 1))
        elif name == "EndGame":
            print("Game over, winning team", struct.unpack_from("<i", b, 1)[0])
            time.sleep(12)
            ready()
        elif name == "Chat":
            pid = struct.unpack_from("<i", b, 1)[0]
            print(f"Chat from {pid}: {t.rstr(b, 5)[0]}")
        elif name == "Ready" and len(b) >= 6:
            print("Ready from", struct.unpack_from("<i?", b, 1))


def next_packet(self, timeout=3600):
    end = time.time() + timeout
    with self.cv:
        while not self.packets:
            self.cv.wait(max(0.1, end - time.time()))
        return self.packets.pop(0)


t.Client.next_packet = next_packet

if __name__ == "__main__":
    main()
