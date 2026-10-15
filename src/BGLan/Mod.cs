using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BGLan.Game;
using BGLan.PlayFabEmu;
using PlayFab;
using UnityEngine;

namespace BGLan
{
    public static class Mod
    {
        public const string Version = "1.3";

        private static bool initialized;
        private static HttpServer http;
        private static LanServer game;
        public static string Status = "";

        public static string PlayFabUrl
        {
            get
            {
                string url = "http://" + Config.ServerAddressForThisPC + ":" + Config.HttpPort;
                if (Config.TestId != 0) url += "/t" + Config.TestId;
                return url;
            }
        }

        public static bool ServersRunning
        {
            get { return game != null; }
        }

        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                Log.Info("Broken Ground LAN mod " + Version + " starting");
                Config.Load();
                var go = new GameObject("BGLan");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<LanBehaviour>();
                Apply();
            }
            catch (Exception e)
            {
                Log.Error("Init failed: " + e);
            }
        }

        /// <summary>Points the game at the LAN server and starts/stops the local server to match the config.</summary>
        public static void Apply()
        {
            PlayFabSettings.CompressApiData = false;
            PlayFabSettings.ProductionEnvironmentUrl = PlayFabUrl;
            Log.Info("PlayFab URL: " + PlayFabUrl);

            if (Config.IsHost && game == null) StartServers();
            else if (!Config.IsHost && game != null) StopServers();

            Status = Config.IsHost
                ? (ServersRunning ? "Server running. Friends connect to: " + string.Join(", ", LocalAddresses().ToArray()) : Status)
                : "Connecting to the server at " + Config.HostAddress;
        }

        private static void StartServers()
        {
            try
            {
                var store = new ProfileStore(Path.Combine(Config.GameDir, "BGLan_profiles.json"));
                game = new LanServer(Config.GamePort, store);
                game.Start();
                var emulator = new PlayFabEmulator(store, game);
                http = new HttpServer(Config.HttpPort, emulator.Handle);
                http.Start();
            }
            catch (Exception e)
            {
                Log.Error("Cannot start the LAN server: " + e);
                Status = "ERROR: cannot start the server (" + e.Message + ")";
                StopServers();
            }
        }

        private static void StopServers()
        {
            if (http != null) http.Stop();
            if (game != null) game.Stop();
            http = null;
            game = null;
        }

        public static void Tick()
        {
            MainThread.Drain();
            if (game != null) game.Tick();
        }

        public static void Shutdown()
        {
            StopServers();
        }

        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) list.Add(a.ToString());
                }
            }
            catch { }
            if (list.Count == 0) list.Add("?");
            return list;
        }
    }

    public class LanBehaviour : MonoBehaviour
    {
        private LanPanel panel;

        private static readonly List<KeyValuePair<float, Action>> later = new List<KeyValuePair<float, Action>>();

        public static void RunLater(float seconds, Action action)
        {
            later.Add(new KeyValuePair<float, Action>(Time.realtimeSinceStartup + seconds, action));
        }

        private void Update()
        {
            Mod.Tick();
            for (int i = later.Count - 1; i >= 0; i--)
            {
                if (later[i].Key > Time.realtimeSinceStartup) continue;
                Action a = later[i].Value;
                later.RemoveAt(i);
                try { a(); }
                catch (Exception e) { Log.Error("Delayed task failed: " + e); }
            }
        }

        private void OnApplicationQuit()
        {
            Mod.Shutdown();
        }

        private void OnGUI()
        {
            // Only before logging in: afterwards the server address cannot change.
            if (PlayFabClientAPI.IsClientLoggedIn()) return;
            if (panel == null) panel = new LanPanel();
            panel.Draw();
        }
    }
}
