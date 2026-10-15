using System;
using System.Collections.Generic;
using System.Linq;
using BGLan.Game;
using PlayFab.ClientModels;
using PlayFab.Json;

namespace BGLan.PlayFabEmu
{
    /// <summary>Answers the PlayFab Client API calls the game makes. Runs on the main thread.</summary>
    public class PlayFabEmulator
    {
        private readonly ProfileStore store;
        private readonly LanServer server;
        private List<string> allItemIds;

        public PlayFabEmulator(ProfileStore store, LanServer server)
        {
            this.store = store;
            this.server = server;
        }

        private class ApiError : Exception
        {
            public readonly string Code;
            public readonly int Number;
            public ApiError(string code, int number, string message) : base(message) { Code = code; Number = number; }
        }

        public string Handle(HttpRequestInfo req)
        {
            string path = req.Path;
            int testId = 0;
            // "/t2/Client/..." is used to give a second game instance on the same PC its own identity.
            if (path.StartsWith("/t"))
            {
                int slash = path.IndexOf('/', 1);
                if (slash > 0 && int.TryParse(path.Substring(2, slash - 2), out testId)) path = path.Substring(slash);
            }
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            string api = path.Substring(path.LastIndexOf('/') + 1);

            try
            {
                object result = Dispatch(api, req, testId);
                return "{\"code\":200,\"status\":\"OK\",\"data\":" + JsonWrapper.SerializeObject(result) + "}";
            }
            catch (ApiError e)
            {
                Log.Warn("PlayFab " + api + " -> " + e.Code + ": " + e.Message);
                return "{\"code\":400,\"status\":\"BadRequest\",\"error\":\"" + e.Code + "\",\"errorCode\":" + e.Number +
                       ",\"errorMessage\":" + JsonWrapper.SerializeObject(e.Message) + "}";
            }
        }

        private object Dispatch(string api, HttpRequestInfo req, int testId)
        {
            if (api != "GetCurrentGames" && api != "GetTime") Log.Info("PlayFab call: " + api);
            switch (api)
            {
                case "LoginWithSteam": return LoginWithSteam(Parse<LoginWithSteamRequest>(req), testId);
                case "GetTime": return new GetTimeResult { Time = DateTime.UtcNow };
                // The news tab reads this key without checking it exists; an old date means "no giveaway".
                case "GetTitleData": return new GetTitleDataResult { Data = new Dictionary<string, string> { { "CreditGiveawayNameDescDateAmount", "LAN|LAN|1/1/2000|0" } } };
                case "GetTitleNews": return new GetTitleNewsResult { News = new List<TitleNewsItem>() };
                case "GetFriendsList": return new GetFriendsListResult { Friends = new List<FriendInfo>() };
                case "GetCatalogItems": return new GetCatalogItemsResult { Catalog = new List<CatalogItem>() };
                case "GetStoreItems": return new GetStoreItemsResult { Store = new List<StoreItem>(), StoreId = "LAN" };
                case "ExecuteCloudScript": return new ExecuteCloudScriptResult { FunctionName = Parse<ExecuteCloudScriptRequest>(req).FunctionName, FunctionResult = false };
            }

            Profile me = store.FromSession(req.Header("X-Authorization"));
            if (me == null) throw new ApiError("NotAuthenticated", 1074, "Not logged in to the LAN server");

            switch (api)
            {
                case "UpdateUserTitleDisplayName": return UpdateDisplayName(me, Parse<UpdateUserTitleDisplayNameRequest>(req));
                case "GetPlayerCombinedInfo": return GetPlayerCombinedInfo(me, Parse<GetPlayerCombinedInfoRequest>(req));
                case "GetUserInventory": return new GetUserInventoryResult { Inventory = Inventory(), VirtualCurrency = Currency() };
                case "UpdateUserData": return UpdateUserData(me, Parse<UpdateUserDataRequest>(req));
                case "GetAccountInfo": return GetAccountInfo(Parse<GetAccountInfoRequest>(req));
                case "GetLeaderboard":
                case "GetFriendLeaderboard":
                case "GetLeaderboardAroundPlayer":
                    return Leaderboard(req);
                case "StartGame": return StartGame(me, Parse<StartGameRequest>(req), req.LocalAddress);
                case "Matchmake": return Matchmake(me, Parse<MatchmakeRequest>(req), req.LocalAddress);
                case "GetCurrentGames": return CurrentGames(Parse<CurrentGamesRequest>(req));
                case "BGLanUpdateStats": return UpdateStats(me, req);
            }
            throw new ApiError("APINotEnabledForGameClientAccess", 1083, api + " is not available on a LAN server");
        }

