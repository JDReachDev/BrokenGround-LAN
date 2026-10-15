using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BGLan.Game;
using PlayFab.Json;
using Packet = TNet.Packet;

namespace BGLan
{
    /// <summary>Static methods the patcher calls from inside Assembly-CSharp.</summary>
    public static class Hooks
    {
        private static readonly HashSet<int> killsRequested = new HashSet<int>();

        /// <summary>Injected at the start of GameManager.Awake.</summary>
        public static void Init()
        {
            Mod.Init();
        }

        /// <summary>Injected at the start of Unlockable.IsUnlocked: returning true unlocks everything.</summary>
        public static bool ForceUnlocked()
        {
            return Config.UnlockAll;
        }

        /// <summary>
        /// Injected before every return of HealthManager.HandleTakeDamage.
        /// Online, soldiers only die when the server says so. The official server tracked health itself; here the game of the
        /// player whose turn it is (which simulates the shot first) reports deaths and the LAN server broadcasts them.
        /// </summary>
        public static void AfterHandleTakeDamage(HealthManager hm, int dealtBySoldierId, bool headshot, string weaponID)
        {
            try
            {
                if (GameManager.offlineMode || hm == null || hm.dead || hm.health > 0) return;
                PlayerManager pm = hm.GetComponent<PlayerManager>();
                if (pm == null) return;
                PlayerManager actor = TurnSetter.currentSoldier;
                if (actor == null || !actor.IsOwnedByMe) return;
                if (!killsRequested.Add(hm.GetInstanceID())) return;

                BinaryWriter w = TNet.TNManager.BeginSend(Lobby.KillRequestPacket);
                w.Write(pm.soldierID);
                w.Write(dealtBySoldierId);
                w.Write(string.IsNullOrEmpty(weaponID) ? -1 : WeaponLoader.Instance.ConvertIDToInt(weaponID));
                w.Write(headshot);
                w.Write(RPCQueue.frameOn);
                TNet.TNManager.EndSend();
                Log.Info("Reported death of soldier " + pm.soldierID);
            }
            catch (Exception e)
            {
                Log.Error("AfterHandleTakeDamage: " + e);
            }
        }

        /// <summary>
        /// Replaces TNetConnectionHandler.OnClientReceivedAmmo. The original decides when to stop reading from the absolute
        /// stream position, which is wrong when several packets arrive in one TCP read (common over VPNs): players then got
        /// no weapons. Packet: soldierID, XP, pair count, then (weapon index, amount) pairs.
        /// </summary>
        public static void OnAmmo(BinaryReader reader)
        {
            try
            {
                int soldierId = reader.ReadInt32();
                int xp = reader.ReadInt32();
                int pairs = reader.ReadInt32();
                var ammo = new List<int>();
                for (int i = 0; i < pairs * 2; i++) ammo.Add(reader.ReadInt32());
                if (xp > 0) GameManager.loggedInPlayerData.stats.XP = xp;
                Log.Info("Ammo for soldier " + soldierId + ": " + pairs + " weapons");
                GiveAmmoWhenSpawned(soldierId, ammo, 0f);
            }
            catch (Exception e)
            {
                Log.Error("OnAmmo: " + e);
            }
        }

        private static void GiveAmmoWhenSpawned(int soldierId, List<int> ammo, float waited)
        {
            PlayerManager pm = GameManager.Instance.GetPlayerManager(soldierId);
            if (pm != null)
            {
                pm.GiveAmmo(ammo);
                return;
            }
            if (waited > 30f)
            {
                Log.Warn("Soldier " + soldierId + " never appeared, ammo dropped");
                return;
            }
            // The soldier may not be spawned yet on a slow PC: try again shortly.
            LanBehaviour.RunLater(0.25f, () => GiveAmmoWhenSpawned(soldierId, ammo, waited + 0.25f));
        }

        /// <summary>Injected at the end of GameManager.EndGame: stores this player's stats on the host.</summary>
        public static void AfterEndGame()
        {
            try
            {
                PlayerData me = GameManager.loggedInPlayerData;
                if (me == null || GameManager.offlineMode) return;
                var stats = new Dictionary<string, int>();
                foreach (string name in PlayerStats.statNamesListFull)
                {
                    FieldInfo f = typeof(PlayerStats).GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(int)) stats[name] = (int)f.GetValue(me.stats);
                }
                string body = JsonWrapper.SerializeObject(stats);
                string url = Mod.PlayFabUrl + "/Client/BGLanUpdateStats";
                string ticket = GameManager.Instance.sessionTicket;
                new Thread(() => Post(url, ticket, body)) { IsBackground = true }.Start();
            }
            catch (Exception e)
            {
                Log.Error("AfterEndGame: " + e);
            }
        }

        private static void Post(string url, string ticket, string body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["X-Authorization"] = ticket;
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (WebResponse resp = req.GetResponse()) { }
                Log.Info("Stats saved on the LAN server");
            }
            catch (Exception e)
            {
                Log.Warn("Could not save stats: " + e.Message);
            }
        }
    }
}
