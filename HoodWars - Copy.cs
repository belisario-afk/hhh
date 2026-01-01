using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("HoodWars", "Gemini", "7.8.1")]
    [Description("A robust, comprehensive gang-based territory and identity system for a unique vanilla-feel Rust experience.")]
    public class HoodWars : RustPlugin
    {
        #region References & Fields

        private static HoodWars _instance;
        private StoredData _storedData;
        private DynamicConfigFile _data;

        private Dictionary<ulong, float> _spottedPlayers = new Dictionary<ulong, float>();
        private Dictionary<NetworkableId, MapMarkerGenericRadius> _activeMarkers = new Dictionary<NetworkableId, MapMarkerGenericRadius>();
        
        // Timer for periodic updates
        private Timer _identityTimer;

        private const string PrefabMarker = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string PermAdmin = "hoodwars.admin";
        private const string PermUse = "hoodwars.use";

        #endregion

        #region Configuration

        private ConfigData _config;

        private class ConfigData
        {
            [JsonProperty("General Settings")]
            public GeneralSettings General { get; set; }

            [JsonProperty("Neighborhood Definitions")]
            public List<NeighborhoodConfig> Neighborhoods { get; set; }

            [JsonProperty("Marker Settings")]
            public MarkerSettings Markers { get; set; }

            [JsonProperty("Chat Settings")]
            public ChatSettings Chat { get; set; }

            public class GeneralSettings
            {
                [JsonProperty("Identity Reveal Duration (Seconds)")]
                public float RevealDuration { get; set; } = 300f;

                [JsonProperty("Proximity Reveal Distance (Meters)")]
                public float ProximityDistance { get; set; } = 5f;

                [JsonProperty("Kill Reveal (True/False)")]
                public bool KillReveal { get; set; } = true;

                [JsonProperty("Scrap Bounty for Rat TCs")]
                public int BountyAmount { get; set; } = 150;

                [JsonProperty("Enable Reputation System")]
                public bool UseReputation { get; set; } = true;
            }

            public class NeighborhoodConfig
            {
                public string Name { get; set; }
                public NeighborhoodType Type { get; set; }
                public float MinX { get; set; }
                public float MaxX { get; set; }
                public float MinZ { get; set; }
                public float MaxZ { get; set; }
                public string HexColor { get; set; }
            }

            public class MarkerSettings
            {
                [JsonProperty("Marker Opacity (0.0 to 1.0)")]
                public float InfiltratorAlpha { get; set; } = 0.3f;

                [JsonProperty("Marker Size (Radius)")]
                public float InfiltratorRadius { get; set; } = 1.2f;

                [JsonProperty("Position Jitter (Randomize Offset Radius)")]
                public float RandomOffsetRadius { get; set; } = 25f;

                public string Color1 { get; set; } = "#FF0000";
                public string Color2 { get; set; } = "#000000";
            }

            public class ChatSettings
            {
                public string AnonymousColor { get; set; } = "#55aaee";
                public string RevealedColor { get; set; } = "#ff4444";
                public string NeighborhoodColor { get; set; } = "#ffffff";
                public string Prefix { get; set; } = "STREETS";
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception();
            }
            catch
            {
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            // For a 2100 map, coordinates range from -1050 to 1050
            _config = new ConfigData
            {
                General = new ConfigData.GeneralSettings(),
                Markers = new ConfigData.MarkerSettings(),
                Chat = new ConfigData.ChatSettings(),
                Neighborhoods = new List<ConfigData.NeighborhoodConfig>
                {
                    new ConfigData.NeighborhoodConfig { Name = "Westside Pirus", Type = NeighborhoodType.West, MinX = -1050, MaxX = 0, MinZ = 0, MaxZ = 1050, HexColor = "#ff4444" },
                    new ConfigData.NeighborhoodConfig { Name = "Northside Vagos", Type = NeighborhoodType.North, MinX = 0, MaxX = 1050, MinZ = 0, MaxZ = 1050, HexColor = "#ccff33" },
                    new ConfigData.NeighborhoodConfig { Name = "Southside Sureños", Type = NeighborhoodType.South, MinX = -1050, MaxX = 0, MinZ = -1050, MaxZ = 0, HexColor = "#3366ff" },
                    new ConfigData.NeighborhoodConfig { Name = "Eastside Disciples", Type = NeighborhoodType.East, MinX = 0, MaxX = 1050, MinZ = -1050, MaxZ = 0, HexColor = "#444444" }
                }
            };
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Data Storage

        private class StoredData
        {
            public Dictionary<ulong, PlayerGangInfo> Players = new Dictionary<ulong, PlayerGangInfo>();
        }

        private class PlayerGangInfo
        {
            public NeighborhoodType HomeHood = NeighborhoodType.Neutral;
            public bool IsInfiltrator = false;
            public string CustomSet = "";
            public int Reputation = 0;
            public DateTime JoinDate = DateTime.Now;
        }

        public enum NeighborhoodType { West, North, South, East, Neutral }

        private void SaveData() => _data.WriteObject(_storedData);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Welcome_Neutral"] = "Welcome to the Streets. Place your first Tool Cupboard to claim your loyalty.",
                ["Welcome_Loyal"] = "Welcome home, soldier of the <color={0}>{1}</color>.",
                ["BloodIn"] = "<color=#55ff55>BLOOD IN:</color> You are now officially repping {0} for life.",
                ["SnitchAlert"] = "<color=#ff4444>SNITCH ALERT:</color> You authorized in rival territory. You are now a marked RAT.",
                ["StreetJustice"] = "<color=#55ff55>STREET JUSTICE:</color> You cleared a Rat house. Earned {0} Scrap.",
                ["GangWarfare"] = "<color=#ff4444>[GANG WARFARE]</color> {0} took out a rival in {1}!",
                ["WhoAmI_Header"] = "--- {0} ---",
                ["WhoAmI_Loyalty"] = "Loyalty: {0}",
                ["WhoAmI_Zone"] = "Current Zone: {0}",
                ["WhoAmI_Rep"] = "Reputation: {0}",
                ["WhoAmI_Status"] = "Status: {0}",
                ["InfiltratorNews"] = "<color=#ff4444>[STREET NEWS]</color> A rival presence was detected in {0}! Check your maps for the search area."
            }, this);
        }

        private string GetMsg(string key, string userId = null, params object[] args) => string.Format(lang.GetMessage(key, this, userId), args);

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            _data = Interface.Oxide.DataFileSystem.GetFile("HoodWars_CoreData");
            _storedData = _data.ReadObject<StoredData>() ?? new StoredData();

            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);
        }

        private void OnServerInitialized()
        {
            _identityTimer = timer.Every(10f, () =>
            {
                CheckProximity();
                UpdateAllIdentities();
                RefreshInfiltratorMarkers();
            });
        }

        private void Unload()
        {
            _identityTimer?.Destroy();
            ClearAllMarkers();
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            UpdateIdentity(player);
            
            var info = GetPlayerData(player.userID);
            if (info.HomeHood == NeighborhoodType.Neutral)
                SendReply(player, GetMsg("Welcome_Neutral", player.UserIDString));
            else
            {
                var hood = GetNeighborhoodConfig(info.HomeHood);
                SendReply(player, GetMsg("Welcome_Loyal", player.UserIDString, hood.HexColor, hood.Name));
            }

            // Sync all active markers to the joining player
            foreach (var marker in _activeMarkers.Values)
            {
                if (marker != null) marker.SendUpdate();
            }
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var cupboard = go.GetComponent<BuildingPrivlidge>();
            if (cupboard == null) return;

            BasePlayer player = plan.GetOwnerPlayer();
            if (player == null) return;

            CheckTCPlacement(cupboard, player);
        }

        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (player == null || privilege == null) return;
            CheckTCPlacement(privilege, player);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity is BuildingPrivlidge tc)
            {
                RemoveMarker(tc.net.ID);

                // Bounty logic
                if (tc.lastAttacker is BasePlayer killer)
                {
                    var killerData = GetPlayerData(killer.userID);
                    var victimData = GetPlayerData(tc.OwnerID);

                    // Only reward if it was enemy territory
                    if (killerData.HomeHood != GetNeighborhoodAt(tc.transform.position).Type)
                    {
                        killer.GiveItem(ItemManager.CreateByItemID(-932201673, _config.General.BountyAmount));
                        SendReply(killer, GetMsg("StreetJustice", killer.UserIDString, _config.General.BountyAmount));
                        killerData.Reputation += 15;
                    }
                }
            }
        }

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (!_config.General.KillReveal || info == null) return;
            var killer = info.InitiatorPlayer;
            if (killer != null && killer != victim && !killer.IsNpc)
            {
                RevealPlayer(killer);
                var hood = GetNeighborhoodAt(killer.transform.position);
                PrintToChat(GetMsg("GangWarfare", null, killer.IPlayer.Name, hood.Name));
                
                // Rep adjustment
                var kData = GetPlayerData(killer.userID);
                var vData = GetPlayerData(victim.userID);
                if (kData.HomeHood != vData.HomeHood && vData.HomeHood != NeighborhoodType.Neutral)
                    kData.Reputation += 5;
            }
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (channel != ConVar.Chat.ChatChannel.Global) return null;

            var info = GetPlayerData(player.userID);
            bool revealed = IsSpotted(player) || info.IsInfiltrator;
            
            var hoodConfig = GetNeighborhoodConfig(info.HomeHood);
            string hoodTag = hoodConfig?.Name ?? "Drifter";
            string subTag = !string.IsNullOrEmpty(info.CustomSet) ? $"[{info.CustomSet}] " : "";
            
            string nameColor = revealed ? _config.Chat.RevealedColor : _config.Chat.AnonymousColor;
            string prefix = info.IsInfiltrator ? "[RAT] " : (revealed ? "[REVEALED] " : "");
            string displayName = revealed ? player.IPlayer.Name : "ANONYMOUS";

            string formatted = $"<color={nameColor}>{prefix}{subTag}{displayName}</color> <color={_config.Chat.NeighborhoodColor}>({hoodTag})</color>: {message}";
            ConsoleNetwork.BroadcastToAllClients("chat.add", 0, player.userID, formatted);
            
            return true;
        }

        // Networking hook to ensure markers are visible [cite: TcMapMarkers]
        private object CanNetworkTo(MapMarkerGenericRadius marker, BasePlayer player)
        {
            if (marker == null || player == null) return null;
            if (_activeMarkers.Values.Contains(marker)) return true;
            return null;
        }

        #endregion

        #region Core Mechanics

        private void CheckTCPlacement(BuildingPrivlidge tc, BasePlayer player)
        {
            var info = GetPlayerData(player.userID);
            var zone = GetNeighborhoodAt(tc.transform.position);

            if (info.HomeHood == NeighborhoodType.Neutral)
            {
                if (zone.Type != NeighborhoodType.Neutral)
                {
                    info.HomeHood = zone.Type;
                    SendReply(player, GetMsg("BloodIn", player.UserIDString, zone.Name));
                }
            }
            else if (info.HomeHood != zone.Type && zone.Type != NeighborhoodType.Neutral)
            {
                if (!info.IsInfiltrator)
                {
                    info.IsInfiltrator = true;
                    SendReply(player, GetMsg("SnitchAlert", player.UserIDString));
                }
                // Trigger reveal immediately when authorizing on enemy TC
                RevealPlayer(player);
            }

            SaveData();
            UpdateIdentity(player);
        }

        private void UpdateIdentity(BasePlayer player)
        {
            if (player == null || player.net?.connection == null) return;

            var info = GetPlayerData(player.userID);
            bool revealed = IsSpotted(player) || info.IsInfiltrator;
            
            string hoodName = GetNeighborhoodConfig(info.HomeHood)?.Name ?? "Drifter";
            string subTag = !string.IsNullOrEmpty(info.CustomSet) ? $"{info.CustomSet} | " : "";
            
            string finalName;
            if (revealed)
                finalName = info.IsInfiltrator ? $"{player.IPlayer.Name} (RAT)" : $"{player.IPlayer.Name} ({hoodName})";
            else
                finalName = $"{subTag}{hoodName}";

            if (player.displayName != finalName)
            {
                player.displayName = finalName;
                player.net.connection.username = finalName;
                player.SendNetworkUpdate();
            }
        }

        private void CheckProximity()
        {
            var players = BasePlayer.activePlayerList;
            int count = players.Count;
            if (count < 2) return;

            for (int i = 0; i < count; i++)
            {
                var p = players[i];
                if (p == null) continue;

                for (int j = i + 1; j < count; j++)
                {
                    var t = players[j];
                    if (t == null) continue;

                    if (Vector3.Distance(p.transform.position, t.transform.position) < _config.General.ProximityDistance)
                    {
                        RevealPlayer(p);
                        RevealPlayer(t);
                    }
                }
            }
        }

        private void RevealPlayer(BasePlayer player)
        {
            if (player == null) return;
            _spottedPlayers[player.userID] = Time.realtimeSinceStartup + _config.General.RevealDuration;
            UpdateIdentity(player);
            
            // Immediately refresh markers for the revealed player if they are an infiltrator
            var info = GetPlayerData(player.userID);
            if (info.IsInfiltrator) RefreshInfiltratorMarkers();
        }

        private bool IsSpotted(BasePlayer player)
        {
            if (player == null) return false;
            if (_spottedPlayers.TryGetValue(player.userID, out float expiry))
            {
                if (Time.realtimeSinceStartup < expiry) return true;
                _spottedPlayers.Remove(player.userID);
            }
            return false;
        }

        private void CreateMapMarker(BuildingPrivlidge tc)
        {
            if (tc == null || _activeMarkers.ContainsKey(tc.net.ID)) return;

            // Use the TC's network ID as a seed for a consistent but "wrong" position [cite: Search Area Logic]
            UnityEngine.Random.InitState((int)tc.net.ID.Value);
            Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * _config.Markers.RandomOffsetRadius;
            Vector3 jitteredPos = tc.transform.position + new Vector3(randomCircle.x, 0, randomCircle.y);

            MapMarkerGenericRadius marker = GameManager.server.CreateEntity(PrefabMarker, jitteredPos) as MapMarkerGenericRadius;
            if (marker == null) return;

            marker.alpha = _config.Markers.InfiltratorAlpha;
            
            Color c1, c2;
            if (!ColorUtility.TryParseHtmlString(_config.Markers.Color1, out c1)) c1 = Color.red;
            if (!ColorUtility.TryParseHtmlString(_config.Markers.Color2, out c2)) c2 = Color.black;
            
            marker.color1 = c1;
            marker.color2 = c2;
            marker.radius = _config.Markers.InfiltratorRadius;
            marker.name = "RAT_ZONE";
            marker.OwnerID = tc.OwnerID;

            marker.Spawn();
            
            // Set broadcast flag and force update [cite: TcMapMarkers]
            marker.SetFlag(BaseEntity.Flags.Reserved4, true);
            marker.SendUpdate();
            marker.SendNetworkUpdate();

            _activeMarkers[tc.net.ID] = marker;
            
            var hood = GetNeighborhoodAt(tc.transform.position);
            PrintToChat(GetMsg("InfiltratorNews", null, hood.Name));
        }

        private void RemoveMarker(NetworkableId id)
        {
            if (_activeMarkers.TryGetValue(id, out var marker))
            {
                if (marker != null && !marker.IsDestroyed)
                {
                    marker.Kill();
                    marker.SendUpdate();
                }
                _activeMarkers.Remove(id);
            }
        }

        private void RefreshInfiltratorMarkers()
        {
            // Gather all current TCs on the server
            var allTCs = BaseNetworkable.serverEntities.OfType<BuildingPrivlidge>().ToList();
            
            // Check each TC: Should it have a marker right now?
            foreach (var tc in allTCs)
            {
                if (tc == null || tc.OwnerID == 0) continue;
                
                var owner = BasePlayer.FindByID(tc.OwnerID);
                var info = GetPlayerData(tc.OwnerID);
                var zone = GetNeighborhoodAt(tc.transform.position);

                // Conditions for a marker:
                // 1. Owner is an Infiltrator
                // 2. TC is in rival territory
                // 3. Owner is CURRENTLY REVEALED (Spotted)
                bool shouldShow = info.IsInfiltrator && 
                                 info.HomeHood != zone.Type && 
                                 zone.Type != NeighborhoodType.Neutral &&
                                 (owner != null && IsSpotted(owner));

                if (shouldShow)
                {
                    if (!_activeMarkers.ContainsKey(tc.net.ID)) CreateMapMarker(tc);
                }
                else
                {
                    if (_activeMarkers.ContainsKey(tc.net.ID)) RemoveMarker(tc.net.ID);
                }
            }
        }

        private void ClearAllMarkers()
        {
            foreach (var marker in _activeMarkers.Values)
            {
                if (marker != null && !marker.IsDestroyed)
                {
                    marker.Kill();
                    marker.SendUpdate();
                }
            }
            _activeMarkers.Clear();
        }

        private void UpdateAllIdentities()
        {
            foreach (var p in BasePlayer.activePlayerList) UpdateIdentity(p);
        }

        #endregion

        #region Helpers & Commands

        private PlayerGangInfo GetPlayerData(ulong id)
        {
            if (!_storedData.Players.TryGetValue(id, out var info))
            {
                info = new PlayerGangInfo();
                _storedData.Players[id] = info;
            }
            return info;
        }

        private ConfigData.NeighborhoodConfig GetNeighborhoodAt(Vector3 pos)
        {
            foreach (var hood in _config.Neighborhoods)
            {
                if (pos.x >= hood.MinX && pos.x <= hood.MaxX && pos.z >= hood.MinZ && pos.z <= hood.MaxZ)
                    return hood;
            }
            return new ConfigData.NeighborhoodConfig { Name = "Neutral Ground", Type = NeighborhoodType.Neutral, HexColor = "#aaaaaa" };
        }

        private ConfigData.NeighborhoodConfig GetNeighborhoodConfig(NeighborhoodType type)
        {
            return _config.Neighborhoods.FirstOrDefault(x => x.Type == type);
        }

        [ChatCommand("gangname")]
        private void CmdGangName(BasePlayer player, string command, string[] args)
        {
            var info = GetPlayerData(player.userID);
            if (args.Length == 0)
            {
                info.CustomSet = "";
                SendReply(player, "Your sub-gang name has been cleared.");
            }
            else
            {
                string name = string.Join(" ", args).ToUpper();
                if (name.Length > 15) { SendReply(player, "Name too long (max 15 characters)."); return; }
                info.CustomSet = name;
                SendReply(player, $"You are now repping the <color=#ffaa00>{name}</color> set.");
            }
            UpdateIdentity(player);
            SaveData();
        }

        [ChatCommand("whoami")]
        private void CmdWhoAmI(BasePlayer player, string command, string[] args)
        {
            var info = GetPlayerData(player.userID);
            var hood = GetNeighborhoodAt(player.transform.position);
            
            string status = IsSpotted(player) ? "<color=#ff4444>REVEALED</color>" : "<color=#55ff55>HIDDEN</color>";
            if (info.IsInfiltrator) status = "<color=#ff0000>WANTED RAT</color>";

            string hoodName = GetNeighborhoodConfig(info.HomeHood)?.Name ?? "Drifter";

            string msg = GetMsg("WhoAmI_Header", player.UserIDString, player.displayName) + "\n" +
                         GetMsg("WhoAmI_Loyalty", player.UserIDString, hoodName) + "\n" +
                         GetMsg("WhoAmI_Zone", player.UserIDString, hood.Name) + "\n" +
                         GetMsg("WhoAmI_Rep", player.UserIDString, info.Reputation) + "\n" +
                         GetMsg("WhoAmI_Status", player.UserIDString, status);
            
            SendReply(player, msg);
        }

        [ConsoleCommand("hood.clear")]
        private void ConsoleClear(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            ClearAllMarkers();
            Puts("All gang markers cleared from map.");
        }

        [ConsoleCommand("hood.refresh")]
        private void ConsoleRefresh(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            RefreshInfiltratorMarkers();
            Puts("Map markers refreshed based on current TCs.");
        }

        [ConsoleCommand("hood.resetplayer")]
        private void ConsoleResetPlayer(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin || !arg.HasArgs()) return;
            ulong id;
            if (!ulong.TryParse(arg.Args[0], out id))
            {
                Puts("Invalid SteamID provided.");
                return;
            }
            
            if (_storedData.Players.Remove(id))
            {
                Puts($"Reset gang data for {id}");
                SaveData();
            }
        }

        #endregion
    }
}