        private static T Parse<T>(HttpRequestInfo req) where T : class, new()
        {
            if (string.IsNullOrEmpty(req.Body)) return new T();
            return JsonWrapper.DeserializeObject<T>(req.Body) ?? new T();
        }

        // ---------------- accounts ----------------

        private LoginResult LoginWithSteam(LoginWithSteamRequest r, int testId)
        {
            string steamId = SteamIdFromTicket(r.SteamTicket);
            if (testId != 0) steamId += "_t" + testId;
            bool created;
            Profile p = store.GetOrCreateBySteamId(steamId, out created);
            Log.Info("Login: steam " + steamId + " -> " + p.PlayFabId + " (" + (p.DisplayName ?? "new") + ")");
            return new LoginResult
            {
                PlayFabId = p.PlayFabId,
                SessionTicket = store.StartSession(p),
                NewlyCreated = created,
                LastLoginTime = DateTime.UtcNow,
                SettingsForUser = new UserSettings(),
                InfoResultPayload = Payload(p, p, null, null, true),
            };
        }

        /// <summary>Steam auth session tickets carry the SteamID64 at byte 12 (after the 4 byte length and 8 byte GC token).</summary>
        public static string SteamIdFromTicket(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "unknown";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            var offsets = new List<int> { 12 };
            for (int i = 0; i + 8 <= bytes.Length; i++) offsets.Add(i);
            foreach (int offset in offsets)
            {
                if (offset + 8 > bytes.Length) continue;
                ulong id = BitConverter.ToUInt64(bytes, offset);
                if ((id & 0xFFFFFFFF00000000UL) == 0x0110000100000000UL) return id.ToString();
            }
            Log.Warn("Could not find a SteamID in the ticket, using its hash");
            return "ticket" + hex.Substring(0, Math.Min(32, hex.Length)).GetHashCode().ToString("X8");
        }

        private object UpdateDisplayName(Profile me, UpdateUserTitleDisplayNameRequest r)
        {
            string name = (r.DisplayName ?? "").Trim();
            if (name.Length < 3 || name.Length > 25) throw new ApiError("InvalidParams", 1000, "Invalid display name");
            Profile other = store.FindByName(name);
            if (other != null && other != me) throw new ApiError("NameNotAvailable", 1058, "Name not available");
            me.DisplayName = name;
            store.Save();
            return new UpdateUserTitleDisplayNameResult { DisplayName = name };
        }

        private object GetAccountInfo(GetAccountInfoRequest r)
        {
            Profile p = !string.IsNullOrEmpty(r.PlayFabId) ? store.Get(r.PlayFabId)
                : !string.IsNullOrEmpty(r.TitleDisplayName) ? store.FindByName(r.TitleDisplayName)
                : !string.IsNullOrEmpty(r.Username) ? store.FindByName(r.Username) : null;
            if (p == null) throw new ApiError("AccountNotFound", 1001, "Account not found");
            return new GetAccountInfoResult { AccountInfo = AccountInfo(p) };
        }

        private object GetPlayerCombinedInfo(Profile me, GetPlayerCombinedInfoRequest r)
        {
            Profile target = store.Get(r.PlayFabId) ?? me;
            GetPlayerCombinedInfoRequestParams prm = r.InfoRequestParameters ?? new GetPlayerCombinedInfoRequestParams();
            return new GetPlayerCombinedInfoResult
            {
                PlayFabId = target.PlayFabId,
                InfoResultPayload = Payload(target, me, prm.UserDataKeys, prm.PlayerStatisticNames, target == me),
            };
        }

