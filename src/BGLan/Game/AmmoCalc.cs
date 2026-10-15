using System;
using System.Collections.Generic;
using BGLan.PlayFabEmu;
using PlayFab.ClientModels;

namespace BGLan.Game
{
    /// <summary>Same LCG as the game's SeededRandom, but with its own state so the host's game is not disturbed.</summary>
    public class Lcg
    {
        private int seed;

        public Lcg(int seed) { this.seed = seed; }

        public int Range(int low, int high)
        {
            return (int)Math.Floor(Next() * (float)(1 + high - low)) + low;
        }

        private float Next()
        {
            seed = Mod(seed * 9301 + 49297, 233280);
            return seed / 233280f;
        }

        private static int Mod(int a, int n)
        {
            return a - (int)Math.Floor((double)a / n) * n;
        }
    }

    /// <summary>Server side ammo assignment, ported from Common.GetAmmo (online branch).</summary>
    public static class AmmoCalc
    {
        public static bool MapIsCavern(int mapIndex, int mapSeed)
        {
            MapData map = GameManager.Instance.GetMap(mapIndex);
            if (!map.generated) return map.cavern;
            return new Lcg(mapSeed).Range(0, 4) == 1;
        }

        private static bool MapHasMovingPlatform(int mapIndex)
        {
            MapData map = GameManager.Instance.GetMap(mapIndex);
            return map.prefab != null && map.prefab.GetComponentInChildren<MovingPlatform>() != null;
        }

        /// <summary>Returns ammo as (weapon id, amount) pairs.</summary>
        public static Dictionary<string, int> GetAmmo(int seed, PlayerData player, GameOptions options, bool cavern, bool firstAssignment)
        {
            var rng = new Lcg(seed);
            var candidates = new List<WeaponData>();
            var ammo = new Dictionary<string, int>();
            bool movingPlatform = MapHasMovingPlatform(options.mapIndex);
            bool roulette = options.rouletteMode;

            foreach (WeaponData w in WeaponLoader.Instance.weapons.Values)
            {
                if (w.HiddenWeapon || !w.Unlockable.IsUnlocked(player) || !Permitted(w, options, cavern, movingPlatform)) continue;
                string item = w.Unlockable.itemID;
                if (item.Contains("WeaponPack") && player.disabledPacks.Contains(item.Replace("WeaponPack", string.Empty))) continue;

                if (!roulette)
                {
                    if (w.ID == "RPG" || w.ID == "GRENADE") ammo.Add(w.ID, 999);
                    else if (w.ID == "TELEPORT") ammo.Add(w.ID, 1);
                    else candidates.Add(w);
                }
                else if (w.ID == "TELEPORT")
                {
                    if (firstAssignment) ammo.Add(w.ID, 1);
                }
                else
                {
                    candidates.Add(w);
                }
            }

            var chosen = new List<WeaponData>();
            int picks = roulette ? 1 : 15;
            for (int i = 0; i < picks && candidates.Count > 0; i++)
            {
                WeaponData w = candidates[rng.Range(0, candidates.Count - 1)];
                chosen.Add(w);
                candidates.Remove(w);
            }

            int draws = roulette ? 1 : 20;
            for (int i = 0; i < draws && chosen.Count > 0; i++)
            {
                WeaponData w = chosen[rng.Range(0, chosen.Count - 1)];
                if (ammo.ContainsKey(w.ID))
                {
                    ammo[w.ID]++;
                    if (ammo[w.ID] == 3) chosen.Remove(w);
                }
                else
                {
                    ammo.Add(w.ID, 1);
                    if (w.ID == "HEALTH_POTION") chosen.Remove(w);
                }
            }
            return ammo;
        }

        private static bool Permitted(WeaponData w, GameOptions options, bool cavern, bool movingPlatform)
        {
            return ((options.ceiling == WallOptions.None && !cavern) || !w.IsFromSkyWeapon()) && (!movingPlatform || !w.IsDirtWeapon());
        }

        /// <summary>Builds the PlayerData the unlock checks need, from the host's copy of the profile.</summary>
        public static PlayerData PlayerDataFor(Profile profile, string disabledPacks)
        {
            var pd = new PlayerData();
            var data = new Dictionary<string, UserDataRecord>();
            var stats = new List<StatisticValue>();
            if (profile != null)
            {
                foreach (KeyValuePair<string, string> kv in profile.Data) data[kv.Key] = new UserDataRecord { Value = kv.Value };
                foreach (KeyValuePair<string, int> kv in profile.Stats) stats.Add(new StatisticValue { StatisticName = kv.Key, Value = kv.Value });
            }
            pd.SetNewData(profile != null ? profile.DisplayName : "Player", data, stats);
            pd.AddShopItems(new List<ItemInstance>());
            pd.disabledPacks = new List<string>();
            if (!string.IsNullOrEmpty(disabledPacks)) pd.disabledPacks.AddRange(disabledPacks.Split(','));
            return pd;
        }
    }
}
