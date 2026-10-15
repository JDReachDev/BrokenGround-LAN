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
        public const string Version = "1.2";

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
        private string ipField;
        private bool hostField;
        private Rect window = new Rect(10, 10, 360, 210);
        private bool applied;
        private bool appliedHost;
        private string appliedIp;
        private GUIStyle appliedStyle;

        private void Start()
        {
            ipField = Config.HostAddress;
            hostField = Config.IsHost;
        }

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
            window = GUILayout.Window(735001, window, DrawWindow, "Broken Ground LAN " + Mod.Version);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(hostField, " I host")) hostField = true;
            if (GUILayout.Toggle(!hostField, " Join a friend")) hostField = false;
            GUILayout.EndHorizontal();

            if (!hostField)
            {
                GUILayout.Label("IP address of the host PC:");
                ipField = GUILayout.TextField(ipField ?? "", 64);
            }

            if (GUILayout.Button("Apply"))
            {
                Config.IsHost = hostField;
                if (!hostField) Config.HostAddress = (ipField ?? "").Trim();
                Config.Save();
                Mod.Apply();
                appliedHost = hostField;
                appliedIp = ipField;
                applied = true;
            }

            // The confirmation disappears as soon as the settings are edited again.
            if (applied && (hostField != appliedHost || (!hostField && ipField != appliedIp))) applied = false;
            if (applied)
            {
                if (appliedStyle == null)
                {
                    appliedStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true };
                    appliedStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);
                }
                GUILayout.Label("Settings applied! Now click \"Connect\".", appliedStyle);
            }

            GUILayout.Space(6);
            GUILayout.Label(Mod.Status);
            GUI.DragWindow();
        }
    }
}
