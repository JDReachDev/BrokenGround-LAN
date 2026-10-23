using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BGLan
{
    /// <summary>
    /// Dedicated server mode: the game runs headless (-batchmode -nographics -bglan-server), started by
    /// "Start Dedicated Server.bat". The console window shows BGLan_server.log and passes typed commands
    /// through BGLan_server_cmd.txt. The server quits when that window closes (-bglan-parent PID).
    /// </summary>
    public static class Dedicated
    {
        public const string LogFile = "BGLan_server.log";
        private const string CommandFile = "BGLan_server_cmd.txt";

        public static readonly bool Enabled = Environment.GetCommandLineArgs().Contains("-bglan-server");

        private static int parentPid;
        private static float nextCheck;

        public static void Init()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-bglan-parent");
            if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out parentPid);

            Config.IsHost = true;
            Application.targetFrameRate = 30;
            QualitySettings.vSyncCount = 0;
            AudioListener.volume = 0f;
            BlockSteam();
            try { File.Delete(Path.Combine(Config.GameDir, CommandFile)); } catch { }
            Log.Info("Dedicated server mode");
        }

        /// <summary>
        /// The server never logs in, so it must not register with Steam: otherwise Steam would show the game as running
        /// and the owner could not start it to play. SteamManager creates itself lazily, so an inactive instance is
        /// put in its place: its Awake (which calls SteamAPI.Init) never runs.
        /// </summary>
        private static void BlockSteam()
        {
            try
            {
                var go = new GameObject("SteamManager (disabled by BGLan)");
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                SteamManager dummy = go.AddComponent<SteamManager>();
                typeof(SteamManager).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, dummy);
            }
            catch (Exception e)
            {
                Log.Warn("Could not keep Steam off: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.5f;

            if (parentPid > 0 && !ProcessAlive(parentPid))
            {
                Log.Info("Console window closed, stopping the server");
                Stop();
                return;
            }

            string path = Path.Combine(Config.GameDir, CommandFile);
            if (!File.Exists(path)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
                File.Delete(path);
            }
            catch
            {
                return;
            }
            foreach (string line in lines)
            {
                if (line.Trim().Length > 0) Run(line.Trim());
            }
        }

        private static void Run(string command)
        {
            Log.Info("> " + command);
            string[] parts = command.Split(new[] { ' ' }, 2);
            string arg = parts.Length > 1 ? parts[1].Trim() : "";
            switch (parts[0].ToLowerInvariant())
            {
                case "help":
                    Log.Info("Commands: list (lobbies and players), kick <player name>, ip (addresses to share), stop");
                    break;
                case "list":
                    foreach (string l in Mod.Server.Describe()) Log.Info(l);
                    break;
                case "kick":
                    Log.Info(Mod.Server.Kick(arg) ? "Kicked " + arg : "No player called '" + arg + "'");
                    break;
                case "ip":
                    LogAddresses();
                    break;
                case "stop":
                    Stop();
                    break;
                default:
                    Log.Info("Unknown command. Type help");
                    break;
            }
        }

        public static void LogAddresses()
        {
            Log.Info("Players on your network connect to: " + string.Join(", ", Mod.LocalAddresses().ToArray()));
            Log.Info("Players over the internet use your public IP (forward TCP ports " + Config.HttpPort + " and " +
                     Config.GamePort + " on your router) or your Radmin/Hamachi address.");
        }

        private static void Stop()
        {
            Log.Info("Server stopped");
            Mod.Shutdown();
            Application.Quit();
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                return !Process.GetProcessById(pid).HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