        private GetPlayerCombinedInfoResultPayload Payload(Profile p, Profile viewer, List<string> dataKeys, List<string> statNames, bool includePrivate)
        {
            var data = new Dictionary<string, UserDataRecord>();
            foreach (KeyValuePair<string, string> kv in p.Data)
            {
                if (dataKeys == null || dataKeys.Contains(kv.Key)) data[kv.Key] = Record(kv.Value);
            }
            var readOnly = new Dictionary<string, UserDataRecord>();
            if (includePrivate)
            {
                foreach (KeyValuePair<string, string> kv in p.ReadOnlyData) readOnly[kv.Key] = Record(kv.Value);
            }
            var stats = new List<StatisticValue>();
            foreach (KeyValuePair<string, int> kv in p.Stats)
            {
                if (statNames == null || statNames.Contains(kv.Key)) stats.Add(new StatisticValue { StatisticName = kv.Key, Value = kv.Value });
            }
            return new GetPlayerCombinedInfoResultPayload
            {
                AccountInfo = AccountInfo(p),
                UserData = data,
                UserReadOnlyData = readOnly,
                PlayerStatistics = stats,
                UserInventory = includePrivate ? Inventory() : new List<ItemInstance>(),
                UserVirtualCurrency = includePrivate ? Currency() : new Dictionary<string, int>(),
                TitleData = new Dictionary<string, string>(),
            };
        }

        private static UserAccountInfo AccountInfo(Profile p)
        {
            DateTime created;
            if (!DateTime.TryParse(p.Created, out created)) created = DateTime.UtcNow;
            return new UserAccountInfo
            {
                PlayFabId = p.PlayFabId,
                Created = created,
                TitleInfo = new UserTitleInfo { DisplayName = p.DisplayName, Created = created, LastLogin = DateTime.UtcNow, isBanned = false },
            };
        }

        private static UserDataRecord Record(string value)
        {
            return new UserDataRecord { Value = value, LastUpdated = DateTime.UtcNow, Permission = UserDataPermission.Public };
        }

        private object UpdateUserData(Profile me, UpdateUserDataRequest r)
        {
            if (r.Data != null)
            {
                foreach (KeyValuePair<string, string> kv in r.Data)
                {
                    if (kv.Value == null) me.Data.Remove(kv.Key);
                    else me.Data[kv.Key] = kv.Value;
                }
            }
            if (r.KeysToRemove != null)
            {
                foreach (string k in r.KeysToRemove) me.Data.Remove(k);
            }
            store.Save();
            return new UpdateUserDataResult { DataVersion = 1 };
        }

        /// <summary>Custom call made by the mod after each game so XP and stats persist.</summary>
        private object UpdateStats(Profile me, HttpRequestInfo req)
        {
            var stats = JsonWrapper.DeserializeObject<Dictionary<string, int>>(req.Body);
            if (stats != null)
            {
                foreach (KeyValuePair<string, int> kv in stats) me.Stats[kv.Key] = kv.Value;
                store.Save();
            }
            return new UpdateUserDataResult { DataVersion = 1 };
        }

        private object Leaderboard(HttpRequestInfo req)
        {
            var r = Parse<GetLeaderboardRequest>(req);
            string stat = r.StatisticName ?? "XP";
            var entries = store.All.Where(p => p.DisplayName != null)
                .Select(p => new { p, v = p.Stats.ContainsKey(stat) ? p.Stats[stat] : 0 })
                .OrderByDescending(x => x.v)
                .Select((x, i) => new PlayerLeaderboardEntry { PlayFabId = x.p.PlayFabId, DisplayName = x.p.DisplayName, StatValue = x.v, Position = i })
                .ToList();
            return new GetLeaderboardResult { Leaderboard = entries };
        }

        // ---------------- items ----------------

        private static Dictionary<string, int> Currency()
        {
            return new Dictionary<string, int> { { "CR", 0 }, { "ST", 0 } };
        }

