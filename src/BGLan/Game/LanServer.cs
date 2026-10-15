using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BGLan.PlayFabEmu;
using Packet = TNet.Packet;

namespace BGLan.Game
{
    /// <summary>
    /// Replacement for the official Broken Ground game server: a TNet-compatible TCP server hosting lobbies.
    /// Network threads only read; all logic runs on the Unity main thread.
    /// </summary>
    public class LanServer
    {
        private class TicketInfo
        {
            public Lobby Lobby;
            public Profile Profile;
        }

        public readonly ProfileStore Profiles;
        private readonly int port;
        private TcpListener listener;
        private volatile bool running;
        private readonly long startTime = NowMs();

        private readonly List<Lobby> lobbies = new List<Lobby>();
        private readonly Dictionary<string, TicketInfo> tickets = new Dictionary<string, TicketInfo>();
        private readonly List<Connection> connections = new List<Connection>();
        private int nextLobbyId = 1;

        public readonly Lobby ServiceLobby;

        public LanServer(int port, ProfileStore profiles)
        {
            this.port = port;
            Profiles = profiles;
            ServiceLobby = new Lobby(this, "service", "Service", null) { IsServiceLobby = true };
        }

        public IEnumerable<Lobby> Lobbies
        {
            get { return lobbies; }
        }

        public static long NowMs()
        {
            return DateTime.UtcNow.Ticks / 10000;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "BGLan game accept" };
            t.Start();
            Log.Info("Game server listening on port " + port);
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            foreach (Connection c in connections.ToArray()) c.Close();
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    MainThread.Post(() =>
                    {
                        var c = new Connection(client, this);
                        connections.Add(c);
                        Log.Info("Connection " + c.PlayerId + " from " + c.Remote);
                    });
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        // ---------------- lobbies & tickets (called by the PlayFab emulator) ----------------

        public Lobby CreateLobby(string gameMode, string customData)
        {
            if (!GameModes.modes.ContainsKey(gameMode ?? "")) gameMode = GameModes.FFA2;
            var lobby = new Lobby(this, "lan" + (nextLobbyId++), gameMode, customData);
            lobbies.Add(lobby);
            Log.Info("Lobby " + lobby.Id + " created (" + gameMode + ")");
            return lobby;
        }

        public Lobby FindLobby(string id)
        {
            return lobbies.FirstOrDefault(l => l.Id == id);
        }

        public string IssueTicket(Lobby lobby, Profile profile)
        {
            string ticket = Guid.NewGuid().ToString("N");
            tickets[ticket] = new TicketInfo { Lobby = lobby, Profile = profile };
            return ticket;
        }

        public void RemoveLobby(Lobby lobby)
        {
            if (lobbies.Remove(lobby)) Log.Info("Lobby " + lobby.Id + " closed");
        }

        // ---------------- packets ----------------

        public void OnPacket(Connection c, byte[] body)
        {
            if (c.Closed) return;
            var reader = new BinaryReader(new MemoryStream(body));
            byte id = reader.ReadByte();

            if (!c.Verified)
            {
                if (id != (byte)Packet.RequestID || reader.ReadInt32() != Connection.ProtocolVersion)
                {
                    var err = new PacketWriter(Packet.Error);
                    err.W.Write("Version mismatch! Server is running a different protocol version!");
                    c.Send(err);
                    c.Close();
                    return;
                }
                c.Verified = true;
                var resp = new PacketWriter(Packet.ResponseID);
                resp.W.Write(Connection.ProtocolVersion);
                resp.W.Write(c.PlayerId);
                resp.W.Write(NowMs());
                resp.W.Write(startTime);
                c.Send(resp);
                return;
            }

            switch ((Packet)id)
            {
                case Packet.RequestPing:
                    c.Send(new PacketWriter(Packet.ResponsePing));
                    return;
                case Packet.RequestSetUDP:
                    var udp = new PacketWriter(Packet.ResponseSetUDP);
                    udp.W.Write((ushort)0);
                    c.Send(udp);
                    return;
                case Packet.RequestNoDelay:
                case Packet.RequestSetName:
                case Packet.RequestSetTimeout:
                case Packet.RequestSetPlayerData:
                case Packet.ServerLog:
                    return;
                case Packet.MatchmakerAuth:
                case Packet.Auth:
                    Authenticate(c, reader.ReadString());
                    return;
            }

            if (c.Lobby == null)
            {
                Log.Warn("Packet " + (Packet)id + " from unauthenticated connection " + c.PlayerId);
                return;
            }
            try
            {
                c.Lobby.OnPacket(c, (Packet)id, reader, body);
            }
            catch (Exception e)
            {
                Log.Error("Error handling " + (Packet)id + " from " + c.PlayerId + ": " + e);
            }
        }

        private void Authenticate(Connection c, string ticket)
        {
            TicketInfo info;
            if (ticket == null || !tickets.TryGetValue(ticket, out info))
            {
                Log.Warn("Unknown ticket from connection " + c.PlayerId);
                c.Close();
                return;
            }
            tickets.Remove(ticket);
            c.PlayFabId = info.Profile.PlayFabId;
            if (info.Lobby.IsServiceLobby)
            {
                c.IsService = true;
                c.Lobby = info.Lobby;
                return;
            }
            if (!lobbies.Contains(info.Lobby) || info.Lobby.IsFull || info.Lobby.InGame)
            {
                c.Send(new PacketWriter(Packet.GameFull));
                c.Close();
                return;
            }
            c.Lobby = info.Lobby;
            info.Lobby.Join(c, info.Profile);
        }

        public void OnDisconnected(Connection c)
        {
            if (!connections.Remove(c)) return;
            c.Close();
            Log.Info("Connection " + c.PlayerId + " closed");
            if (c.Lobby != null && !c.IsService) c.Lobby.Leave(c);
        }

        /// <summary>Called every frame from the main thread.</summary>
        public void Tick()
        {
            foreach (Lobby l in lobbies.ToArray()) l.Tick();
        }
    }
}
