using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BGLan.PlayFabEmu;
using Packet = TNet.Packet;

namespace BGLan.Game
{
    public class LobbyPlayer
    {
        public Connection Conn;
        public Profile Profile;
        public int Id { get { return Conn.PlayerId; } }
        public int TeamChosen;
        public bool Ready;
        public string DisabledPacks = "";
        // In game
        public int Team;
        public bool Spawned;
        public bool AckedTurn;
    }

    public class Soldier
    {
        public int Id;
        public int Team;
        public bool Dead;
        public Profile Profile;
        public string DisabledPacks;
    }

    /// <summary>One game room. Implements what the official server did: lobby, game start, turns and relaying.</summary>
    public class Lobby
    {
        /// <summary>Custom packet sent by the mod when the acting player's simulation kills a soldier.</summary>
        public const byte KillRequestPacket = 240;

        private enum State { Lobby, Countdown, Loading, Playing, Ended }

        private enum TurnPhase { None, Acting, WaitingAcks }

        public readonly string Id;
        public readonly string GameMode;
        public bool IsServiceLobby;
        private readonly LanServer server;
        private readonly GameMode mode;
        private readonly DateTime created = DateTime.UtcNow;
        private readonly Random rng = new Random();

        private readonly List<LobbyPlayer> players = new List<LobbyPlayer>();
        private readonly GameOptions options = new GameOptions();
        private int hostId = -1;
        private State state = State.Lobby;
        private float stateTimer;
        private int countdown;
        private float lastTick;

        // Game
        private int mapSeed;
        private bool cavern;
        private readonly Dictionary<int, Soldier> soldiers = new Dictionary<int, Soldier>();
        private List<int> teamOrder;
        private readonly Dictionary<int, List<int>> teamSoldiers = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> teamTurnIndex = new Dictionary<int, int>();
        private int teamOrderIndex = -1;
        private int currentSoldier = -1;
        private TurnPhase phase = TurnPhase.None;
        private int turnSecondsLeft;
        private bool shotThisTurn;
        private float phaseTimer;
        private bool ammoSent;

        public Lobby(LanServer server, string id, string gameMode, string customData)
        {
            this.server = server;
            Id = id;
            GameMode = gameMode;
            mode = GameModes.Get(gameMode);
            ApplyCustomData(customData);
        }

        public int PlayerCount { get { return players.Count; } }
        public int MaxPlayers { get { return mode.maxPlayers; } }
        public bool IsFull { get { return players.Count >= mode.maxPlayers; } }
        public bool InGame { get { return state == State.Loading || state == State.Playing || state == State.Ended; } }
        public bool IsPrivate { get { return options.sendableGameOptions.privateGame; } }
        public TimeSpan Age { get { return DateTime.UtcNow - created; } }
        public List<string> PlayFabIds { get { return players.Select(p => p.Profile.PlayFabId).ToList(); } }

        public string Describe()
        {
            string names = string.Join(", ", players.Select(p => p.Profile.DisplayName ?? p.Profile.PlayFabId).ToArray());
            return Id + " " + mode.displayName + " (" + players.Count + "/" + MaxPlayers + ") " + state +
                   (IsPrivate ? " private" : "") + ": " + names;
        }

        /// <summary>CreateMultiplayer sends "map,health,time,walls,ceiling,gravity,wind,roulette,bigExplosions,private".</summary>
        private void ApplyCustomData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            string[] parts = data.Split(',');
            try
            {
                options.mapIndex = int.Parse(parts[0]);
                for (byte i = 1; i <= 5 && i < parts.Length; i++) options.UpdateOption(i, byte.Parse(parts[i]));
                for (byte i = 1; i <= 4 && i + 5 < parts.Length; i++) options.UpdateBool(i, parts[i + 5].ToLowerInvariant() == "true");
            }
            catch (Exception e)
            {
                Log.Warn("Bad lobby options '" + data + "': " + e.Message);
            }
        }

        // ---------------- helpers ----------------

        private void SendAll(PacketWriter p, LobbyPlayer except = null)
        {
            byte[] data = p.ToArray();
            foreach (LobbyPlayer lp in players)
            {
                if (lp != except) lp.Conn.Send(data);
            }
        }

        private LobbyPlayer Find(Connection c)
        {
            return players.FirstOrDefault(p => p.Conn == c);
        }

        private static PacketWriter P(Packet id) { return new PacketWriter(id); }

