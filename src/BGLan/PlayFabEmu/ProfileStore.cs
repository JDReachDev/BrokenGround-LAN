using System;
using System.Collections.Generic;
using System.IO;
using PlayFab.Json;

namespace BGLan.PlayFabEmu
{
    public class Profile
    {
        public string PlayFabId;
        public string SteamId;
        public string DisplayName;
        public Dictionary<string, string> Data = new Dictionary<string, string>();
        public Dictionary<string, string> ReadOnlyData = new Dictionary<string, string>();
        public Dictionary<string, int> Stats = new Dictionary<string, int>();
        public string Created;
    }

    public class ProfileDb
    {
        public List<Profile> Profiles = new List<Profile>();
    }

    /// <summary>Player accounts kept by the host in BGLan_profiles.json. Only touched on the main thread.</summary>
    public class ProfileStore
    {
        private readonly string path;
        private ProfileDb db = new ProfileDb();
        private readonly Dictionary<string, Profile> bySession = new Dictionary<string, Profile>();

        public ProfileStore(string path)
        {
            this.path = path;
            if (File.Exists(path))
            {
                try
                {
                    db = JsonWrapper.DeserializeObject<ProfileDb>(File.ReadAllText(path)) ?? new ProfileDb();
                    foreach (Profile p in db.Profiles)
                    {
                        if (p.Data == null) p.Data = new Dictionary<string, string>();
                        if (p.ReadOnlyData == null) p.ReadOnlyData = new Dictionary<string, string>();
                        if (p.Stats == null) p.Stats = new Dictionary<string, int>();
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Cannot read profiles, starting empty: " + e.Message);
                    File.Copy(path, path + ".broken", true);
                    db = new ProfileDb();
                }
            }
            Log.Info("Profiles loaded: " + db.Profiles.Count);
        }

        public IEnumerable<Profile> All
        {
            get { return db.Profiles; }
        }

        public Profile GetOrCreateBySteamId(string steamId, out bool created)
        {
            created = false;
            foreach (Profile p in db.Profiles)
            {
                if (p.SteamId == steamId) return p;
            }
            created = true;
            var profile = new Profile
            {
                SteamId = steamId,
                PlayFabId = MakePlayFabId(steamId),
                Created = DateTime.UtcNow.ToString("o"),
            };
            db.Profiles.Add(profile);
            Save();
            return profile;
        }

        private static string MakePlayFabId(string steamId)
        {
            ulong id;
            if (ulong.TryParse(steamId, out id)) return id.ToString("X16");
            return Math.Abs(steamId.GetHashCode()).ToString("X16");
        }

        public Profile Get(string playFabId)
        {
            if (playFabId == null) return null;
            foreach (Profile p in db.Profiles)
            {
                if (p.PlayFabId == playFabId) return p;
            }
            return null;
        }

        public Profile FindByName(string name)
        {
            foreach (Profile p in db.Profiles)
            {
                if (p.DisplayName != null && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public string StartSession(Profile p)
        {
            string ticket = p.PlayFabId + "-" + Guid.NewGuid().ToString("N");
            bySession[ticket] = p;
            return ticket;
        }

        public Profile FromSession(string ticket)
        {
            Profile p;
            if (ticket != null && bySession.TryGetValue(ticket, out p)) return p;
            // Session tickets survive host restarts: they start with the PlayFabId.
            if (ticket != null && ticket.Length > 16) return Get(ticket.Substring(0, 16));
            return null;
        }

        public void Save()
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonWrapper.SerializeObject(db));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Error("Cannot save profiles: " + e.Message);
            }
        }
    }
}
