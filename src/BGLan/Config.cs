using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BGLan
{
    /// <summary>Settings stored in BGLan.cfg next to BrokenGround.exe.</summary>
    public static class Config
    {
        public static bool IsHost = true;
        public static string HostAddress = "127.0.0.1";
        public static int HttpPort = 27080;
        public static int GamePort = 27081;
        public static bool UnlockAll = true;
        // Only for testing two game instances on one PC: gives the second instance a different identity.
        public static int TestId = 0;

        private static string gameDir;

        // Application.dataPath may only be read on the main thread, so it is cached on first use (from Mod.Init).
        public static string GameDir
        {
            get
            {
                if (gameDir == null) gameDir = Path.GetDirectoryName(Application.dataPath);
                return gameDir;
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(GameDir, "BGLan.cfg"); }
        }

        public static void Load()
        {
            if (!File.Exists(FilePath))
            {
                Save();
                return;
            }
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "mode": IsHost = val.ToLowerInvariant() != "client"; break;
                    case "host": HostAddress = val; break;
                    case "http_port": int.TryParse(val, out HttpPort); break;
                    case "game_port": int.TryParse(val, out GamePort); break;
                    case "unlock_all": UnlockAll = val.ToLowerInvariant() != "false"; break;
                    case "test_id": int.TryParse(val, out TestId); break;
                }
            }
        }

        public static void Save()
        {
            var lines = new List<string>
            {
                "# Broken Ground LAN mod",
                "# mode = host  -> this PC hosts the server (friends connect to your IP)",
                "# mode = client -> connect to the PC at 'host'",
                "mode=" + (IsHost ? "host" : "client"),
                "host=" + HostAddress,
                "http_port=" + HttpPort,
                "game_port=" + GamePort,
                "unlock_all=" + (UnlockAll ? "true" : "false"),
            };
            if (TestId != 0) lines.Add("test_id=" + TestId);
            try { File.WriteAllLines(FilePath, lines.ToArray()); }
            catch (Exception e) { Log.Error("Cannot save config: " + e.Message); }
        }

        /// <summary>Address the local game uses to reach the PlayFab emulator.</summary>
        public static string ServerAddressForThisPC
        {
            get { return IsHost ? "127.0.0.1" : HostAddress; }
        }
    }

    public static class Log
    {
        private static readonly object Lock = new object();
        private static string path;

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg;
            lock (Lock)
            {
                try
                {
                    if (path == null)
                    {
                        path = Path.Combine(Config.GameDir, "BGLan.log");
                        File.WriteAllText(path, "");
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