        // ---------------- join / leave ----------------

        public void Join(Connection c, Profile profile)
        {
            var me = new LobbyPlayer { Conn = c, Profile = profile };
            if (players.Count == 0 || hostId < 0) hostId = c.PlayerId;

            var join = new JoinGameData
            {
                mapID = options.mapIndex,
                gameMode = GameMode,
                players = players.Select(p => new PlayerJoinData
                {
                    playFabId = p.Profile.PlayFabId,
                    playerID = p.Id,
                    teamChosen = p.TeamChosen,
                    isReady = p.Ready,
                    dataCheckStatus = DataCheckStatus.Passed,
                }).ToList(),
            };
            players.Add(me);

            var jd = P(Packet.JoinData);
            TNet.Serialization.WriteObject(jd.W, join);
            TNet.Serialization.WriteObject(jd.W, options.sendableGameOptions);
            c.Send(jd);

            var host = P(Packet.Host);
            host.W.Write(hostId);
            c.Send(host);

            var np = P(Packet.NewPlayer);
            np.W.Write(c.PlayerId);
            np.W.Write(profile.PlayFabId);
            SendAll(np, me);

            Log.Info("Lobby " + Id + ": " + profile.DisplayName + " joined as player " + c.PlayerId + " (" + players.Count + "/" + MaxPlayers + ")");
            CancelCountdown();
        }

        public void Leave(Connection c)
        {
            LobbyPlayer lp = Find(c);
            if (lp == null) return;
            players.Remove(lp);
            Log.Info("Lobby " + Id + ": player " + c.PlayerId + " left");

            var left = P(Packet.PlayerLeft);
            left.W.Write(c.PlayerId);
            SendAll(left);

            if (players.Count == 0)
            {
                server.RemoveLobby(this);
                return;
            }
            if (hostId == c.PlayerId)
            {
                hostId = players[0].Id;
                var host = P(Packet.Host);
                host.W.Write(hostId);
                SendAll(host);
            }

            if (InGame)
            {
                Soldier s;
                if (soldiers.TryGetValue(c.PlayerId, out s) && !s.Dead) KillSoldier(s.Id, -1, -1, false, 0);
                if (state == State.Playing && phase == TurnPhase.WaitingAcks) CheckAcks();
                if (state == State.Playing && currentSoldier == c.PlayerId && phase == TurnPhase.Acting) EndTurn();
            }
            else
            {
                CancelCountdown();
            }
        }

        // ---------------- packets ----------------

