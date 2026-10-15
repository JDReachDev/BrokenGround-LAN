"""End-to-end test of the LAN server running inside the host's game, using two fake clients.

Usage: python tools/fake_clients_test.py [host]
"""
import json
import socket
import struct
import sys
import threading
import time
import urllib.request

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
HTTP = f"http://{HOST}:27080"
VERSION = 20160822

P = dict(RequestID=8, ResponseID=9, RequestPing=10, ResponsePing=11, Error=1, Auth=129, MatchmakerAuth=130, NewPlayer=131,
         PlayerLeft=133, JoinData=134, Chat=135, Host=138, Time=139, MapSeed=141, AssignTeams=142, StartGame=143, Ready=144,
         SegmentIndex=153, Spawn=154, Ammo=155, SetTurn=157, TurnTime=158, EndGame=160, Shoot=166, DamagePlayer=172,
         KillPlayer=173, TurnFinished=183, Wind=186, KillRequest=240)
NAMES = {v: k for k, v in P.items()}


def api(name, body, ticket=None):
    req = urllib.request.Request(f"{HTTP}/Client/{name}", data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", **({"X-Authorization": ticket} if ticket else {})})
    with urllib.request.urlopen(req, timeout=10) as r:
        resp = json.loads(r.read())
    if resp["code"] != 200:
        raise RuntimeError(f"{name}: {resp}")
    return resp["data"]


def steam_ticket(steamid):
    return (struct.pack("<I", 20) + b"\x11" * 8 + struct.pack("<Q", steamid) + b"\x22" * 40).hex()


def wstr(s):
    b = s.encode()
    n, out = len(b), bytearray()
    while True:
        byte = n & 0x7F
        n >>= 7
        out.append(byte | (0x80 if n else 0))
        if not n:
            break
    return bytes(out) + b


def rstr(data, pos):
    n = shift = 0
    while True:
        b = data[pos]; pos += 1
        n |= (b & 0x7F) << shift; shift += 7
        if not b & 0x80:
            break
    return data[pos:pos + n].decode(), pos + n


class Client:
    def __init__(self, name, steamid):
        self.name = name
        login = api("LoginWithSteam", {"SteamTicket": steam_ticket(steamid), "CreateAccount": True, "TitleId": "448C"})
        self.session = login["SessionTicket"]
        self.playfab = login["PlayFabId"]
        if not login["InfoResultPayload"]["AccountInfo"]["TitleInfo"].get("DisplayName"):
            api("UpdateUserTitleDisplayName", {"DisplayName": name}, self.session)
        inv = api("GetUserInventory", {}, self.session)
        print(f"[{name}] logged in as {self.playfab}, inventory items: {len(inv['Inventory'])}")
        self.packets = []
        self.cv = threading.Condition()
        self.player_id = None

    def connect(self, host, port, ticket):
        self.sock = socket.create_connection((host, port))
        self.send(P["RequestID"], struct.pack("<i", VERSION) + wstr(self.name) + wstr(""))
        threading.Thread(target=self.reader, daemon=True).start()
        pid, = struct.unpack("<i", self.wait("ResponseID")[4:8])
        self.player_id = pid
        self.send(P["MatchmakerAuth"], wstr(ticket))
        print(f"[{self.name}] connected, player id {pid}")

    def send(self, pid, payload=b""):
        body = bytes([pid]) + payload
        self.sock.sendall(struct.pack("<i", len(body)) + body)

    def reader(self):
        buf = b""
        while True:
            try:
                chunk = self.sock.recv(65536)
            except OSError:
                return
            if not chunk:
                return
            buf += chunk
            while len(buf) >= 4:
                size, = struct.unpack("<i", buf[:4])
                if len(buf) < 4 + size:
                    break
                body, buf = buf[4:4 + size], buf[4 + size:]
                with self.cv:
                    self.packets.append(body)
                    self.cv.notify_all()

    def wait(self, name, timeout=15, pred=None):
        pid = P[name]
        end = time.time() + timeout
        with self.cv:
            while True:
                for i, b in enumerate(self.packets):
                    if b[0] == pid and (pred is None or pred(b)):
                        del self.packets[:i + 1]
                        return b[1:]
                left = end - time.time()
                if left <= 0:
                    raise TimeoutError(f"[{self.name}] no {name}; got {[NAMES.get(b[0], b[0]) for b in self.packets]}")
                self.cv.wait(left)


def ints(b, n, off=0):
    return struct.unpack_from("<" + "i" * n, b, off)


def main():
    a = Client("Alfa", 76561198000000001)
    b = Client("Bravo", 76561198000000002)

    sg = api("StartGame", {"GameMode": "1v1", "BuildVersion": "1.1.2", "CustomCommandLineData": "3,0,0,0,0,0,False,False,False,False"}, a.session)
    print("StartGame:", sg["LobbyID"], sg["ServerHostname"], sg["ServerPort"])
    a.connect(sg["ServerHostname"], sg["ServerPort"], sg["Ticket"])
    a.wait("JoinData")
    print("[Alfa] host is", ints(a.wait("Host"), 1)[0])

    games = api("GetCurrentGames", {"BuildVersion": "1.1.2"}, b.session)["Games"]
    print("Current games:", [(g["LobbyID"], g["GameMode"], len(g["PlayerUserIds"]), g["Tags"]) for g in games])
    mm = api("Matchmake", {"LobbyId": games[0]["LobbyID"]}, b.session)
    b.connect(mm["ServerHostname"], mm["ServerPort"], mm["Ticket"])
    jd = b.wait("JoinData")
    print("[Bravo] JoinData", len(jd), "bytes, contains Alfa:", a.playfab.encode() in jd)
    np = a.wait("NewPlayer")
    pid, = ints(np, 1)
    print("[Alfa] NewPlayer", pid, rstr(np, 4)[0])

    # Profile of the other player, like UserData.LoadUserData does
    info = api("GetPlayerCombinedInfo", {"PlayFabId": b.playfab, "InfoRequestParameters": {"GetUserAccountInfo": True, "GetUserData": True}}, a.session)
    print("Bravo's name seen by Alfa:", info["InfoResultPayload"]["AccountInfo"]["TitleInfo"]["DisplayName"])

    a.send(P["Chat"], wstr("ciao!"))
    chat = b.wait("Chat")
    print("[Bravo] chat from", ints(chat, 1)[0], ":", rstr(chat, 4)[0])

    a.send(P["Ready"]); b.send(P["Ready"])
    print("[Bravo] sees ready:", b.wait("Ready").hex())
    for c in (a, b):
        c.wait("Time", pred=lambda x: ints(x[1:], 1)[0] == 0)
    teams = rstr(a.wait("AssignTeams"), 0)[0]
    seed = ints(a.wait("MapSeed"), 1)[0]
    seg_a, seg_b = ints(a.wait("SegmentIndex"), 1)[0], ints(b.wait("SegmentIndex"), 1)[0]
    mapi = ints(a.wait("StartGame"), 1)[0]
    b.wait("StartGame")
    print(f"Game start: teams={teams} seed={seed} segments={seg_a},{seg_b} map={mapi}")

    a.send(P["Spawn"], struct.pack("<ff", 100.0, 50.0))
    b.send(P["Spawn"], struct.pack("<ff", 900.0, 60.0))
    sp = b.wait("Spawn")
    print("[Bravo] sees spawn of", ints(sp, 1)[0], struct.unpack_from("<ff", sp, 4))
    for _ in range(2):
        am = a.wait("Ammo")
        soldier, xp, n = ints(am, 3)
        pairs = ints(am, 2 * n, 12)
        print(f"Ammo for soldier {soldier}: {n} weapons {list(zip(pairs[::2], pairs[1::2]))}")
    actor = ints(a.wait("SetTurn"), 1)[0]
    b.wait("SetTurn")
    print("First turn: soldier", actor, "turn time", ints(a.wait("TurnTime"), 1)[0])

    shooter, victim = (a, b) if actor == a.player_id else (b, a)
    shooter.send(P["Shoot"], struct.pack("<ffii", 50.0, 30.0, 1234, 100))
    print(f"[{victim.name}] receives Shoot:", struct.unpack("<ffii", victim.wait("Shoot")))
    shooter.send(P["DamagePlayer"], struct.pack("<iiiii", victim.player_id, 120, 150, 3, 0))
    print(f"[{victim.name}] receives DamagePlayer:", victim.wait("DamagePlayer").hex())
    shooter.send(P["KillRequest"], struct.pack("<iii?i", victim.player_id, shooter.player_id, 3, True, 151))
    for c in (a, b):
        k = c.wait("KillPlayer")
        print(f"[{c.name}] KillPlayer soldier={ints(k, 1)[0]} killer={ints(k, 1, 4)[0]}")
    shooter.send(P["TurnFinished"], struct.pack("<i", 200))
    print(f"[{victim.name}] TurnFinished frame", ints(victim.wait("TurnFinished"), 1)[0])
    victim.send(P["TurnFinished"])
    for c in (a, b):
        print(f"[{c.name}] EndGame winning team", ints(c.wait("EndGame"), 1)[0])
    print("\nALL OK")


if __name__ == "__main__":
    main()