        private List<ItemInstance> Inventory()
        {
            var list = new List<ItemInstance>();
            if (!Config.UnlockAll) return list;
            if (allItemIds == null) allItemIds = CollectItemIds();
            foreach (string id in allItemIds)
            {
                list.Add(new ItemInstance { ItemId = id, ItemInstanceId = "lan-" + id, ItemClass = "lan", PurchaseDate = new DateTime(2018, 1, 1), DisplayName = id });
            }
            return list;
        }

        private static List<string> CollectItemIds()
        {
            var ids = new HashSet<string> { "Pro" };
            Action<Unlockable> add = u => { if (u != null && !string.IsNullOrEmpty(u.itemID)) ids.Add(u.itemID); };
            foreach (WeaponData w in WeaponLoader.Instance.weapons.Values) add(w.Unlockable);
            foreach (OutfitData o in OutfitLoader.Instance.outfits) add(o.unlockable);
            foreach (BadgeData b in BadgeLoader.Instance.badgeBackgrounds.Values) add(b.unlockable);
            foreach (BadgeData b in BadgeLoader.Instance.badgeEmblems.Values) add(b.unlockable);
            foreach (MapData m in GameManager.Instance.maps.mapData) add(m.unlockable);
            Log.Info("Unlock-all inventory: " + ids.Count + " items");
            return ids.ToList();
        }

        // ---------------- matchmaking ----------------

        private object StartGame(Profile me, StartGameRequest r, string address)
        {
            Lobby lobby = server.CreateLobby(r.GameMode, r.CustomCommandLineData);
            return new StartGameResult
            {
                LobbyID = lobby.Id,
                ServerHostname = address,
                ServerPort = Config.GamePort,
                Ticket = server.IssueTicket(lobby, me),
                Expires = DateTime.UtcNow.AddMinutes(5).ToString("o"),
                Password = "",
            };
        }

        private object Matchmake(Profile me, MatchmakeRequest r, string address)
        {
            Lobby lobby = null;
            if (r.GameMode == "Service")
            {
                lobby = server.ServiceLobby;
            }
            else if (!string.IsNullOrEmpty(r.LobbyId))
            {
                lobby = server.FindLobby(r.LobbyId);
                if (lobby != null && (lobby.IsFull || lobby.InGame)) lobby = null;
            }
            else
            {
                lobby = server.Lobbies.FirstOrDefault(l => l.GameMode == r.GameMode && !l.IsPrivate && !l.IsFull && !l.InGame && l.PlayerCount > 0);
                if (lobby == null && r.StartNewIfNoneFound == true) lobby = server.CreateLobby(r.GameMode, null);
            }
            if (lobby == null) return new MatchmakeResult { Status = MatchmakeStatus.GameNotFound };
            return new MatchmakeResult
            {
                LobbyID = lobby.Id,
                ServerHostname = address,
                ServerPort = Config.GamePort,
                Ticket = server.IssueTicket(lobby, me),
                Expires = DateTime.UtcNow.AddMinutes(5).ToString("o"),
                Status = MatchmakeStatus.Complete,
            };
        }

        private object CurrentGames(CurrentGamesRequest r)
        {
            var games = new List<GameInfo>();
            int players = 0;
            foreach (Lobby l in server.Lobbies)
            {
                players += l.PlayerCount;
                if (!string.IsNullOrEmpty(r.GameMode) && r.GameMode != l.GameMode) continue;
                games.Add(new GameInfo
                {
                    LobbyID = l.Id,
                    BuildVersion = GameManager.currentServerBuild,
                    GameMode = l.GameMode,
                    MaxPlayers = l.MaxPlayers,
                    PlayerUserIds = l.PlayFabIds,
                    RunTime = (uint)l.Age.TotalSeconds,
                    GameServerStateEnum = GameInstanceState.Open,
                    Tags = new Dictionary<string, string>
                    {
                        { "Ready", "True" },
                        { "InGame", l.InGame ? "True" : "False" },
                        { "Private", l.IsPrivate ? "True" : "False" },
                        { "Full", l.IsFull ? "True" : "False" },
                    },
                });
            }
            return new CurrentGamesResult { Games = games, GameCount = games.Count, PlayerCount = players };
        }
    }
}