        public void OnPacket(Connection c, Packet id, BinaryReader r, byte[] raw)
        {
            LobbyPlayer me = Find(c);
            if (me == null) return;

            if ((byte)id == KillRequestPacket)
            {
                int soldier = r.ReadInt32();
                int killer = r.ReadInt32();
                int weapon = r.ReadInt32();
                bool headshot = r.ReadBoolean();
                int frame = r.ReadInt32();
                KillSoldier(soldier, killer, weapon, headshot, frame);
                return;
            }

            switch (id)
            {
                // ----- lobby -----
                case Packet.Chat:
                {
                    var p = P(Packet.Chat);
                    p.W.Write(c.PlayerId);
                    p.W.Write(r.ReadString());
                    SendAll(p, me);
                    return;
                }
                case Packet.Ready:
                {
                    me.Ready = !me.Ready;
                    var p = P(Packet.Ready);
                    p.W.Write(c.PlayerId);
                    p.W.Write(me.Ready);
                    SendAll(p, me);
                    if (!me.Ready) CancelCountdown();
                    return;
                }
                case Packet.ChangeTeam:
                {
                    if (InGame) return;
                    IncrementTeam(me);
                    var p = P(Packet.ChangeTeam);
                    p.W.Write(c.PlayerId);
                    p.W.Write(me.TeamChosen);
                    SendAll(p);
                    return;
                }
                case Packet.MapID:
                {
                    if (c.PlayerId != hostId || InGame) return;
                    options.mapIndex = r.ReadInt32();
                    var p = P(Packet.MapID);
                    p.W.Write(options.mapIndex);
                    SendAll(p, me);
                    return;
                }
                case Packet.ChangeToggle:
                {
                    if (c.PlayerId != hostId || InGame) return;
                    byte opt = r.ReadByte();
                    options.UpdateBool(opt, !options.GetOptionValueBool(opt));
                    var p = P(Packet.ChangeToggle);
                    p.W.Write(opt);
                    p.W.Write(options.GetOptionValueBool(opt));
                    SendAll(p);
                    return;
                }
                case Packet.ChangeOption:
                {
                    if (c.PlayerId != hostId || InGame) return;
                    byte opt = r.ReadByte();
                    options.UpdateOption(opt);
                    var p = P(Packet.ChangeOption);
                    p.W.Write(opt);
                    p.W.Write(options.GetOptionValue(opt));
                    SendAll(p);
                    return;
                }
                case Packet.Emblem:
                case Packet.Badge:
                {
                    var p = P(id);
                    p.W.Write(r.ReadInt32());
                    p.W.Write(c.PlayerId);
                    SendAll(p, me);
                    return;
                }
                case Packet.Outfit:
                {
                    var p = P(Packet.Outfit);
                    p.W.Write(c.PlayerId);
                    for (int i = 0; i < 8; i++) p.W.Write(r.ReadInt32());
                    SendAll(p, me);
                    return;
                }
                case Packet.DisabledPacks:
                    me.DisabledPacks = r.ReadString() ?? "";
                    return;
                case Packet.Friend:
                {
                    int target = r.ReadInt32();
                    LobbyPlayer t = players.FirstOrDefault(p => p.Id == target);
                    if (t == null) return;
                    var p2 = P(Packet.Friend);
                    p2.W.Write(c.PlayerId);
                    t.Conn.Send(p2);
                    return;
                }
                case Packet.IntentionalDisconnect:
                    c.Close();
                    server.OnDisconnected(c);
                    return;
                case Packet.ChallengeCompleted:
                case Packet.Invite:
                    return;
            }

            if (state != State.Loading && state != State.Playing)
            {
                Log.Warn("Lobby " + Id + ": ignoring " + id + " outside a game");
                return;
            }

            switch (id)
            {
                case Packet.Spawn:
                {
                    me.Spawned = true;
                    var p = P(Packet.Spawn);
                    p.W.Write(c.PlayerId);
                    p.W.Write(r.ReadSingle());
                    p.W.Write(r.ReadSingle());
                    SendAll(p, me);
                    return;
                }
                case Packet.DamagePlayer:
                    RelayDamage(me, r);
                    return;
                case Packet.CollectItem:
                {
                    var p = P(Packet.CollectItem);
                    p.W.Write(r.ReadInt32());
                    r.ReadString();
                    p.W.Write(r.ReadInt32());
                    SendAll(p, me);
                    return;
                }
                case Packet.ShootRemote:
                {
                    r.ReadSingle(); // weapon power, not needed by receivers
                    float angle = r.ReadSingle();
                    int seed = r.ReadInt32();
                    int button = r.ReadInt32();
                    string weapon = r.ReadString();
                    int frame = r.ReadInt32();
                    var p = P(Packet.ShootRemote);
                    p.W.Write(angle);
                    p.W.Write(weapon);
                    p.W.Write(seed);
                    p.W.Write(button);
                    p.W.Write(frame);
                    SendAll(p, me);
                    shotThisTurn = true;
                    return;
                }
                case Packet.TurnFinished:
                    OnTurnFinished(me, r, raw);
                    return;
                case Packet.Shoot:
                case Packet.CreateProjectile:
                case Packet.Teleport:
                    shotThisTurn = true;
                    RelayRaw(me, raw);
                    return;
                case Packet.Walk:
                case Packet.StopWalk:
                case Packet.ChangeAngle:
                case Packet.ChangeWeapon:
                case Packet.TeleportAfterHit:
                case Packet.RemoveProjectile:
                case Packet.HitPlayer:
                case Packet.HitObject:
                case Packet.Explode:
                case Packet.ExplodeWithVel:
                case Packet.SetPosVel:
                case Packet.SetPosVelWithRot:
                case Packet.SetHomingTarget:
                case Packet.Steer:
                case Packet.Shatter:
                case Packet.ExplodeBarrel:
                case Packet.PressButton:
                case Packet.SetPosWithNetID:
                case Packet.SetVelWithNetID:
                case Packet.CutHole:
                    RelayRaw(me, raw);
                    return;
                default:
                    Log.Warn("Lobby " + Id + ": unhandled packet " + id + " from " + c.PlayerId);
                    return;
            }
        }

