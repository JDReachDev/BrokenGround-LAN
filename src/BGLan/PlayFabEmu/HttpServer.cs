using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BGLan.PlayFabEmu
{
    public class HttpRequestInfo
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body;
        /// <summary>The address of this PC as seen by the caller (used to tell clients where the game server is).</summary>
        public string LocalAddress;
        public string RemoteAddress;

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }
    }

    /// <summary>Tiny HTTP/1.1 server. Requests are handled on the Unity main thread through MainThread.Invoke.</summary>
    public class HttpServer
    {
        private readonly int port;
        private readonly Func<HttpRequestInfo, string> handler;
        private TcpListener listener;
        private Thread thread;
        private volatile bool running;

        public HttpServer(int port, Func<HttpRequestInfo, string> handler)
        {
            this.port = port;
            this.handler = handler;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "BGLan HTTP" };
            thread.Start();
            Log.Info("PlayFab emulator listening on port " + port);
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { if (!running) return; continue; }
                var t = new Thread(() => HandleClient(client)) { IsBackground = true };
                t.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 15000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    HttpRequestInfo req = ReadRequest(stream);
                    if (req == null) return;
                    req.LocalAddress = ((IPEndPoint)client.Client.LocalEndPoint).Address.ToString();
                    req.RemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

                    string body;
                    try
                    {
                        body = MainThread.Invoke(() => handler(req));
                    }
                    catch (Exception e)
                    {
                        Log.Error("HTTP handler failed for " + req.Path + ": " + e);
                        body = "{\"code\":500,\"status\":\"InternalServerError\",\"error\":\"InternalServerError\",\"errorCode\":1123,\"errorMessage\":\"LAN server error\"}";
                    }
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch (Exception e)
            {
                Log.Warn("HTTP connection error: " + e.Message);
            }
        }

        private static HttpRequestInfo ReadRequest(Stream stream)
        {
            var headerBytes = new MemoryStream();
            int last4 = 0;
            while (last4 != 0x0D0A0D0A)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                headerBytes.WriteByte((byte)b);
                last4 = (last4 << 8) | b;
                if (headerBytes.Length > 65536) return null;
            }
            string[] lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new HttpRequestInfo { Method = first[0], Path = first[1] };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int length = 0;
            string cl = req.Header("Content-Length");
            if (cl != null) int.TryParse(cl, out length);
            byte[] body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(body, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            string enc = req.Header("Content-Encoding");
            if (enc != null && enc.ToLowerInvariant().Contains("gzip"))
            {
                using (var gz = new GZipStream(new MemoryStream(body, 0, read), CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    var buf = new byte[4096];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                    body = outMs.ToArray();
                    read = body.Length;
                }
            }
            req.Body = Encoding.UTF8.GetString(body, 0, read);
            return req;
        }
    }
}
