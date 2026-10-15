using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TNet;

namespace BGLan.Game
{
    /// <summary>Builds one TNet packet: int32 size (excluding itself) + byte id + payload.</summary>
    public class PacketWriter
    {
        private readonly MemoryStream ms = new MemoryStream();
        public readonly BinaryWriter W;

        public PacketWriter(Packet id) : this((byte)id) { }

        public PacketWriter(byte id)
        {
            W = new BinaryWriter(ms);
            W.Write(0);
            W.Write(id);
        }

        public byte[] ToArray()
        {
            W.Flush();
            byte[] data = ms.ToArray();
            byte[] size = BitConverter.GetBytes(data.Length - 4);
            System.Buffer.BlockCopy(size, 0, data, 0, 4);
            return data;
        }
    }

    /// <summary>A TCP connection from a game client. Reading happens on its own thread, everything else on the main thread.</summary>
    public class Connection
    {
        public const int ProtocolVersion = 20160822;

        private static int nextPlayerId;

        public readonly int PlayerId;
        public readonly string Remote;
        public bool Verified;
        public bool Closed;

        // Set once the client authenticates with a matchmaker ticket.
        public Lobby Lobby;
        public string PlayFabId;
        public bool IsService;

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly LanServer server;
        private readonly object sendLock = new object();

        public Connection(TcpClient client, LanServer server)
        {
            this.client = client;
            this.server = server;
            client.NoDelay = true;
            stream = client.GetStream();
            Remote = client.Client.RemoteEndPoint.ToString();
            PlayerId = Interlocked.Increment(ref nextPlayerId);
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "BGLan conn " + PlayerId };
            t.Start();
        }

        private void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    if (!ReadExactly(header, 4)) break;
                    int size = BitConverter.ToInt32(header, 0);
                    if (size < 0 || size > 16 * 1024 * 1024)
                    {
                        Log.Warn("Bad packet size " + size + " from " + Remote);
                        break;
                    }
                    var body = new byte[size];
                    if (!ReadExactly(body, size)) break;
                    if (size == 0) continue;
                    MainThread.Post(() => server.OnPacket(this, body));
                }
            }
            catch (Exception e)
            {
                if (!Closed) Log.Info("Connection " + PlayerId + " read ended: " + e.Message);
            }
            MainThread.Post(() => server.OnDisconnected(this));
        }

        private bool ReadExactly(byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Send(byte[] packet)
        {
            if (Closed) return;
            lock (sendLock)
            {
                try
                {
                    stream.Write(packet, 0, packet.Length);
                }
                catch (Exception e)
                {
                    Log.Info("Send to " + PlayerId + " failed: " + e.Message);
                    Close();
                }
            }
        }

        public void Send(PacketWriter p)
        {
            Send(p.ToArray());
        }

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { client.Close(); } catch { }
        }
    }
}