        /// <summary>Forwards a packet unchanged to everyone except the sender.</summary>
        private void RelayRaw(LobbyPlayer from, byte[] body)
        {
            byte[] framed = new byte[body.Length + 4];
            System.Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, framed, 0, 4);
            System.Buffer.BlockCopy(body, 0, framed, 4, body.Length);
            foreach (LobbyPlayer lp in players)
            {
                if (lp != from) lp.Conn.Send(framed);
            }
        }

        private void RelayDamage(LobbyPlayer from, BinaryReader r)
        {
            int target = r.ReadInt32();
            int damage = r.ReadInt32();
            int frame = r.ReadInt32();
            int weapon = r.ReadInt32();
            r.ReadInt32(); // projectile index

            // The shooter's game sends damage for the hit soldier; friendly fire is redirected to the shooter.
            Soldier t, d;
            if (damage > 0 && currentSoldier >= 0 && target != currentSoldier &&
                soldiers.TryGetValue(target, out t) && soldiers.TryGetValue(currentSoldier, out d) && t.Team == d.Team)
            {
                target = currentSoldier;
            }

            var p = P(Packet.DamagePlayer);
            p.W.Write(target);
            p.W.Write(damage);
            p.W.Write(IsElectric(weapon));
            p.W.Write(frame);
            SendAll(p, from);
        }

        private static bool IsElectric(int weaponIndex)
        {
            try
            {
                if (weaponIndex < 0) return false;
                WeaponData w = WeaponLoader.Instance.GetWeaponData(WeaponLoader.Instance.ConvertIDToString(weaponIndex));
                return w != null && w.Projectiles != null && w.Projectiles.Any(pd => pd.ElectricDamage);
            }
            catch
            {
                return false;
            }
        }

        private void KillSoldier(int soldierId, int killer, int weapon, bool headshot, int frame)
        {
            Soldier s;
            if (!soldiers.TryGetValue(soldierId, out s) || s.Dead) return;
            s.Dead = true;
            Log.Info("Lobby " + Id + ": soldier " + soldierId + " killed by " + killer);
            var p = P(Packet.KillPlayer);
            p.W.Write(soldierId);
            p.W.Write(killer);
            p.W.Write(weapon);
            p.W.Write(headshot);
            p.W.Write(frame);
            SendAll(p);
        }

        private void IncrementTeam(LobbyPlayer lp)
        {
            for (int guard = 0; guard <= mode.totalTeams + 1; guard++)
            {
                lp.TeamChosen++;
                if (lp.TeamChosen > mode.totalTeams) lp.TeamChosen = 0;
                if (lp.TeamChosen == 0) return;
                int count = players.Count(p => p.TeamChosen == lp.TeamChosen);
                if (count <= mode.playersPerTeam) return;
            }
        }

        // ---------------- countdown & game start ----------------

        private void CancelCountdown()
        {
            if (state != State.Countdown) return;
            state = State.Lobby;
            var t = P(Packet.Time);
            t.W.Write(-1);
            SendAll(t);
        }

        public void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float dt = now - lastTick;
            if (lastTick == 0f) dt = 0f;
            lastTick = now;
            stateTimer += dt;
            phaseTimer += dt;

            switch (state)
            {
                case State.Lobby:
                    if (players.Count >= 2 && players.All(p => p.Ready))
                    {
                        state = State.Countdown;
                        countdown = 6;
                        stateTimer = 1f;
                    }
                    break;
                case State.Countdown:
                    if (stateTimer >= 1f)
                    {
                        stateTimer = 0f;
                        countdown--;
                        var t = P(Packet.Time);
                        t.W.Write(countdown);
                        SendAll(t);
                        if (countdown <= 0) StartGame();
                    }
                    break;
                case State.Loading:
                    if (!ammoSent && (players.All(p => p.Spawned) ? stateTimer >= 2f : stateTimer >= 40f))
                    {
                        SendAmmo();
                        ammoSent = true;
                        stateTimer = 0f;
                    }
                    else if (ammoSent && stateTimer >= 1.5f)
                    {
                        state = State.Playing;
                        NextTurn();
                    }
                    break;
                case State.Playing:
                    TickTurn();
                    break;
                case State.Ended:
                    if (stateTimer >= 10f)
                    {
                        state = State.Lobby;
                        foreach (LobbyPlayer p in players) p.Ready = false;
                    }
                    break;
            }
        }

        private void StartGame()
        {
            if (players.Count < 2)
            {
                state = State.Lobby;
                return;
            }
            Log.Info("Lobby " + Id + ": starting game with " + players.Count + " players on map " + options.mapIndex);

            AssignTeams();
            mapSeed = rng.Next(0, 100000);
            cavern = AmmoCalc.MapIsCavern(options.mapIndex, mapSeed);

            var teams = P(Packet.AssignTeams);
            teams.W.Write(string.Join("|", players.Select(p => p.Id + "," + p.Team).ToArray()));
            SendAll(teams);

            var seed = P(Packet.MapSeed);
            seed.W.Write(mapSeed);
            SendAll(seed);

            // Spawn segment: each team owns playersPerTeam consecutive segments.
            foreach (IGrouping<int, LobbyPlayer> team in players.GroupBy(p => p.Team))
            {
                int k = 0;
                foreach (LobbyPlayer lp in team)
                {
                    var seg = P(Packet.SegmentIndex);
                    seg.W.Write((team.Key - 1) * mode.playersPerTeam + k++);
                    lp.Conn.Send(seg);
                }
            }

            soldiers.Clear();
            teamSoldiers.Clear();
            teamTurnIndex.Clear();
            foreach (LobbyPlayer lp in players)
            {
                lp.Spawned = false;
                lp.AckedTurn = false;
                soldiers[lp.Id] = new Soldier { Id = lp.Id, Team = lp.Team, Profile = lp.Profile, DisabledPacks = lp.DisabledPacks };
            }
            teamOrder = soldiers.Values.Select(s => s.Team).Distinct().OrderBy(x => rng.Next()).ToList();
            foreach (int team in teamOrder)
            {
                teamSoldiers[team] = soldiers.Values.Where(s => s.Team == team).Select(s => s.Id).OrderBy(x => rng.Next()).ToList();
                teamTurnIndex[team] = -1;
            }
            teamOrderIndex = -1;
            currentSoldier = -1;
            phase = TurnPhase.None;
            ammoSent = false;

            var start = P(Packet.StartGame);
            start.W.Write(options.mapIndex);
            SendAll(start);

            state = State.Loading;
            stateTimer = 0f;
        }

        private void AssignTeams()
        {
            var counts = new int[mode.totalTeams + 1];
            foreach (LobbyPlayer lp in players) lp.Team = 0;
            foreach (LobbyPlayer lp in players.Where(p => p.TeamChosen > 0 && p.TeamChosen <= mode.totalTeams))
            {
                if (counts[lp.TeamChosen] < mode.playersPerTeam)
                {
                    lp.Team = lp.TeamChosen;
                    counts[lp.Team]++;
                }
            }
            foreach (LobbyPlayer lp in players.Where(p => p.Team == 0).OrderBy(x => rng.Next()))
            {
                int best = 1;
                for (int t = 2; t <= mode.totalTeams; t++)
                {
                    if (counts[t] < counts[best]) best = t;
                }
                lp.Team = best;
                counts[best]++;
            }
        }

        private void SendAmmo()
        {
            foreach (Soldier s in soldiers.Values)
            {
                Dictionary<string, int> ammo = AmmoCalc.GetAmmo(rng.Next(0, 100000), AmmoCalc.PlayerDataFor(s.Profile, s.DisabledPacks), options, cavern, true);
                var p = P(Packet.Ammo);
                p.W.Write(s.Id);
                p.W.Write(0);
                p.W.Write(ammo.Count);
                foreach (KeyValuePair<string, int> kv in ammo)
                {
                    p.W.Write(WeaponLoader.Instance.ConvertIDToInt(kv.Key));
                    p.W.Write(kv.Value);
                }
                SendAll(p);
            }
        }

        // ---------------- turns ----------------

        private void NextTurn()
        {
            if (CheckGameOver()) return;

            // Next team (in the fixed random order) that still has a living soldier, then its next living soldier.
            for (int i = 0; i < teamOrder.Count; i++)
            {
                teamOrderIndex = (teamOrderIndex + 1) % teamOrder.Count;
                int team = teamOrder[teamOrderIndex];
                List<int> list = teamSoldiers[team];
                for (int j = 0; j < list.Count; j++)
                {
                    teamTurnIndex[team] = (teamTurnIndex[team] + 1) % list.Count;
                    int candidate = list[teamTurnIndex[team]];
                    if (!soldiers[candidate].Dead)
                    {
                        BeginTurn(candidate);
                        return;
                    }
                }
            }
        }

        private void BeginTurn(int soldierId)
        {
            currentSoldier = soldierId;
            shotThisTurn = false;
            foreach (LobbyPlayer lp in players) lp.AckedTurn = false;

            if (options.wind)
            {
                var w = P(Packet.Wind);
                w.W.Write(rng.Next(-100, 101));
                SendAll(w);
            }
            if (options.gravity == GravityOptions.Random)
            {
                var g = P(Packet.Gravity);
                g.W.Write(-14 + rng.Next(-1, 2) * 6);
                SendAll(g);
            }
            if (options.rouletteMode)
            {
                Soldier s = soldiers[soldierId];
                Dictionary<string, int> ammo = AmmoCalc.GetAmmo(rng.Next(0, 100000), AmmoCalc.PlayerDataFor(s.Profile, s.DisabledPacks), options, cavern, false);
                string weapon = ammo.Keys.FirstOrDefault(k => k != "TELEPORT");
                if (weapon != null)
                {
                    var sa = P(Packet.SingleAmmo);
                    sa.W.Write(WeaponLoader.Instance.ConvertIDToInt(weapon));
                    sa.W.Write(soldierId);
                    SendAll(sa);
                }
            }

            var st = P(Packet.SetTurn);
            st.W.Write(soldierId);
            SendAll(st);

            phase = TurnPhase.Acting;
            turnSecondsLeft = options.time;
            phaseTimer = 0f;
            SendTurnTime();
            Log.Info("Lobby " + Id + ": turn of soldier " + soldierId);
        }

        private void SendTurnTime()
        {
            var t = P(Packet.TurnTime);
            t.W.Write(turnSecondsLeft);
            SendAll(t);
        }

        private void TickTurn()
        {
            if (phase == TurnPhase.Acting)
            {
                // The countdown stops once the player fires; their game then reports TurnFinished.
                if (!shotThisTurn && turnSecondsLeft > 0 && phaseTimer >= 1f)
                {
                    phaseTimer = 0f;
                    turnSecondsLeft--;
                    SendTurnTime();
                }
                // When time runs out without a shot the acting game freezes the soldier and never reports TurnFinished
                // (TurnSetter sets turnEnded), so the server moves on by itself.
                if (!shotThisTurn && turnSecondsLeft == 0 && phaseTimer >= 3f)
                {
                    Log.Info("Lobby " + Id + ": time is up for soldier " + currentSoldier);
                    EndTurn();
                    return;
                }
                if (phaseTimer >= 90f)
                {
                    Log.Warn("Lobby " + Id + ": soldier " + currentSoldier + " never finished the turn, skipping");
                    EndTurn();
                }
            }
            else if (phase == TurnPhase.WaitingAcks && phaseTimer >= 15f)
            {
                Log.Warn("Lobby " + Id + ": not every player confirmed the end of turn, continuing");
                EndTurn();
            }
        }

        private void OnTurnFinished(LobbyPlayer from, BinaryReader r, byte[] raw)
        {
            bool fromActor = from.Id == currentSoldier && raw.Length >= 5;
            if (fromActor && phase == TurnPhase.Acting)
            {
                int frame = r.ReadInt32();
                var p = P(Packet.TurnFinished);
                p.W.Write(frame);
                SendAll(p, from);
                from.AckedTurn = true;
                phase = TurnPhase.WaitingAcks;
                phaseTimer = 0f;
                CheckAcks();
            }
            else if (phase == TurnPhase.WaitingAcks)
            {
                from.AckedTurn = true;
                CheckAcks();
            }
        }

        private void CheckAcks()
        {
            if (players.All(p => p.AckedTurn)) EndTurn();
        }

        private void EndTurn()
        {
            phase = TurnPhase.None;
            NextTurn();
        }

        private bool CheckGameOver()
        {
            List<int> alive = soldiers.Values.Where(s => !s.Dead && players.Any(p => p.Id == s.Id)).Select(s => s.Team).Distinct().ToList();
            if (alive.Count > 1) return false;
            int winner = alive.Count == 1 ? alive[0] : -1;
            Log.Info("Lobby " + Id + ": game over, winning team " + winner);
            var p = P(Packet.EndGame);
            p.W.Write(winner);
            SendAll(p);
            state = State.Ended;
            stateTimer = 0f;
            phase = TurnPhase.None;
            return true;
        }
    }
}
