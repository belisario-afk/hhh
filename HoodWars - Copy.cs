using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("HoodWars", "Gemini", "7.9.6")]
    [Description("A robust, comprehensive gang-based territory and identity system for a unique vanilla-feel Rust experience.")]
    public class HoodWars : RustPlugin
    {
        #region References & Fields

        private static HoodWars _instance;
        private StoredData _storedData;
        private DynamicConfigFile _data;

        private Dictionary<ulong, float> _spottedPlayers = new Dictionary<ulong, float>();
        private Dictionary<NetworkableId, MapMarkerGenericRadius> _activeMarkers = new Dictionary<NetworkableId, MapMarkerGenericRadius>();
        private Dictionary<ulong, Dictionary<NeighborhoodType, float>> _trespassWarningCooldowns = new Dictionary<ulong, Dictionary<NeighborhoodType, float>>();
        private Dictionary<NeighborhoodType, NetworkableId> _hqToolCupboards = new Dictionary<NeighborhoodType, NetworkableId>();
        private Dictionary<NeighborhoodType, SphereEntity> _hqSphereMarkers = new Dictionary<NeighborhoodType, SphereEntity>();
        
        // Timer for periodic updates
        private Timer _identityTimer;

        // ManualDoor plugin reference for hotel door integration
        [PluginReference]
        private Plugin ManualDoor;

        // GangKits plugin reference for gang outfit/weapon integration
        [PluginReference]
        private Plugin GangKits;

        // DriveBy plugin reference for NPC drive-by events
        [PluginReference]
        private Plugin DriveBy;

        private const string PrefabMarker = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string PrefabSphere = "assets/prefabs/visualization/sphere.prefab";
        private const string PrefabToolCupboard = "assets/prefabs/deployable/tool cupboard/cupboard.tool.deployed.prefab";
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

            [JsonProperty("HQ Settings")]
            public HQSettings HQ { get; set; }

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

                [JsonProperty("HQ Center X")]
                public float HQCenterX { get; set; }

                [JsonProperty("HQ Center Z")]
                public float HQCenterZ { get; set; }

                [JsonProperty("HQ Radius (Meters)")]
                public float HQRadius { get; set; } = 50f;
            }

            public class HQSettings
            {
                [JsonProperty("Enable HQ Safezones")]
                public bool EnableHQSafezones { get; set; } = true;

                [JsonProperty("Trespass Warning Interval (Seconds)")]
                public float TrespassWarningInterval { get; set; } = 30f;

                [JsonProperty("Show HQ Zone Spheres")]
                public bool ShowHQSpheres { get; set; } = true;

                [JsonProperty("HQ Sphere Opacity (0.0 to 1.0)")]
                public float HQSphereAlpha { get; set; } = 0.25f;

                [JsonProperty("Allowed Hotel Items (Short Prefab Names)")]
                public List<string> AllowedHotelItems { get; set; } = new List<string>
                {
                    "box.wooden.large",
                    "box.wooden",
                    "sleepingbag_leather_deployed",
                    "bed_deployed",
                    "small_stash_deployed",
                    "rug.deployed",
                    "rug.bear.deployed",
                    "furnace",
                    "campfire",
                    "workbench1.deployed",
                    "research.table.deployed",
                    "mixingtable.deployed",
                    "locker.deployed",
                    "fridge.deployed",
                    "repairbench_deployed"
                };
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
                
                // Ensure nested objects exist (for existing configs that may not have new properties)
                if (_config.General == null) _config.General = new ConfigData.GeneralSettings();
                if (_config.Markers == null) _config.Markers = new ConfigData.MarkerSettings();
                if (_config.Chat == null) _config.Chat = new ConfigData.ChatSettings();
                if (_config.HQ == null) _config.HQ = new ConfigData.HQSettings();
                if (_config.Neighborhoods == null) _config.Neighborhoods = new List<ConfigData.NeighborhoodConfig>();
                if (_config.HQ.AllowedHotelItems == null) _config.HQ.AllowedHotelItems = new List<string> 
                {
                    "box.wooden.large", "box.wooden", "sleepingbag_leather_deployed", 
                    "bed_deployed", "small_stash_deployed", "furnace", "campfire",
                    "workbench1.deployed", "locker.deployed", "fridge.deployed"
                };
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
            // HQ centers are in the middle of each quadrant
            _config = new ConfigData
            {
                General = new ConfigData.GeneralSettings(),
                Markers = new ConfigData.MarkerSettings(),
                Chat = new ConfigData.ChatSettings(),
                HQ = new ConfigData.HQSettings(),
                Neighborhoods = new List<ConfigData.NeighborhoodConfig>
                {
                    new ConfigData.NeighborhoodConfig { Name = "Westside Pirus", Type = NeighborhoodType.West, MinX = -1050, MaxX = 0, MinZ = 0, MaxZ = 1050, HexColor = "#ff4444", HQCenterX = -525, HQCenterZ = 525, HQRadius = 50 },
                    new ConfigData.NeighborhoodConfig { Name = "Northside Vagos", Type = NeighborhoodType.North, MinX = 0, MaxX = 1050, MinZ = 0, MaxZ = 1050, HexColor = "#ccff33", HQCenterX = 525, HQCenterZ = 525, HQRadius = 50 },
                    new ConfigData.NeighborhoodConfig { Name = "Southside Sureños", Type = NeighborhoodType.South, MinX = -1050, MaxX = 0, MinZ = -1050, MaxZ = 0, HexColor = "#3366ff", HQCenterX = -525, HQCenterZ = -525, HQRadius = 50 },
                    new ConfigData.NeighborhoodConfig { Name = "Eastside Disciples", Type = NeighborhoodType.East, MinX = 0, MaxX = 1050, MinZ = -1050, MaxZ = 0, HexColor = "#444444", HQCenterX = 525, HQCenterZ = -525, HQRadius = 50 }
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
                ["InfiltratorNews"] = "<color=#ff4444>[STREET NEWS]</color> A rival presence was detected in {0}! Check your maps for the search area.",
                ["HQ_Trespass"] = "<color=#ffaa00>WARNING:</color> You are trespassing in <color={0}>{1}</color> territory.",
                ["HQ_NoBuild_Rival"] = "<color=#ff4444>ACCESS DENIED:</color> You cannot build in enemy HQ territory.",
                ["HQ_NoBuild_TC"] = "<color=#ff4444>ACCESS DENIED:</color> Only the HQ Tool Cupboard is allowed here. Gang members can place personal items in hotel rooms.",
                ["HQ_NoBuild_Item"] = "<color=#ff4444>ACCESS DENIED:</color> Only small personal items (boxes, bags, beds) are allowed in hotel rooms.",
                ["HQ_NoAuth_Rival"] = "<color=#ff4444>ACCESS DENIED:</color> You cannot authorize on this HQ's Tool Cupboard.",
                ["HQ_TC_NoTake"] = "<color=#ff4444>ACCESS DENIED:</color> You can only deposit resources into the HQ Tool Cupboard, not withdraw.",
                ["HQ_Safezone"] = "<color=#55ff55>SAFEZONE:</color> You are now in {0} HQ. No damage can be dealt here.",
                ["HQ_TC_Registered"] = "<color=#55ff55>HQ REGISTERED:</color> This Tool Cupboard is now the official {0} headquarters TC.",
                ["HQ_NotYourHQ"] = "<color=#ff4444>ACCESS DENIED:</color> This is not your gang's HQ. You cannot build here."
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

            // Create HQ sphere markers
            if (_config != null && _config.HQ != null && _config.HQ.ShowHQSpheres)
            {
                CreateAllHQSpheres();
            }
        }

        private void Unload()
        {
            _identityTimer?.Destroy();
            ClearAllMarkers();
            ClearAllHQSpheres();
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

            // Check HQ authorization restrictions first
            if (_config != null && _config.HQ != null && _config.HQ.EnableHQSafezones)
            {
                var hqHood = GetHQAtPosition(privilege.transform.position);
                if (hqHood != null)
                {
                    var playerInfo = GetPlayerData(player.userID);

                    // Block rivals from authorizing on HQ TCs
                    if (playerInfo.HomeHood != hqHood.Type && playerInfo.HomeHood != NeighborhoodType.Neutral)
                    {
                        SendReply(player, GetMsg("HQ_NoAuth_Rival", player.UserIDString));
                        // Schedule deauthorization on next frame since we can't modify during this hook cleanly
                        ulong targetUserId = player.userID;
                        timer.Once(0.1f, () => 
                        {
                            if (privilege != null && !privilege.IsDestroyed)
                            {
                                // Remove the player from the authorized HashSet
                                // In latest Rust, authorizedPlayers is HashSet<ulong>
                                privilege.authorizedPlayers.Remove(targetUserId);
                                privilege.SendNetworkUpdate();
                            }
                        });
                        return;
                    }
                }
            }

            CheckTCPlacement(privilege, player);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity is BuildingPrivlidge tc)
            {
                // Check if this is an HQ TC - HQ TCs should never be killed
                // (This is a backup - damage should be blocked earlier)
                var hqHood = GetHQAtPosition(tc.transform.position);
                if (hqHood != null && _hqToolCupboards.TryGetValue(hqHood.Type, out var tcId) && tc.net?.ID == tcId)
                {
                    // Remove from HQ registry since it's being destroyed (shouldn't happen normally)
                    _hqToolCupboards.Remove(hqHood.Type);
                }

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

        #region HQ Safezone Hooks

        // Block all damage in HQ safezones
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones || entity == null || info == null) return null;

            // Let ManualDoor handle its own doors (avoid hook conflict)
            if (ManualDoor != null && entity.net != null)
            {
                try
                {
                    var result = ManualDoor.Call("API_IsManagedDoor", entity.net.ID.Value);
                    bool isDoor = result != null && (bool)result;
                    if (isDoor)
                    {
                        // Let ManualDoor handle decay damage on its doors
                        if (info.damageTypes.Has(Rust.DamageType.Decay))
                        {
                            return null;  // Let ManualDoor handle this
                        }
                        // For non-decay damage on ManualDoor doors in HQ, still block it
                    }
                }
                catch
                {
                    // API not available or returned unexpected type - continue with HoodWars logic
                }
            }

            // Check if entity is in an HQ safezone
            var hqHood = GetHQAtPosition(entity.transform.position);
            if (hqHood == null) return null;

            // Check if it's an HQ TC - these are indestructible
            if (entity is BuildingPrivlidge tc)
            {
                if (_hqToolCupboards.TryGetValue(hqHood.Type, out var tcId) && tc.net?.ID == tcId)
                {
                    // HQ TC is indestructible - nullify damage
                    info.damageTypes.ScaleAll(0f);
                    return true;
                }
            }

            // Block all damage in HQ safezone by scaling to 0
            info.damageTypes.ScaleAll(0f);
            return true;
        }

        // Block building for rivals in HQ zones and restrict TC placement
        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones || planner == null) return null;

            var player = planner.GetOwnerPlayer();
            if (player == null) return null;

            var hqHood = GetHQAtPosition(target.position);
            if (hqHood == null) return null;

            var playerInfo = GetPlayerData(player.userID);

            // Check if player belongs to this HQ's gang
            // Neutral players are also blocked - they must join a gang first
            if (playerInfo.HomeHood != hqHood.Type)
            {
                // Rivals and neutral players cannot build anything in gang HQ
                SendReply(player, GetMsg("HQ_NoBuild_Rival", player.UserIDString));
                return false;
            }

            // Player is from this gang - check what they're trying to build
            string shortName = prefab.fullName;

            // Check if trying to place a Tool Cupboard
            bool isCupboard = shortName.EndsWith("cupboard.tool.deployed") || shortName.Contains("/cupboard.tool");
            
            if (isCupboard)
            {
                // Only allow TC placement if this HQ doesn't have one yet
                if (_hqToolCupboards.ContainsKey(hqHood.Type))
                {
                    SendReply(player, GetMsg("HQ_NoBuild_TC", player.UserIDString));
                    return false;
                }
                // Allow first TC placement
                return null;
            }

            // Allow only hotel items for gang members (not TCs at this point)
            bool isAllowedItem = _config.HQ.AllowedHotelItems.Any(allowed => 
                shortName.EndsWith(allowed) || shortName.Contains("/" + allowed));

            if (!isAllowedItem)
            {
                SendReply(player, GetMsg("HQ_NoBuild_Item", player.UserIDString));
                return false;
            }

            return null;
        }

        // Handle TC placement in HQ - register as HQ TC
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones) return;

            if (entity is BuildingPrivlidge tc)
            {
                var hqHood = GetHQAtPosition(tc.transform.position);
                if (hqHood == null) return;

                // Register this TC as the HQ TC if not already registered
                if (!_hqToolCupboards.ContainsKey(hqHood.Type))
                {
                    _hqToolCupboards[hqHood.Type] = tc.net.ID;
                    
                    // Notify the owner
                    var owner = BasePlayer.FindByID(tc.OwnerID);
                    if (owner != null)
                    {
                        SendReply(owner, GetMsg("HQ_TC_Registered", owner.UserIDString, hqHood.Name));
                    }
                }
            }
        }

        // Block taking items from HQ TC (only allow deposits)
        private object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainerId targetContainerId, int targetSlot, int amount)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones || item == null || playerInventory == null) return null;

            // Check if item is coming FROM a TC container
            var sourceContainer = item.parent;
            if (sourceContainer?.entityOwner is BuildingPrivlidge tc)
            {
                var hqHood = GetHQAtPosition(tc.transform.position);
                if (hqHood == null) return null;

                // Check if this is an HQ TC
                if (_hqToolCupboards.TryGetValue(hqHood.Type, out var tcId) && tc.net?.ID == tcId)
                {
                    // Block taking items from HQ TC
                    var player = playerInventory.baseEntity;
                    if (player != null)
                    {
                        SendReply(player, GetMsg("HQ_TC_NoTake", player.UserIDString));
                    }
                    return false;
                }
            }

            return null;
        }

        // Track player movement for trespass warnings
        private void OnPlayerTick(BasePlayer player)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones || player == null || player.IsNpc) return;

            CheckTrespassWarning(player);
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
                    
                    // Give gang kit when player bloods in
                    Puts($"[DEBUG] Player {player.displayName} blooded into {zone.Name}, triggering GangKit...");
                    timer.Once(0.5f, () => {
                        if (GangKits != null)
                        {
                            Puts($"[DEBUG] Calling GangKits.API_GiveGangKit for {player.displayName} (gang: {zone.Name})");
                            GangKits.Call("API_GiveGangKit", player, zone.Name);
                            SendReply(player, "<color=#55ff55>GANG KIT:</color> Welcome to the gang! You've received your gang outfit and weapon.");
                        }
                        else
                        {
                            Puts("[DEBUG] GangKits plugin not found!");
                        }
                    });
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

        #region HQ Sphere Visual Markers

        private void CreateAllHQSpheres()
        {
            foreach (var hood in _config.Neighborhoods)
            {
                if (hood.Type == NeighborhoodType.Neutral) continue;
                CreateHQSphere(hood);
            }
        }

        private void CreateHQSphere(ConfigData.NeighborhoodConfig hood)
        {
            if (hood == null || hood.Type == NeighborhoodType.Neutral) return;
            
            // Remove existing sphere if any
            RemoveHQSphere(hood.Type);

            // Create sphere at HQ center (positioned slightly above terrain)
            float terrainHeight = TerrainMeta.HeightMap.GetHeight(new Vector3(hood.HQCenterX, 0, hood.HQCenterZ));
            Vector3 center = new Vector3(hood.HQCenterX, terrainHeight + 1f, hood.HQCenterZ);
            
            var sphere = GameManager.server.CreateEntity(PrefabSphere, center) as SphereEntity;
            if (sphere == null) return;

            // SphereEntity.currentRadius is actually the diameter, so radius * 2 gives correct visual size
            sphere.currentRadius = hood.HQRadius * 2f;
            sphere.lerpSpeed = 0f;
            
            sphere.Spawn();

            // Set sphere color based on gang color
            Color gangColor;
            if (!ColorUtility.TryParseHtmlString(hood.HexColor, out gangColor))
            {
                gangColor = Color.white;
            }

            // Apply color and transparency to the sphere
            var renderer = sphere.GetComponentInChildren<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                // Create a new material instance to avoid affecting other spheres
                var mat = new Material(renderer.sharedMaterial);
                gangColor.a = _config.HQ.HQSphereAlpha;
                mat.color = gangColor;
                renderer.material = mat;
            }

            _hqSphereMarkers[hood.Type] = sphere;
        }

        private void RemoveHQSphere(NeighborhoodType type)
        {
            if (_hqSphereMarkers.TryGetValue(type, out var sphere))
            {
                if (sphere != null && !sphere.IsDestroyed)
                {
                    sphere.Kill();
                }
                _hqSphereMarkers.Remove(type);
            }
        }

        private void ClearAllHQSpheres()
        {
            foreach (var sphere in _hqSphereMarkers.Values)
            {
                if (sphere != null && !sphere.IsDestroyed)
                {
                    sphere.Kill();
                }
            }
            _hqSphereMarkers.Clear();
        }

        private void RefreshAllHQSpheres()
        {
            ClearAllHQSpheres();
            if (_config.HQ.ShowHQSpheres)
            {
                CreateAllHQSpheres();
            }
        }

        #endregion

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

        // Get the HQ neighborhood config if position is within an HQ radius
        private ConfigData.NeighborhoodConfig GetHQAtPosition(Vector3 pos)
        {
            if (_config == null || _config.Neighborhoods == null) return null;

            foreach (var hood in _config.Neighborhoods)
            {
                if (hood.Type == NeighborhoodType.Neutral) continue;

                float distance = Vector3.Distance(
                    new Vector3(hood.HQCenterX, pos.y, hood.HQCenterZ),
                    pos
                );

                if (distance <= hood.HQRadius)
                {
                    return hood;
                }
            }
            return null;
        }

        // Check and send trespass warnings to players in enemy HQ
        private void CheckTrespassWarning(BasePlayer player)
        {
            if (player == null) return;

            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null) return;

            var playerInfo = GetPlayerData(player.userID);

            // If player is neutral, they haven't chosen a gang yet
            if (playerInfo.HomeHood == NeighborhoodType.Neutral) return;

            // If player is in their own HQ, show safezone message (with cooldown)
            if (playerInfo.HomeHood == hqHood.Type)
            {
                if (CanShowTrespassWarning(player.userID, hqHood.Type))
                {
                    SendReply(player, GetMsg("HQ_Safezone", player.UserIDString, hqHood.Name));
                    SetTrespassWarningCooldown(player.userID, hqHood.Type);
                }
                return;
            }

            // Player is in enemy HQ - show trespass warning
            if (CanShowTrespassWarning(player.userID, hqHood.Type))
            {
                SendReply(player, GetMsg("HQ_Trespass", player.UserIDString, hqHood.HexColor, hqHood.Name));
                SetTrespassWarningCooldown(player.userID, hqHood.Type);
            }
        }

        private bool CanShowTrespassWarning(ulong playerId, NeighborhoodType hqType)
        {
            if (!_trespassWarningCooldowns.TryGetValue(playerId, out var cooldowns))
                return true;

            if (!cooldowns.TryGetValue(hqType, out float expiry))
                return true;

            return Time.realtimeSinceStartup >= expiry;
        }

        private void SetTrespassWarningCooldown(ulong playerId, NeighborhoodType hqType)
        {
            if (!_trespassWarningCooldowns.TryGetValue(playerId, out var cooldowns))
            {
                cooldowns = new Dictionary<NeighborhoodType, float>();
                _trespassWarningCooldowns[playerId] = cooldowns;
            }

            cooldowns[hqType] = Time.realtimeSinceStartup + _config.HQ.TrespassWarningInterval;
        }

        // Check if a TC is an HQ TC
        private bool IsHQToolCupboard(BuildingPrivlidge tc)
        {
            if (tc == null) return false;

            var hqHood = GetHQAtPosition(tc.transform.position);
            if (hqHood == null) return false;

            return _hqToolCupboards.TryGetValue(hqHood.Type, out var tcId) && tc.net?.ID == tcId;
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

        #region Admin GUI

        private const string AdminUIName = "HoodWars_AdminUI";
        private const string AdminUIOverlay = "HoodWars_AdminOverlay";
        private Dictionary<ulong, int> _adminUIPage = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> _adminUISection = new Dictionary<ulong, string>();

        [ConsoleCommand("hoodwars.admin")]
        private void ConsoleHoodAdmin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin) return;

            if (!arg.HasArgs()) return;

            // Ensure config is loaded before processing any commands
            if (_config == null)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> Configuration not loaded. Please reload the plugin.");
                return;
            }

            string action = arg.Args[0].ToLower();

            switch (action)
            {
                case "close":
                    DestroyAdminUI(player);
                    break;

                case "section":
                    if (arg.Args.Length < 2) return;
                    _adminUISection[player.userID] = arg.Args[1];
                    _adminUIPage[player.userID] = 0;
                    ShowAdminUI(player);
                    break;

                case "page":
                    if (arg.Args.Length < 2) return;
                    int page;
                    if (int.TryParse(arg.Args[1], out page))
                    {
                        _adminUIPage[player.userID] = Math.Max(0, page);
                        ShowAdminUI(player);
                    }
                    break;

                case "sethqcenter":
                    if (_config.HQ == null || _config.Neighborhoods == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    int hoodIndex;
                    if (int.TryParse(arg.Args[1], out hoodIndex) && hoodIndex >= 0 && hoodIndex < _config.Neighborhoods.Count)
                    {
                        var hood = _config.Neighborhoods[hoodIndex];
                        hood.HQCenterX = player.transform.position.x;
                        hood.HQCenterZ = player.transform.position.z;
                        SaveConfig();
                        // Refresh sphere for this hood
                        if (_config.HQ.ShowHQSpheres)
                        {
                            CreateHQSphere(hood);
                        }
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ center for {hood.Name} set to your current position ({hood.HQCenterX:F1}, {hood.HQCenterZ:F1})");
                        ShowAdminUI(player);
                    }
                    break;

                case "sethqradius":
                    if (_config.HQ == null || _config.Neighborhoods == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 3) return;
                    int hIndex;
                    float radius;
                    if (int.TryParse(arg.Args[1], out hIndex) && float.TryParse(arg.Args[2], out radius) && hIndex >= 0 && hIndex < _config.Neighborhoods.Count)
                    {
                        var hood = _config.Neighborhoods[hIndex];
                        hood.HQRadius = Math.Max(10f, radius);
                        SaveConfig();
                        // Refresh sphere for this hood
                        if (_config.HQ.ShowHQSpheres)
                        {
                            CreateHQSphere(hood);
                        }
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ radius updated to {radius}m");
                        ShowAdminUI(player);
                    }
                    break;

                case "togglesafezone":
                    if (_config.HQ == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    _config.HQ.EnableHQSafezones = !_config.HQ.EnableHQSafezones;
                    SaveConfig();
                    SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ Safezones are now {(_config.HQ.EnableHQSafezones ? "ENABLED" : "DISABLED")}");
                    ShowAdminUI(player);
                    break;

                case "togglehqspheres":
                    if (_config.HQ == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    _config.HQ.ShowHQSpheres = !_config.HQ.ShowHQSpheres;
                    SaveConfig();
                    RefreshAllHQSpheres();
                    SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ Sphere Markers are now {(_config.HQ.ShowHQSpheres ? "VISIBLE" : "HIDDEN")}");
                    ShowAdminUI(player);
                    break;

                case "setwarninginterval":
                    if (_config.HQ == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    float interval;
                    if (float.TryParse(arg.Args[1], out interval))
                    {
                        _config.HQ.TrespassWarningInterval = Math.Max(5f, interval);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Trespass warning interval set to {interval}s");
                        ShowAdminUI(player);
                    }
                    break;

                case "addhotelitem":
                    if (_config.HQ == null || _config.HQ.AllowedHotelItems == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    string itemToAdd = arg.Args[1];
                    if (!_config.HQ.AllowedHotelItems.Contains(itemToAdd))
                    {
                        _config.HQ.AllowedHotelItems.Add(itemToAdd);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Added '{itemToAdd}' to allowed hotel items");
                    }
                    ShowAdminUI(player);
                    break;

                case "removehotelitem":
                    if (_config.HQ == null || _config.HQ.AllowedHotelItems == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    int itemIndex;
                    if (int.TryParse(arg.Args[1], out itemIndex) && itemIndex >= 0 && itemIndex < _config.HQ.AllowedHotelItems.Count)
                    {
                        string removed = _config.HQ.AllowedHotelItems[itemIndex];
                        _config.HQ.AllowedHotelItems.RemoveAt(itemIndex);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Removed '{removed}' from allowed hotel items");
                        ShowAdminUI(player);
                    }
                    break;

                case "setrevealduration":
                    if (_config.General == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    float duration;
                    if (float.TryParse(arg.Args[1], out duration))
                    {
                        _config.General.RevealDuration = Math.Max(10f, duration);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Reveal duration set to {duration}s");
                        ShowAdminUI(player);
                    }
                    break;

                case "setproximitydist":
                    if (_config.General == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    float dist;
                    if (float.TryParse(arg.Args[1], out dist))
                    {
                        _config.General.ProximityDistance = Math.Max(1f, dist);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Proximity distance set to {dist}m");
                        ShowAdminUI(player);
                    }
                    break;

                case "togglekillreveal":
                    if (_config.General == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    _config.General.KillReveal = !_config.General.KillReveal;
                    SaveConfig();
                    SendReply(player, $"<color=#55ff55>SUCCESS:</color> Kill reveal is now {(_config.General.KillReveal ? "ENABLED" : "DISABLED")}");
                    ShowAdminUI(player);
                    break;

                case "setbounty":
                    if (_config.General == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    int bounty;
                    if (int.TryParse(arg.Args[1], out bounty))
                    {
                        _config.General.BountyAmount = Math.Max(0, bounty);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Bounty amount set to {bounty} scrap");
                        ShowAdminUI(player);
                    }
                    break;

                case "clearhqtc":
                    if (_config.Neighborhoods == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    int clearIndex;
                    if (int.TryParse(arg.Args[1], out clearIndex) && clearIndex >= 0 && clearIndex < _config.Neighborhoods.Count)
                    {
                        var hood = _config.Neighborhoods[clearIndex];
                        if (_hqToolCupboards.Remove(hood.Type))
                        {
                            SendReply(player, $"<color=#55ff55>SUCCESS:</color> Cleared HQ TC registration for {hood.Name}");
                        }
                        ShowAdminUI(player);
                    }
                    break;

                case "sethqtc":
                    if (_config.Neighborhoods == null) { SendReply(player, "<color=#ff4444>ERROR:</color> Config not fully loaded."); return; }
                    if (arg.Args.Length < 2) return;
                    int setIndex;
                    if (int.TryParse(arg.Args[1], out setIndex) && setIndex >= 0 && setIndex < _config.Neighborhoods.Count)
                    {
                        SetHQTCFromLook(player, setIndex);
                        ShowAdminUI(player);
                    }
                    break;

                case "forceexpire":
                    ForceExpireNearestDoor(player);
                    break;

                // Testing commands
                case "testsafezone":
                    TestSafezoneFromUI(player);
                    break;

                case "testtrespass":
                    TestTrespassFromUI(player);
                    break;

                case "spawndoor":
                    SpawnHotelDoor(player, false);
                    break;

                case "spawndoubledoor":
                    SpawnHotelDoor(player, true);
                    break;

                case "doorinfo":
                    GetHotelDoorInfo(player);
                    break;

                case "resetdoor":
                    ResetHotelDoor(player);
                    break;

                case "listdoors":
                    ListNearbyDoors(player);
                    break;

                case "resetgangtc":
                    if (arg.Args.Length < 2) return;
                    ResetGangTCFromUI(player, arg.Args[1]);
                    ShowAdminUI(player);
                    break;

                case "testallkits":
                    TestAllGangKits(player);
                    break;

                case "givemykit":
                    GivePlayerGangKit(player);
                    break;

                case "testdriveby":
                    TestDriveBy(player);
                    break;
            }
        }

        // UI-triggered test methods
        private void TestSafezoneFromUI(BasePlayer player)
        {
            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>SAFEZONE TEST:</color> You are NOT in any HQ safezone.");
                return;
            }

            var playerInfo = GetPlayerData(player.userID);
            bool isOwner = playerInfo.HomeHood == hqHood.Type;

            SendReply(player, $"<color=#55ff55>SAFEZONE TEST:</color>\n" +
                             $"HQ Zone: <color={hqHood.HexColor}>{hqHood.Name}</color>\n" +
                             $"Your Gang: {playerInfo.HomeHood}\n" +
                             $"Is Your HQ: {(isOwner ? "<color=#55ff55>YES</color>" : "<color=#ff4444>NO</color>")}\n" +
                             $"Can Build: {(isOwner ? "<color=#55ff55>Hotel items only</color>" : "<color=#ff4444>NO</color>")}\n" +
                             $"Can Take Damage: <color=#55ff55>NO (Protected)</color>\n" +
                             $"Safezones Active: {(_config.HQ.EnableHQSafezones ? "<color=#55ff55>YES</color>" : "<color=#ff4444>NO</color>")}");
        }

        private void TestTrespassFromUI(BasePlayer player)
        {
            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>TRESPASS TEST:</color> You are NOT in any HQ zone.");
                return;
            }

            var playerInfo = GetPlayerData(player.userID);
            if (playerInfo.HomeHood == hqHood.Type)
            {
                SendReply(player, GetMsg("HQ_Safezone", player.UserIDString, hqHood.Name));
            }
            else
            {
                SendReply(player, GetMsg("HQ_Trespass", player.UserIDString, hqHood.HexColor, hqHood.Name));
            }
        }

        private void ListNearbyDoors(BasePlayer player)
        {
            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> You are not in an HQ zone. Stand in an HQ area to list doors.");
                return;
            }

            var doorsInHQ = BaseNetworkable.serverEntities.OfType<Door>()
                .Where(d => d != null && GetHQAtPosition(d.transform.position)?.Type == hqHood.Type)
                .Take(10)
                .ToList();

            SendReply(player, $"<color=#55ff55>DOORS IN {hqHood.Name.ToUpper()} HQ:</color>");
            
            if (doorsInHQ.Count == 0)
            {
                SendReply(player, "No doors found in this HQ zone.");
                return;
            }

            int count = 0;
            foreach (var door in doorsInHQ)
            {
                count++;
                float dist = Vector3.Distance(player.transform.position, door.transform.position);
                SendReply(player, $"{count}. Distance: {dist:F1}m | ID: {door.net?.ID.Value}");
            }
        }

        private void ResetGangTCFromUI(BasePlayer player, string gangType)
        {
            NeighborhoodType type;
            switch (gangType.ToLower())
            {
                case "west":
                    type = NeighborhoodType.West;
                    break;
                case "north":
                    type = NeighborhoodType.North;
                    break;
                case "south":
                    type = NeighborhoodType.South;
                    break;
                case "east":
                    type = NeighborhoodType.East;
                    break;
                default:
                    SendReply(player, "<color=#ff4444>ERROR:</color> Invalid gang type.");
                    return;
            }

            if (_hqToolCupboards.Remove(type))
            {
                var hood = GetNeighborhoodConfig(type);
                SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ TC registration cleared for {hood?.Name ?? type.ToString()}.");
            }
            else
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> No HQ TC was registered for that gang.");
            }
        }

        // Test all gang kits (calls GangKits plugin)
        private void TestAllGangKits(BasePlayer player)
        {
            if (GangKits == null || !GangKits.IsLoaded)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> GangKits plugin is not loaded.");
                return;
            }

            // Call the testallkits command on the player
            player.SendConsoleCommand("chat.say", "/testallkits");
            SendReply(player, "<color=#55ff55>GANG KITS TEST:</color> All 4 gang kits spawned. Check your inventory.");
        }

        // Give player their gang's kit
        private void GivePlayerGangKit(BasePlayer player)
        {
            if (GangKits == null || !GangKits.IsLoaded)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> GangKits plugin is not loaded.");
                return;
            }

            var playerInfo = GetPlayerData(player.userID);
            if (playerInfo.HomeHood == NeighborhoodType.Neutral)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> You are not in a gang. Join a gang first by building a TC in gang territory.");
                return;
            }

            var hoodConfig = GetNeighborhoodConfig(playerInfo.HomeHood);
            string gangName = hoodConfig?.Name ?? "Unknown";

            // Call GangKits to give the kit - it will automatically detect the player's gang
            // We simulate this by triggering the OnPlayerRespawned-like behavior
            GangKits.Call("API_GiveGangKit", player, gangName);
            SendReply(player, $"<color=#55ff55>GANG KIT:</color> Gave you your <color={hoodConfig?.HexColor ?? "#ffffff"}>{gangName}</color> kit.");
        }

        // Test DriveBy functionality via admin command
        private void TestDriveBy(BasePlayer player)
        {
            if (DriveBy == null || !DriveBy.IsLoaded)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> DriveBy plugin is not loaded.");
                return;
            }

            // Get the current territory the player is in
            var hood = GetNeighborhoodAt(player.transform.position);
            if (hood == null || hood.Type == NeighborhoodType.Neutral)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> Stand in a gang territory to test a drive-by. Current location: Neutral Ground");
                return;
            }

            SendReply(player, $"<color=#ff4444>DRIVE-BY TEST:</color> Triggering drive-by in <color={hood.HexColor}>{hood.Name}</color> territory...");
            
            // Call the DriveBy plugin's test command functionality
            // DriveBy has a /testdriveby chat command, but we can also call it directly
            player.Command("chat.say", "/testdriveby");
        }

        // Admin command to set the TC they're looking at as the HQ TC for a gang
        private void SetHQTCFromLook(BasePlayer player, int gangIndex)
        {
            if (_config == null || _config.Neighborhoods == null || gangIndex < 0 || gangIndex >= _config.Neighborhoods.Count)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> Invalid gang index.");
                return;
            }

            // Raycast to find TC the admin is looking at
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> Look at a Tool Cupboard within 10m and try again.");
                return;
            }

            var tc = hit.GetEntity() as BuildingPrivlidge;
            if (tc == null)
            {
                // Try to find nearest TC
                tc = BaseNetworkable.serverEntities.OfType<BuildingPrivlidge>()
                    .Where(t => t != null && Vector3.Distance(t.transform.position, player.transform.position) < 5f)
                    .OrderBy(t => Vector3.Distance(t.transform.position, player.transform.position))
                    .FirstOrDefault();
            }

            if (tc == null)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> No Tool Cupboard found nearby. Stand next to a TC and try again.");
                return;
            }

            var hood = _config.Neighborhoods[gangIndex];
            
            // Clear existing TC registration for this gang
            _hqToolCupboards.Remove(hood.Type);
            
            // Set this TC as the HQ TC
            _hqToolCupboards[hood.Type] = tc.net.ID;
            
            SendReply(player, $"<color=#55ff55>SUCCESS:</color> Set TC (ID: {tc.net.ID.Value}) as the HQ TC for <color={hood.HexColor}>{hood.Name}</color>.\n" +
                             "This TC is now indestructible and deposit-only.");
        }

        // Force expire the claim on the nearest door (for testing eviction timer)
        private void ForceExpireNearestDoor(BasePlayer player)
        {
            if (!IsManualDoorLoaded())
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> ManualDoor plugin is not loaded.");
                return;
            }

            var nearestDoor = FindNearestHotelDoor(player.transform.position, 5f);
            if (nearestDoor == null)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> No hotel door found within 5m. Stand next to a door and try again.");
                return;
            }

            // Call ManualDoor API to force expire the claim
            var result = ManualDoor?.Call("API_ForceExpireClaim", nearestDoor.net.ID.Value);
            if (result != null && (bool)result)
            {
                SendReply(player, "<color=#55ff55>EVICTION TIMER TEST:</color> Door claim has been force-expired. The claimant should receive the eviction notification.");
            }
            else
            {
                // If API doesn't exist, try the resetdoor approach
                player.SendConsoleCommand("chat.say", "/resetdoor");
                SendReply(player, "<color=#ffaa00>INFO:</color> API_ForceExpireClaim not found in ManualDoor. Used /resetdoor instead.");
            }
        }

        private void ShowAdminUI(BasePlayer player)
        {
            DestroyAdminUI(player);

            string section = _adminUISection.ContainsKey(player.userID) ? _adminUISection[player.userID] : "main";
            int page = _adminUIPage.ContainsKey(player.userID) ? _adminUIPage[player.userID] : 0;

            var elements = new CuiElementContainer();

            // Main panel background
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.95" },
                RectTransform = { AnchorMin = "0.15 0.15", AnchorMax = "0.85 0.85" },
                CursorEnabled = true
            }, "Overlay", AdminUIName);

            // Header
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.2 0.2 1" },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, AdminUIName, "Header");

            elements.Add(new CuiLabel
            {
                Text = { Text = "HOODWARS ADMIN PANEL", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "Header");

            // Close button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "hoodwars.admin close" },
                RectTransform = { AnchorMin = "0.92 0.2", AnchorMax = "0.98 0.8" },
                Text = { Text = "X", FontSize = 16, Align = TextAnchor.MiddleCenter }
            }, "Header");

            // Navigation tabs
            AddNavTab(elements, "main", "Main", "0.01 0.82", "0.12 0.88", section == "main");
            AddNavTab(elements, "hq", "HQ", "0.13 0.82", "0.24 0.88", section == "hq");
            AddNavTab(elements, "neighborhoods", "Hoods", "0.25 0.82", "0.38 0.88", section == "neighborhoods");
            AddNavTab(elements, "hotelitems", "Hotel", "0.39 0.82", "0.50 0.88", section == "hotelitems");
            AddNavTab(elements, "general", "General", "0.51 0.82", "0.64 0.88", section == "general");
            AddNavTab(elements, "testing", "Testing", "0.65 0.82", "0.78 0.88", section == "testing");

            // Content area
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 1" },
                RectTransform = { AnchorMin = "0.01 0.05", AnchorMax = "0.99 0.80" }
            }, AdminUIName, "Content");

            switch (section)
            {
                case "main":
                    AddMainMenuContent(elements);
                    break;
                case "hq":
                    AddHQSettingsContent(elements);
                    break;
                case "neighborhoods":
                    AddNeighborhoodsContent(elements, page);
                    break;
                case "hotelitems":
                    AddHotelItemsContent(elements, page);
                    break;
                case "general":
                    AddGeneralSettingsContent(elements);
                    break;
                case "testing":
                    AddTestingContent(elements, player);
                    break;
            }

            CuiHelper.AddUi(player, elements);
        }

        private void AddNavTab(CuiElementContainer elements, string section, string label, string anchorMin, string anchorMax, bool active)
        {
            string color = active ? "0.3 0.5 0.3 1" : "0.25 0.25 0.25 1";
            elements.Add(new CuiButton
            {
                Button = { Color = color, Command = $"hoodwars.admin section {section}" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleCenter }
            }, AdminUIName);
        }

        private void AddMainMenuContent(CuiElementContainer elements)
        {
            elements.Add(new CuiLabel
            {
                Text = { Text = "Welcome to HoodWars Admin Panel", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.8", AnchorMax = "1 0.95" }
            }, "Content");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Use the tabs above to configure different aspects of the plugin:\n\n" +
                               "• <color=#55ff55>HQ Settings</color> - Configure safezone options and warnings\n" +
                               "• <color=#55ff55>Neighborhoods</color> - Set HQ centers and radius for each gang\n" +
                               "• <color=#55ff55>Hotel Items</color> - Manage allowed items in hotel rooms\n" +
                               "• <color=#55ff55>General</color> - Configure reveal duration, proximity, bounty\n\n" +
                               "Tip: Stand at your desired HQ location and use 'Set to My Position' to configure HQ centers.",
                        FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.2", AnchorMax = "0.95 0.75" }
            }, "Content");

            // Quick stats
            int totalPlayers = _storedData?.Players?.Count ?? 0;
            int totalHQs = _hqToolCupboards?.Count ?? 0;
            bool safezones = _config?.HQ?.EnableHQSafezones ?? false;
            elements.Add(new CuiLabel
            {
                Text = { Text = $"Players: {totalPlayers} | Active HQ TCs: {totalHQs}/4 | Safezones: {(safezones ? "ON" : "OFF")}", 
                        FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0 0.05", AnchorMax = "1 0.15" }
            }, "Content");
        }

        private void AddHQSettingsContent(CuiElementContainer elements)
        {
            float y = 0.85f;
            float rowHeight = 0.12f;

            if (_config == null || _config.HQ == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Configuration not loaded", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.3 0.3 1" },
                    RectTransform = { AnchorMin = "0 0.4", AnchorMax = "1 0.6" }
                }, "Content");
                return;
            }

            // Enable Safezones toggle
            AddSettingRow(elements, ref y, rowHeight, "HQ Safezones", 
                _config.HQ.EnableHQSafezones ? "ENABLED" : "DISABLED",
                _config.HQ.EnableHQSafezones ? "0.3 0.6 0.3 1" : "0.6 0.3 0.3 1",
                "hoodwars.admin togglesafezone");

            // Show HQ Sphere Markers toggle
            AddSettingRow(elements, ref y, rowHeight, "Show HQ Sphere Markers", 
                _config.HQ.ShowHQSpheres ? "VISIBLE" : "HIDDEN",
                _config.HQ.ShowHQSpheres ? "0.3 0.6 0.3 1" : "0.6 0.3 0.3 1",
                "hoodwars.admin togglehqspheres");

            // Trespass Warning Interval
            AddSettingRowWithInput(elements, ref y, rowHeight, "Trespass Warning Interval",
                $"{_config.HQ.TrespassWarningInterval}s",
                "hoodwars.admin setwarninginterval");

            // Info text
            elements.Add(new CuiLabel
            {
                Text = { Text = "When safezones are enabled:\n" +
                               "• No damage can be dealt inside HQ radius\n" +
                               "• Only gang members can build (hotel items only)\n" +
                               "• HQ TC is indestructible (deposit only)\n" +
                               "• Rivals receive trespass warnings\n" +
                               "• Sphere markers show the HQ zone visually",
                        FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.38" }
            }, "Content");
        }

        private void AddNeighborhoodsContent(CuiElementContainer elements, int page)
        {
            if (_config == null || _config.Neighborhoods == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Configuration not loaded", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.3 0.3 1" },
                    RectTransform = { AnchorMin = "0 0.4", AnchorMax = "1 0.6" }
                }, "Content");
                return;
            }

            // Add help text at top
            elements.Add(new CuiLabel
            {
                Text = { Text = "HQ TC Setup: 1) Stand in the HQ zone 2) Place a TC or stand near an existing one 3) Click 'Set Looked TC' OR place a new TC (auto-registers)", 
                        FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "0.6 0.8 0.6 1" },
                RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.98 0.98" }
            }, "Content");

            int itemsPerPage = 2;
            int totalPages = (int)Math.Ceiling(_config.Neighborhoods.Count / (float)itemsPerPage);
            int startIndex = page * itemsPerPage;

            float y = 0.88f;

            for (int i = startIndex; i < Math.Min(startIndex + itemsPerPage, _config.Neighborhoods.Count); i++)
            {
                var hood = _config.Neighborhoods[i];
                
                // Hood name header
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.2 0.2 0.2 1" },
                    RectTransform = { AnchorMin = $"0.02 {y - 0.38f}", AnchorMax = $"0.98 {y}" }
                }, "Content", $"Hood_{i}");

                Color hoodColor = Color.white;
                if (!string.IsNullOrEmpty(hood.HexColor))
                {
                    ColorUtility.TryParseHtmlString(hood.HexColor, out hoodColor);
                }
                
                elements.Add(new CuiLabel
                {
                    Text = { Text = hood.Name, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = $"{hoodColor.r} {hoodColor.g} {hoodColor.b} 1" },
                    RectTransform = { AnchorMin = "0.02 0.75", AnchorMax = "0.5 0.95" }
                }, $"Hood_{i}");

                // HQ Status
                bool hasTC = _hqToolCupboards.ContainsKey(hood.Type);
                elements.Add(new CuiLabel
                {
                    Text = { Text = $"HQ TC: {(hasTC ? "Registered" : "Not Set")}", FontSize = 11, Align = TextAnchor.MiddleRight, 
                            Color = hasTC ? "0.3 0.8 0.3 1" : "0.8 0.3 0.3 1" },
                    RectTransform = { AnchorMin = "0.5 0.75", AnchorMax = "0.98 0.95" }
                }, $"Hood_{i}");

                // HQ Center
                elements.Add(new CuiLabel
                {
                    Text = { Text = $"HQ Center: ({hood.HQCenterX:F0}, {hood.HQCenterZ:F0})", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = "0.02 0.5", AnchorMax = "0.4 0.7" }
                }, $"Hood_{i}");

                // Set to my position button
                elements.Add(new CuiButton
                {
                    Button = { Color = "0.3 0.4 0.5 1", Command = $"hoodwars.admin sethqcenter {i}" },
                    RectTransform = { AnchorMin = "0.42 0.52", AnchorMax = "0.68 0.68" },
                    Text = { Text = "Set to My Position", FontSize = 10, Align = TextAnchor.MiddleCenter }
                }, $"Hood_{i}");

                // HQ Radius
                elements.Add(new CuiLabel
                {
                    Text = { Text = $"HQ Radius: {hood.HQRadius}m", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = "0.02 0.25", AnchorMax = "0.3 0.45" }
                }, $"Hood_{i}");

                // Radius buttons
                AddRadiusButton(elements, $"Hood_{i}", i, 25, "0.32 0.27", "0.42 0.43");
                AddRadiusButton(elements, $"Hood_{i}", i, 50, "0.44 0.27", "0.54 0.43");
                AddRadiusButton(elements, $"Hood_{i}", i, 75, "0.56 0.27", "0.66 0.43");
                AddRadiusButton(elements, $"Hood_{i}", i, 100, "0.68 0.27", "0.78 0.43");

                // Clear HQ TC button or Set HQ TC button
                if (hasTC)
                {
                    elements.Add(new CuiButton
                    {
                        Button = { Color = "0.6 0.2 0.2 1", Command = $"hoodwars.admin clearhqtc {i}" },
                        RectTransform = { AnchorMin = "0.02 0.05", AnchorMax = "0.25 0.2" },
                        Text = { Text = "Clear HQ TC", FontSize = 10, Align = TextAnchor.MiddleCenter }
                    }, $"Hood_{i}");
                }
                else
                {
                    // Add "Set Looked TC" button for admins to manually set the TC
                    elements.Add(new CuiButton
                    {
                        Button = { Color = "0.3 0.5 0.3 1", Command = $"hoodwars.admin sethqtc {i}" },
                        RectTransform = { AnchorMin = "0.02 0.05", AnchorMax = "0.28 0.2" },
                        Text = { Text = "Set Looked TC", FontSize = 10, Align = TextAnchor.MiddleCenter }
                    }, $"Hood_{i}");
                }

                y -= 0.42f;
            }

            // Pagination
            if (totalPages > 1)
            {
                AddPagination(elements, page, totalPages);
            }
        }

        private void AddRadiusButton(CuiElementContainer elements, string parent, int hoodIndex, int radius, string anchorMin, string anchorMax)
        {
            elements.Add(new CuiButton
            {
                Button = { Color = "0.25 0.35 0.25 1", Command = $"hoodwars.admin sethqradius {hoodIndex} {radius}" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Text = { Text = $"{radius}m", FontSize = 10, Align = TextAnchor.MiddleCenter }
            }, parent);
        }

        private void AddHotelItemsContent(CuiElementContainer elements, int page)
        {
            if (_config == null || _config.HQ == null || _config.HQ.AllowedHotelItems == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Configuration not loaded", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.3 0.3 1" },
                    RectTransform = { AnchorMin = "0 0.4", AnchorMax = "1 0.6" }
                }, "Content");
                return;
            }

            int itemsPerPage = 8;
            int totalItems = _config.HQ.AllowedHotelItems.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (float)itemsPerPage);
            int startIndex = page * itemsPerPage;

            elements.Add(new CuiLabel
            {
                Text = { Text = "Allowed Hotel Items (items players can place in HQ)", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0.88", AnchorMax = "0.98 0.98" }
            }, "Content");

            float y = 0.82f;
            float rowHeight = 0.09f;

            for (int i = startIndex; i < Math.Min(startIndex + itemsPerPage, totalItems); i++)
            {
                string item = _config.HQ.AllowedHotelItems[i];
                
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.2 0.2 0.2 0.8" },
                    RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.98 {y}" }
                }, "Content", $"Item_{i}");

                elements.Add(new CuiLabel
                {
                    Text = { Text = item, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                    RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.8 1" }
                }, $"Item_{i}");

                elements.Add(new CuiButton
                {
                    Button = { Color = "0.6 0.2 0.2 1", Command = $"hoodwars.admin removehotelitem {i}" },
                    RectTransform = { AnchorMin = "0.85 0.15", AnchorMax = "0.98 0.85" },
                    Text = { Text = "Remove", FontSize = 10, Align = TextAnchor.MiddleCenter }
                }, $"Item_{i}");

                y -= rowHeight + 0.01f;
            }

            // Add item info
            elements.Add(new CuiLabel
            {
                Text = { Text = "To add items, use chat: /hoodadmin additem <prefab_name>", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.6 1" },
                RectTransform = { AnchorMin = "0 0.02", AnchorMax = "1 0.08" }
            }, "Content");

            // Pagination
            if (totalPages > 1)
            {
                AddPagination(elements, page, totalPages);
            }
        }

        private void AddGeneralSettingsContent(CuiElementContainer elements)
        {
            if (_config == null || _config.General == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Configuration not loaded", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.3 0.3 1" },
                    RectTransform = { AnchorMin = "0 0.4", AnchorMax = "1 0.6" }
                }, "Content");
                return;
            }

            float y = 0.88f;
            float rowHeight = 0.12f;

            // Reveal Duration
            AddSettingRowWithInput(elements, ref y, rowHeight, "Identity Reveal Duration",
                $"{_config.General.RevealDuration}s",
                "hoodwars.admin setrevealduration");

            // Proximity Distance
            AddSettingRowWithInput(elements, ref y, rowHeight, "Proximity Reveal Distance",
                $"{_config.General.ProximityDistance}m",
                "hoodwars.admin setproximitydist");

            // Kill Reveal toggle
            AddSettingRow(elements, ref y, rowHeight, "Kill Reveal",
                _config.General.KillReveal ? "ENABLED" : "DISABLED",
                _config.General.KillReveal ? "0.3 0.6 0.3 1" : "0.6 0.3 0.3 1",
                "hoodwars.admin togglekillreveal");

            // Bounty Amount
            AddSettingRowWithInput(elements, ref y, rowHeight, "Rat TC Bounty Amount",
                $"{_config.General.BountyAmount} scrap",
                "hoodwars.admin setbounty");

            // Reputation toggle
            elements.Add(new CuiLabel
            {
                Text = { Text = $"Reputation System: {(_config.General.UseReputation ? "ENABLED" : "DISABLED")}", 
                        FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.02 0.1", AnchorMax = "0.98 0.22" }
            }, "Content");
        }

        private void AddTestingContent(CuiElementContainer elements, BasePlayer player)
        {
            elements.Add(new CuiLabel
            {
                Text = { Text = "Admin Testing Tools", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0.2 1" },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.98" }
            }, "Content");

            float y = 0.82f;
            float rowHeight = 0.1f;
            float spacing = 0.02f;

            // Current location status
            var hqHood = GetHQAtPosition(player.transform.position);
            var playerInfo = GetPlayerData(player.userID);
            string locationStatus = hqHood != null 
                ? $"In <color={hqHood.HexColor}>{hqHood.Name}</color> HQ" 
                : "Not in any HQ zone";

            elements.Add(new CuiLabel
            {
                Text = { Text = $"Current Location: {locationStatus} | Your Gang: {playerInfo.HomeHood}", 
                        FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.02 0.72", AnchorMax = "0.98 0.82" }
            }, "Content");

            y = 0.68f;

            // Test Safezone button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.3 0.5 0.6 1", Command = "hoodwars.admin testsafezone" },
                RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.48 {y}" },
                Text = { Text = "Test Safezone Status", FontSize = 11, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Test Trespass button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.2 1", Command = "hoodwars.admin testtrespass" },
                RectTransform = { AnchorMin = $"0.52 {y - rowHeight}", AnchorMax = $"0.98 {y}" },
                Text = { Text = "Test Trespass Warning", FontSize = 11, Align = TextAnchor.MiddleCenter }
            }, "Content");

            y -= rowHeight + spacing;

            // Spawn Door button (requires ManualDoor)
            bool manualDoorLoaded = ManualDoor != null && ManualDoor.IsLoaded;
            elements.Add(new CuiButton
            {
                Button = { Color = manualDoorLoaded ? "0.3 0.6 0.3 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin spawndoor" },
                RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.24 {y}" },
                Text = { Text = manualDoorLoaded ? "Spawn Door" : "ManualDoor N/A", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Spawn Double Door button (requires ManualDoor)
            elements.Add(new CuiButton
            {
                Button = { Color = manualDoorLoaded ? "0.3 0.5 0.6 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin spawndoubledoor" },
                RectTransform = { AnchorMin = $"0.26 {y - rowHeight}", AnchorMax = $"0.48 {y}" },
                Text = { Text = manualDoorLoaded ? "Spawn Double" : "ManualDoor N/A", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Spawn HQ TC button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.2 0.5 0.7 1", Command = "hoodwars.admin spawntc" },
                RectTransform = { AnchorMin = $"0.50 {y - rowHeight}", AnchorMax = $"0.72 {y}" },
                Text = { Text = "Spawn HQ TC", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Door Info button
            elements.Add(new CuiButton
            {
                Button = { Color = manualDoorLoaded ? "0.4 0.5 0.4 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin doorinfo" },
                RectTransform = { AnchorMin = $"0.74 {y - rowHeight}", AnchorMax = $"0.98 {y}" },
                Text = { Text = "Door Info", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            y -= rowHeight + spacing;

            // Reset Door button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.6 0.3 0.3 1", Command = "hoodwars.admin resetdoor" },
                RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.32 {y}" },
                Text = { Text = "Reset Door (Evict)", FontSize = 10, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Force Expire Timer button (for testing eviction)
            elements.Add(new CuiButton
            {
                Button = { Color = manualDoorLoaded ? "0.7 0.5 0.2 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin forceexpire" },
                RectTransform = { AnchorMin = $"0.34 {y - rowHeight}", AnchorMax = $"0.64 {y}" },
                Text = { Text = "Force Expire Timer", FontSize = 10, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // List Hotel Doors button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.5 1", Command = "hoodwars.admin listdoors" },
                RectTransform = { AnchorMin = $"0.66 {y - rowHeight}", AnchorMax = $"0.98 {y}" },
                Text = { Text = "List Nearby Doors", FontSize = 10, Align = TextAnchor.MiddleCenter }
            }, "Content");

            y -= rowHeight + spacing;

            // Gang TC Reset buttons
            elements.Add(new CuiLabel
            {
                Text = { Text = "--- Reset Gang HQ TC Registration ---", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.02 0.26", AnchorMax = "0.98 0.34" }
            }, "Content");

            // Four gang reset buttons
            float buttonWidth = 0.23f;
            float buttonStart = 0.02f;
            string[] gangs = { "West", "North", "South", "East" };
            string[] colors = { "0.7 0.3 0.3 1", "0.7 0.7 0.2 1", "0.3 0.3 0.7 1", "0.4 0.4 0.4 1" };

            for (int i = 0; i < 4; i++)
            {
                float xMin = buttonStart + i * (buttonWidth + 0.01f);
                float xMax = xMin + buttonWidth;
                elements.Add(new CuiButton
                {
                    Button = { Color = colors[i], Command = $"hoodwars.admin resetgangtc {gangs[i].ToLower()}" },
                    RectTransform = { AnchorMin = $"{xMin} 0.15", AnchorMax = $"{xMax} 0.24" },
                    Text = { Text = $"Reset {gangs[i]}", FontSize = 10, Align = TextAnchor.MiddleCenter }
                }, "Content");
            }

            // GangKits test button (if GangKits plugin is loaded)
            bool gangKitsLoaded = GangKits != null && GangKits.IsLoaded;
            elements.Add(new CuiButton
            {
                Button = { Color = gangKitsLoaded ? "0.5 0.3 0.6 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin testallkits" },
                RectTransform = { AnchorMin = "0.02 0.05", AnchorMax = "0.32 0.13" },
                Text = { Text = gangKitsLoaded ? "Test All Gang Kits" : "GangKits N/A", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // Give My Kit button
            elements.Add(new CuiButton
            {
                Button = { Color = gangKitsLoaded ? "0.3 0.5 0.6 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin givemykit" },
                RectTransform = { AnchorMin = "0.34 0.05", AnchorMax = "0.64 0.13" },
                Text = { Text = gangKitsLoaded ? "Give My Gang Kit" : "GangKits N/A", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");

            // DriveBy test button (if DriveBy plugin is loaded)
            bool driveByLoaded = DriveBy != null && DriveBy.IsLoaded;
            elements.Add(new CuiButton
            {
                Button = { Color = driveByLoaded ? "0.6 0.3 0.3 1" : "0.4 0.4 0.4 1", Command = "hoodwars.admin testdriveby" },
                RectTransform = { AnchorMin = "0.66 0.05", AnchorMax = "0.98 0.13" },
                Text = { Text = driveByLoaded ? "Test Drive-By" : "DriveBy N/A", FontSize = 9, Align = TextAnchor.MiddleCenter }
            }, "Content");
        }

        private void AddSettingRow(CuiElementContainer elements, ref float y, float rowHeight, string label, string value, string buttonColor, string command)
        {
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.2 0.2 0.8" },
                RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.98 {y}" }
            }, "Content", $"Setting_{label}");

            elements.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.5 1" }
            }, $"Setting_{label}");

            elements.Add(new CuiButton
            {
                Button = { Color = buttonColor, Command = command },
                RectTransform = { AnchorMin = "0.7 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = value, FontSize = 11, Align = TextAnchor.MiddleCenter }
            }, $"Setting_{label}");

            y -= rowHeight + 0.02f;
        }

        private void AddSettingRowWithInput(CuiElementContainer elements, ref float y, float rowHeight, string label, string currentValue, string command)
        {
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.2 0.2 0.8" },
                RectTransform = { AnchorMin = $"0.02 {y - rowHeight}", AnchorMax = $"0.98 {y}" }
            }, "Content", $"Setting_{label}");

            elements.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.5 1" }
            }, $"Setting_{label}");

            elements.Add(new CuiLabel
            {
                Text = { Text = $"Current: {currentValue}", FontSize = 11, Align = TextAnchor.MiddleRight, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.98 1" }
            }, $"Setting_{label}");

            // Note: Full input fields require more complex CUI implementation
            // Players can use chat commands for now: type the value in chat
            
            y -= rowHeight + 0.02f;
        }

        private void AddPagination(CuiElementContainer elements, int currentPage, int totalPages)
        {
            // Previous button
            if (currentPage > 0)
            {
                elements.Add(new CuiButton
                {
                    Button = { Color = "0.3 0.3 0.4 1", Command = $"hoodwars.admin page {currentPage - 1}" },
                    RectTransform = { AnchorMin = "0.3 0.01", AnchorMax = "0.4 0.06" },
                    Text = { Text = "< Prev", FontSize = 10, Align = TextAnchor.MiddleCenter }
                }, "Content");
            }

            // Page indicator
            elements.Add(new CuiLabel
            {
                Text = { Text = $"Page {currentPage + 1} / {totalPages}", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.42 0.01", AnchorMax = "0.58 0.06" }
            }, "Content");

            // Next button
            if (currentPage < totalPages - 1)
            {
                elements.Add(new CuiButton
                {
                    Button = { Color = "0.3 0.3 0.4 1", Command = $"hoodwars.admin page {currentPage + 1}" },
                    RectTransform = { AnchorMin = "0.6 0.01", AnchorMax = "0.7 0.06" },
                    Text = { Text = "Next >", FontSize = 10, Align = TextAnchor.MiddleCenter }
                }, "Content");
            }
        }

        private void DestroyAdminUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, AdminUIName);
        }

        // Chat command for admin panel and helper commands
        [ChatCommand("hoodadmin")]
        private void CmdHoodAdmin(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, "<color=#ff4444>ACCESS DENIED:</color> You don't have permission.");
                return;
            }

            if (args.Length == 0)
            {
                // Open the GUI
                _adminUIPage[player.userID] = 0;
                _adminUISection[player.userID] = "main";
                ShowAdminUI(player);
                return;
            }

            string action = args[0].ToLower();

            switch (action)
            {
                case "additem":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /hoodadmin additem <prefab_name>");
                        return;
                    }
                    string itemName = args[1];
                    if (!_config.HQ.AllowedHotelItems.Contains(itemName))
                    {
                        _config.HQ.AllowedHotelItems.Add(itemName);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Added '{itemName}' to allowed hotel items.");
                    }
                    else
                    {
                        SendReply(player, $"<color=#ffaa00>INFO:</color> '{itemName}' is already in the list.");
                    }
                    break;

                case "setradius":
                    if (args.Length < 3)
                    {
                        SendReply(player, "Usage: /hoodadmin setradius <gang_index> <radius>");
                        return;
                    }
                    int idx;
                    float rad;
                    if (int.TryParse(args[1], out idx) && float.TryParse(args[2], out rad) && idx >= 0 && idx < _config.Neighborhoods.Count)
                    {
                        _config.Neighborhoods[idx].HQRadius = Math.Max(10f, rad);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Set {_config.Neighborhoods[idx].Name} HQ radius to {rad}m");
                    }
                    break;

                case "setwarning":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /hoodadmin setwarning <seconds>");
                        return;
                    }
                    float warn;
                    if (float.TryParse(args[1], out warn))
                    {
                        _config.HQ.TrespassWarningInterval = Math.Max(5f, warn);
                        SaveConfig();
                        SendReply(player, $"<color=#55ff55>SUCCESS:</color> Trespass warning interval set to {warn}s");
                    }
                    break;

                case "help":
                    SendReply(player, "<color=#55ff55>HoodWars Admin Commands:</color>\n" +
                                     "/hoodadmin - Open admin GUI\n" +
                                     "/hoodadmin additem <prefab> - Add hotel item\n" +
                                     "/hoodadmin setradius <idx> <meters> - Set HQ radius\n" +
                                     "/hoodadmin setwarning <seconds> - Set warning interval\n" +
                                     "/hoodadmin sethqtc <gang_idx> - Set looked TC as gang HQ TC\n" +
                                     "/hoodadmin spawntc - Spawn HQ TC at position\n" +
                                     "/hoodadmin spawndoor - Spawn single hotel door\n" +
                                     "/hoodadmin spawndoubledoor - Spawn double hotel door\n" +
                                     "/hoodadmin testevict - Test evict from nearest door\n" +
                                     "/hoodadmin forceexpire - Force expire door claim (test timer)\n" +
                                     "/hoodadmin doorinfo - Get info on nearest hotel door\n" +
                                     "/hoodadmin resetdoor - Reset nearest door claim");
                    break;

                case "sethqtc":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /hoodadmin sethqtc <gang_index: 0=West, 1=North, 2=South, 3=East>");
                        return;
                    }
                    int tcIdx;
                    if (int.TryParse(args[1], out tcIdx))
                    {
                        SetHQTCFromLook(player, tcIdx);
                    }
                    break;

                case "forceexpire":
                    ForceExpireNearestDoor(player);
                    break;

                case "spawndoor":
                    SpawnHotelDoor(player, false);
                    break;

                case "spawndoubledoor":
                    SpawnHotelDoor(player, true);
                    break;

                case "spawntc":
                    SpawnHQToolCupboard(player);
                    break;

                case "testevict":
                    TestEvictDoor(player);
                    break;

                case "doorinfo":
                    GetHotelDoorInfo(player);
                    break;

                case "resetdoor":
                    ResetHotelDoor(player);
                    break;

                default:
                    SendReply(player, "Unknown command. Use /hoodadmin help for commands.");
                    break;
            }
        }

        #endregion

        #region ManualDoor Integration

        // Check if ManualDoor plugin is loaded and available
        private bool IsManualDoorLoaded()
        {
            return ManualDoor != null && ManualDoor.IsLoaded;
        }

        // Spawn a hotel door at player's position (uses ManualDoor plugin)
        private void SpawnHotelDoor(BasePlayer player, bool isDoubleDoor = false)
        {
            if (!IsManualDoorLoaded())
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> ManualDoor plugin is not loaded. Install ManualDoor.cs to use hotel doors.");
                return;
            }

            // Check if player is in an HQ zone
            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>WARNING:</color> You are not in an HQ zone. Hotel doors should be placed in HQ areas.");
            }

            // Execute ManualDoor's spawndoor or spawndoubledoor command
            if (isDoubleDoor)
            {
                player.SendConsoleCommand("chat.say", "/spawndoubledoor");
                SendReply(player, "<color=#55ff55>SUCCESS:</color> Use the ManualDoor /spawndoubledoor command to place a double hotel door.");
            }
            else
            {
                player.SendConsoleCommand("chat.say", "/spawndoor");
                SendReply(player, "<color=#55ff55>SUCCESS:</color> Use the ManualDoor /spawndoor command to place a single hotel door.");
            }
        }

        // Spawn an HQ Tool Cupboard at player's position
        private void SpawnHQToolCupboard(BasePlayer player)
        {
            // Check if player is in an HQ zone
            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>WARNING:</color> You are not in an HQ zone. Spawning TC anyway, but consider placing it in an HQ area.");
            }

            // Get spawn position (where player is looking at ground, or player position)
            RaycastHit hit;
            Vector3 spawnPos;
            Quaternion spawnRot;

            // Try raycast to get ground position
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
            {
                spawnPos = hit.point + Vector3.up * 0.1f;  // Slightly above hit point
                spawnRot = Quaternion.Euler(0, player.transform.eulerAngles.y + 180, 0);
                SendReply(player, $"<color=#aaaaaa>DEBUG:</color> Raycast hit: {hit.collider?.name ?? "unknown"} at {spawnPos}");
            }
            else
            {
                // Spawn in front of player
                spawnPos = player.transform.position + (player.transform.forward * 1.5f);
                spawnRot = Quaternion.Euler(0, player.transform.eulerAngles.y + 180, 0);
                SendReply(player, $"<color=#aaaaaa>DEBUG:</color> Raycast missed, using player position: {spawnPos}");
            }

            // Create the Tool Cupboard (matching ManualDoor's technique exactly)
            var tc = GameManager.server.CreateEntity(PrefabToolCupboard, spawnPos, spawnRot) as BuildingPrivlidge;
            if (tc == null)
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> Failed to create Tool Cupboard entity.");
                return;
            }
            
            // Set ownership
            tc.OwnerID = player.userID;
            
            // Disable GroundWatch (exactly like ManualDoor does)
            var gw = tc.GetComponent<GroundWatch>();
            if (gw != null) gw.enabled = false;
            
            // Set grounded to prevent self-destruction (exactly like ManualDoor does)
            var stab = tc.GetComponent<StabilityEntity>();
            if (stab != null) stab.grounded = true;
            
            // Disable decay (exactly like ManualDoor does)
            if (tc is DecayEntity de)
                de.decay = null;
            
            // Spawn the entity
            tc.Spawn();
            
            SendReply(player, $"<color=#aaaaaa>DEBUG:</color> TC spawned! net.ID = {tc.net?.ID.Value ?? 0}");

            // Auto-authorize the admin who spawned it
            if (tc.authorizedPlayers != null)
            {
                tc.authorizedPlayers.Add(player.userID);
            }

            // Optionally register as HQ TC if in an HQ zone
            if (hqHood != null)
            {
                var netId = tc.net?.ID ?? default(NetworkableId);
                if (netId.Value != 0)
                {
                    _hqToolCupboards[hqHood.Type] = netId;
                    SendReply(player, $"<color=#55ff55>SUCCESS:</color> Spawned HQ Tool Cupboard and registered for <color={hqHood.HexColor}>{hqHood.Name}</color>.\n" +
                                     $"Position: {spawnPos}\nEntity ID: {netId.Value}");
                }
                else
                {
                    SendReply(player, "<color=#55ff55>SUCCESS:</color> Spawned Tool Cupboard, but could not auto-register (no net ID).");
                }
            }
            else
            {
                SendReply(player, $"<color=#55ff55>SUCCESS:</color> Spawned Tool Cupboard at {spawnPos}.\n" +
                                 "Note: Not in HQ zone - use '/hoodadmin sethqtc <idx>' to register it for a gang.");
            }
        }

        // Test eviction from the nearest hotel door
        private void TestEvictDoor(BasePlayer player)
        {
            if (!IsManualDoorLoaded())
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> ManualDoor plugin is not loaded.");
                return;
            }

            // Find nearest door entity
            var nearestDoor = FindNearestHotelDoor(player.transform.position, 5f);
            if (nearestDoor == null)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> No hotel door found within 5m. Look at a door and try again.");
                return;
            }

            // Call ManualDoor to reset/evict
            player.SendConsoleCommand("chat.say", "/resetdoor");
            SendReply(player, "<color=#55ff55>EVICTION TEST:</color> Attempting to reset the nearby door claim. The previous claimant will be evicted.");
        }

        // Get info about nearest hotel door
        private void GetHotelDoorInfo(BasePlayer player)
        {
            if (!IsManualDoorLoaded())
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> ManualDoor plugin is not loaded.");
                return;
            }

            // Execute ManualDoor's doorinfo command
            player.SendConsoleCommand("chat.say", "/doorinfo");
        }

        // Reset the nearest hotel door claim
        private void ResetHotelDoor(BasePlayer player)
        {
            if (!IsManualDoorLoaded())
            {
                SendReply(player, "<color=#ff4444>ERROR:</color> ManualDoor plugin is not loaded.");
                return;
            }

            // Execute ManualDoor's resetdoor command
            player.SendConsoleCommand("chat.say", "/resetdoor");
            SendReply(player, "<color=#55ff55>SUCCESS:</color> Door claim has been reset.");
        }

        // Find the nearest hotel door entity
        private BaseEntity FindNearestHotelDoor(Vector3 pos, float maxDistance)
        {
            BaseEntity nearest = null;
            float nearestDist = maxDistance;

            // Find all doors within range
            var entities = BaseNetworkable.serverEntities.OfType<Door>();
            foreach (var door in entities)
            {
                if (door == null) continue;
                float dist = Vector3.Distance(door.transform.position, pos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = door;
                }
            }

            return nearest;
        }

        // Hook into ManualDoor's door claiming to restrict based on gang
        private object CanClaimHotelDoor(BasePlayer player, BaseEntity door)
        {
            if (player == null || door == null || _config == null || _config.HQ == null) return null;
            if (!_config.HQ.EnableHQSafezones) return null;

            var hqHood = GetHQAtPosition(door.transform.position);
            if (hqHood == null) return null; // Not in HQ, allow normal claiming

            var playerInfo = GetPlayerData(player.userID);

            // Only gang members can claim doors in their own HQ
            if (playerInfo.HomeHood != hqHood.Type)
            {
                SendReply(player, GetMsg("HQ_NotYourHQ", player.UserIDString));
                return false; // Block claim
            }

            return null; // Allow claim
        }

        // Called by ManualDoor when a player tries to claim a door (API hook)
        private object OnDoorClaim(BasePlayer player, ulong doorNetId)
        {
            // Find the door entity
            var door = BaseNetworkable.serverEntities.FirstOrDefault(e => e.net?.ID.Value == doorNetId) as BaseEntity;
            if (door == null) return null;

            return CanClaimHotelDoor(player, door);
        }

        // API method for ManualDoor to check if player can claim in this location
        private bool API_CanPlayerClaimInHQ(BasePlayer player, Vector3 position)
        {
            if (_config == null || _config.HQ == null || !_config.HQ.EnableHQSafezones) return true;

            var hqHood = GetHQAtPosition(position);
            if (hqHood == null) return true; // Not in HQ

            var playerInfo = GetPlayerData(player.userID);
            return playerInfo.HomeHood == hqHood.Type;
        }

        // API method to get the gang name for a position
        private string API_GetHQGangName(Vector3 position)
        {
            var hqHood = GetHQAtPosition(position);
            return hqHood?.Name ?? "Neutral";
        }

        // API method to get a player's gang name by user ID (used by GangKits)
        private string GetPlayerGangName(ulong playerId)
        {
            if (_config == null || _storedData == null) return "Neutral";
            
            var playerInfo = GetPlayerData(playerId);
            if (playerInfo.HomeHood == NeighborhoodType.Neutral)
                return "Neutral";
            
            var hoodConfig = GetNeighborhoodConfig(playerInfo.HomeHood);
            return hoodConfig?.Name ?? "Neutral";
        }

        // API method to get neighborhood name at position (used by DriveBy)
        // This returns the quadrant-based territory name, not HQ zone
        private string GetNeighborhoodNameAt(Vector3 position)
        {
            if (_config == null || _config.Neighborhoods == null) return "Neutral";
            
            var hood = GetNeighborhoodAt(position);
            return hood?.Name ?? "Neutral";
        }

        // API wrapper for external plugins to get player gang name
        private string API_GetPlayerGangName(ulong playerId)
        {
            return GetPlayerGangName(playerId);
        }

        #endregion

        #region Admin Testing Features

        // Test safezone functionality
        [ChatCommand("testsafezone")]
        private void CmdTestSafezone(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, "<color=#ff4444>ACCESS DENIED:</color> Admin only.");
                return;
            }

            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>SAFEZONE TEST:</color> You are NOT in any HQ safezone.");
                return;
            }

            var playerInfo = GetPlayerData(player.userID);
            bool isOwner = playerInfo.HomeHood == hqHood.Type;

            SendReply(player, $"<color=#55ff55>SAFEZONE TEST:</color>\n" +
                             $"HQ Zone: <color={hqHood.HexColor}>{hqHood.Name}</color>\n" +
                             $"Your Gang: {playerInfo.HomeHood}\n" +
                             $"Is Your HQ: {(isOwner ? "<color=#55ff55>YES</color>" : "<color=#ff4444>NO</color>")}\n" +
                             $"Can Build: {(isOwner ? "<color=#55ff55>Hotel items only</color>" : "<color=#ff4444>NO</color>")}\n" +
                             $"Can Take Damage: <color=#55ff55>NO (Protected)</color>\n" +
                             $"Safezones Active: {(_config.HQ.EnableHQSafezones ? "<color=#55ff55>YES</color>" : "<color=#ff4444>NO</color>")}");
        }

        // Test trespass warning
        [ChatCommand("testtrespass")]
        private void CmdTestTrespass(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, "<color=#ff4444>ACCESS DENIED:</color> Admin only.");
                return;
            }

            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>TRESPASS TEST:</color> You are NOT in any HQ zone.");
                return;
            }

            // Force send trespass warning regardless of cooldown
            var playerInfo = GetPlayerData(player.userID);
            if (playerInfo.HomeHood == hqHood.Type)
            {
                SendReply(player, GetMsg("HQ_Safezone", player.UserIDString, hqHood.Name));
            }
            else
            {
                SendReply(player, GetMsg("HQ_Trespass", player.UserIDString, hqHood.HexColor, hqHood.Name));
            }
        }

        // List all hotel doors in current HQ
        [ChatCommand("listhoteldoors")]
        private void CmdListHotelDoors(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, "<color=#ff4444>ACCESS DENIED:</color> Admin only.");
                return;
            }

            var hqHood = GetHQAtPosition(player.transform.position);
            if (hqHood == null)
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> You are not in an HQ zone. Stand in an HQ area to list doors.");
                return;
            }

            // Find all doors in this HQ zone
            var doorsInHQ = BaseNetworkable.serverEntities.OfType<Door>()
                .Where(d => d != null && GetHQAtPosition(d.transform.position)?.Type == hqHood.Type)
                .Take(20)
                .ToList();

            SendReply(player, $"<color=#55ff55>HOTEL DOORS IN {hqHood.Name.ToUpper()}:</color>");
            
            if (doorsInHQ.Count == 0)
            {
                SendReply(player, "No doors found in this HQ zone.");
                return;
            }

            int count = 0;
            foreach (var door in doorsInHQ)
            {
                count++;
                float dist = Vector3.Distance(player.transform.position, door.transform.position);
                var codeLock = door.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
                bool hasLock = codeLock != null;
                bool isLocked = hasLock && codeLock.IsLocked();

                SendReply(player, $"{count}. Distance: {dist:F1}m | Lock: {(hasLock ? (isLocked ? "Locked" : "Unlocked") : "None")} | ID: {door.net?.ID.Value}");
            }
        }

        // Force claim reset for a gang's HQ TC
        [ChatCommand("resetgangtc")]
        private void CmdResetGangTC(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, "<color=#ff4444>ACCESS DENIED:</color> Admin only.");
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, "Usage: /resetgangtc <gang_type: west/north/south/east>");
                return;
            }

            NeighborhoodType type;
            switch (args[0].ToLower())
            {
                case "west":
                    type = NeighborhoodType.West;
                    break;
                case "north":
                    type = NeighborhoodType.North;
                    break;
                case "south":
                    type = NeighborhoodType.South;
                    break;
                case "east":
                    type = NeighborhoodType.East;
                    break;
                default:
                    SendReply(player, "<color=#ff4444>ERROR:</color> Invalid gang type. Use: west, north, south, east");
                    return;
            }

            if (_hqToolCupboards.Remove(type))
            {
                var hood = GetNeighborhoodConfig(type);
                SendReply(player, $"<color=#55ff55>SUCCESS:</color> HQ TC registration cleared for {hood?.Name ?? type.ToString()}. A new TC can now be placed in their HQ.");
            }
            else
            {
                SendReply(player, "<color=#ffaa00>INFO:</color> No HQ TC was registered for that gang.");
            }
        }

        #endregion
    }
}