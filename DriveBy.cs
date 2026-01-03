using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "1.4.0")]
    [Description("Smart AI drive-bys with terrain navigation, dismount attacks. Spawns from territory borders.")]
    public class DriveBy : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars;

        private enum DriveByPhase
        {
            DrivingToTarget,    // Driving towards target
            Dismounted,          // NPCs dismounted and shooting
            DrivingAway          // Getting back in car and leaving
        }

        private class DriveByEvent
        {
            public BaseVehicle Vehicle;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public Vector3 TargetPosition;        // Where the target was when event started
            public Vector3 ExitPosition;          // Where to drive off to (map edge)
            public ulong TargetID;
            public string GangOwner;
            public DriveByPhase Phase = DriveByPhase.DrivingToTarget;
            public float DismountTimer;           // Time NPCs stay dismounted
            public float StuckTimer;              // To detect if vehicle is stuck
            public Vector3 LastPosition;          // For stuck detection
        }

        private List<DriveByEvent> _activeEvents = new List<DriveByEvent>();
        private Timer _eventTimer;

        private const string PrefabSedan = "assets/content/vehicles/sedan_a/sedan_b.prefab";
        private const string PrefabScientist = "assets/prefabs/npc/scientist/scientistnpc_roaming.prefab";
        private const string PermAdmin = "hoodwars.admin";
        
        // Dismount settings
        private const float DismountDistance = 25f;      // Distance from target to dismount
        private const float DismountDuration = 8f;       // How long NPCs stay out shooting
        private const float StuckThreshold = 2f;         // Time before considering vehicle stuck
        private const float ObstacleCheckDistance = 5f;  // Raycast distance for obstacles

        #region Configuration

        private class GangVisuals
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon = "pistol.semiauto";
            public ulong WeaponSkin = 0;
        }

        private Dictionary<string, GangVisuals> _gangKits = new Dictionary<string, GangVisuals>();

        protected override void LoadDefaultConfig()
        {
            Config["Settings"] = new Dictionary<string, object>
            {
                ["Detection Interval (Seconds)"] = 30,
                ["Cooldown per Player (Minutes)"] = 10,
                ["Vehicle Speed"] = 12f,
                ["NPC Accuracy (0.0 to 1.0)"] = 0.65f,
                ["Dismount Distance"] = 25f,
                ["Dismount Duration (Seconds)"] = 8f,
                ["Spawn Distance From Border"] = 50f
            };

            Config["Visuals"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> { ["hoodie"] = 3637124708, ["pants"] = 3637161289, ["mask.balaclava"] = 3637136628 }
                },
                ["Northside Vagos"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> { ["hoodie"] = 3637132959, ["pants"] = 3637162032, ["mask.bandana"] = 3637144551 }
                },
                ["Southside Sureños"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> { ["hoodie"] = 3637133781, ["pants"] = 3637162360, ["mask.balaclava"] = 3637136303 }
                },
                ["Eastside Disciples"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> { ["hoodie"] = 3637126631, ["pants"] = 3637163268, ["mask.bandana"] = 3637149926 }
                }
            };
            
            // Territory border offsets - where vehicles spawn relative to territory center
            // Positive values = towards map edge, vehicles drive inward
            Config["Border Spawns"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = "west",      // Spawns from west edge
                ["Northside Vagos"] = "north",    // Spawns from north edge
                ["Southside Sureños"] = "south",  // Spawns from south edge
                ["Eastside Disciples"] = "east"   // Spawns from east edge
            };

            SaveConfig();
        }

        private void Init()
        {
            var visualData = Config["Visuals"] as Dictionary<string, object>;
            if (visualData != null)
            {
                foreach (var kvp in visualData)
                {
                    var data = kvp.Value as Dictionary<string, object>;
                    _gangKits[kvp.Key] = new GangVisuals
                    {
                        Clothing = (data["Clothing"] as List<object>).Select(x => x.ToString()).ToList(),
                        Skins = (data["Skins"] as Dictionary<string, object>).ToDictionary(x => x.Key, x => ulong.Parse(x.Value.ToString()))
                    };
                }
            }
            
            permission.RegisterPermission(PermAdmin, this);
        }

        #endregion

        #region Core Loop

        private void OnServerInitialized()
        {
            // Smooth 10 FPS drive-by updates
            _eventTimer = timer.Every(0.1f, UpdateActiveDriveBys);
            
            var settings = Config["Settings"] as Dictionary<string, object>;
            float interval = settings != null ? Convert.ToSingle(settings["Detection Interval (Seconds)"]) : 30f;
            
            timer.Every(interval, CheckForIntruders);
        }

        private void Unload()
        {
            _eventTimer?.Destroy();
            foreach (var ev in _activeEvents) CleanUpEvent(ev);
        }

        private void CheckForIntruders()
        {
            if (HoodWars == null) return;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsSleeping() || !player.IsAlive() || player.IsAdmin) continue;

                // Uses the GetNeighborhoodNameAt string-return hook to prevent InvalidCastException
                string currentZone = HoodWars.Call<string>("GetNeighborhoodNameAt", player.transform.position) ?? "Neutral";
                string homeGang = HoodWars.Call<string>("GetPlayerGangName", player.userID) ?? "Neutral";

                if (currentZone != "Neutral" && homeGang != "Neutral" && currentZone != homeGang)
                {
                    if (!IsOnCooldown(player.userID)) StartDriveBy(player, currentZone);
                }
            }
        }

        #endregion

        #region Event Management

        private void StartDriveBy(BasePlayer target, string territoryGang)
        {
            // Get spawn position from territory border
            Vector3 spawnPos = GetBorderSpawnPosition(territoryGang, target.transform.position);
            if (spawnPos == Vector3.zero)
            {
                Puts($"[DriveBy] Could not find valid spawn for {territoryGang}");
                return;
            }
            
            // Calculate exit position (opposite side from spawn, towards map edge)
            Vector3 exitPos = GetExitPosition(territoryGang, target.transform.position);
            
            // Get proper ground height for spawn
            spawnPos = GetGroundPosition(spawnPos);
            
            // Face towards target
            Quaternion rotation = Quaternion.LookRotation((target.transform.position - spawnPos).normalized);
            
            BaseVehicle vehicle = GameManager.server.CreateEntity(PrefabSedan, spawnPos, rotation) as BaseVehicle;
            if (vehicle == null) return;

            vehicle.Spawn();
            
            // Fill with fuel - find fuel container and add low grade fuel
            var fuelSystem = vehicle.GetFuelSystem();
            if (fuelSystem != null)
            {
                var fuelContainer = fuelSystem.GetFuelContainer();
                if (fuelContainer?.inventory != null)
                {
                    var fuelItem = ItemManager.CreateByName("lowgradefuel", 100);
                    if (fuelItem != null && !fuelItem.MoveToContainer(fuelContainer.inventory))
                    {
                        fuelItem.Remove();
                    }
                }
            }
            
            DriveByEvent ev = new DriveByEvent
            {
                Vehicle = vehicle,
                TargetPosition = target.transform.position,
                ExitPosition = exitPos,
                TargetID = target.userID,
                GangOwner = territoryGang,
                Phase = DriveByPhase.DrivingToTarget,
                LastPosition = spawnPos
            };

            // seatIndex 0: Driver, 1: Front Passenger, 2: Rear Left
            SpawnGangNPC(ev, 0, true); 
            SpawnGangNPC(ev, 1, false); 
            SpawnGangNPC(ev, 2, false); 

            _activeEvents.Add(ev);
            SetCooldown(target.userID);
            
            PrintToChat($"<color=#ff4444>[STREET NEWS]</color> Drive-by in progress in {territoryGang}! Rivals repping on site.");
        }
        
        private Vector3 GetBorderSpawnPosition(string gang, Vector3 targetPos)
        {
            var borderConfig = Config["Border Spawns"] as Dictionary<string, object>;
            var settings = Config["Settings"] as Dictionary<string, object>;
            float spawnDist = settings != null && settings.ContainsKey("Spawn Distance From Border") 
                ? Convert.ToSingle(settings["Spawn Distance From Border"]) : 50f;
            
            string direction = "west";
            if (borderConfig != null && borderConfig.ContainsKey(gang))
                direction = borderConfig[gang].ToString().ToLower();
            
            // Get world size for map boundaries
            float worldSize = TerrainMeta.Size.x / 2f;
            Vector3 spawnPos = targetPos;
            
            switch (direction)
            {
                case "west":
                    spawnPos = new Vector3(-worldSize + spawnDist, 0, targetPos.z);
                    break;
                case "east":
                    spawnPos = new Vector3(worldSize - spawnDist, 0, targetPos.z);
                    break;
                case "north":
                    spawnPos = new Vector3(targetPos.x, 0, worldSize - spawnDist);
                    break;
                case "south":
                    spawnPos = new Vector3(targetPos.x, 0, -worldSize + spawnDist);
                    break;
            }
            
            return spawnPos;
        }
        
        private Vector3 GetExitPosition(string gang, Vector3 targetPos)
        {
            var borderConfig = Config["Border Spawns"] as Dictionary<string, object>;
            string direction = "west";
            if (borderConfig != null && borderConfig.ContainsKey(gang))
                direction = borderConfig[gang].ToString().ToLower();
            
            float worldSize = TerrainMeta.Size.x / 2f;
            
            // Exit opposite from where we spawned
            switch (direction)
            {
                case "west":
                    return new Vector3(worldSize - 30f, 0, targetPos.z);
                case "east":
                    return new Vector3(-worldSize + 30f, 0, targetPos.z);
                case "north":
                    return new Vector3(targetPos.x, 0, -worldSize + 30f);
                case "south":
                    return new Vector3(targetPos.x, 0, worldSize - 30f);
            }
            return targetPos + (Vector3.forward * 200f);
        }
        
        private Vector3 GetGroundPosition(Vector3 pos)
        {
            RaycastHit hit;
            pos.y = 500f; // Start high
            if (Physics.Raycast(pos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain", "World", "Default")))
            {
                return hit.point + Vector3.up * 0.5f; // Slight offset above ground
            }
            return pos;
        }

        private void SpawnGangNPC(DriveByEvent ev, int seatIndex, bool isDriver)
        {
            ScientistNPC npc = GameManager.server.CreateEntity(PrefabScientist, ev.Vehicle.transform.position, Quaternion.identity) as ScientistNPC;
            if (npc == null) return;

            npc.Spawn();
            npc.inventory.Strip();

            if (_gangKits.TryGetValue(ev.GangOwner, out var kit))
            {
                foreach (var itemShort in kit.Clothing)
                {
                    ulong skin = kit.Skins.ContainsKey(itemShort) ? kit.Skins[itemShort] : 0;
                    npc.inventory.GiveItem(ItemManager.CreateByName(itemShort, 1, skin), npc.inventory.containerWear);
                }
                
                Item weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                npc.inventory.GiveItem(weapon, npc.inventory.containerBelt);
                npc.UpdateActiveItem(weapon.uid);
            }

            // Mounting logic for sedan_b
            if (ev.Vehicle.mountPoints != null && seatIndex < ev.Vehicle.mountPoints.Count)
            {
                BaseMountable mountable = ev.Vehicle.mountPoints[seatIndex].mountable;
                if (mountable != null) mountable.MountPlayer(npc);
            }
            
            if (!isDriver)
            {
                npc.Brain.SetEnabled(true);
                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target != null)
                {
                    // Update: Fixed compilation error by removing SetFact.
                    // Sensory injection is the most reliable way to trigger combat in modern builds.
                    // The AI's internal update cycle will automatically pick up the 'Known' target and engage.
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
            }
            else
            {
                npc.Brain.SetEnabled(false); 
            }

            ev.Shooters.Add(npc);
        }

        private void UpdateActiveDriveBys()
        {
            var settings = Config["Settings"] as Dictionary<string, object>;
            float speed = settings != null && settings.ContainsKey("Vehicle Speed") ? Convert.ToSingle(settings["Vehicle Speed"]) : 12f;
            float dismountDist = settings != null && settings.ContainsKey("Dismount Distance") ? Convert.ToSingle(settings["Dismount Distance"]) : 25f;
            float dismountDuration = settings != null && settings.ContainsKey("Dismount Duration (Seconds)") ? Convert.ToSingle(settings["Dismount Duration (Seconds)"]) : 8f;
            
            for (int i = _activeEvents.Count - 1; i >= 0; i--)
            {
                var ev = _activeEvents[i];
                if (ev?.Vehicle == null || ev.Vehicle.IsDestroyed)
                {
                    CleanUpEvent(ev);
                    _activeEvents.RemoveAt(i);
                    continue;
                }

                switch (ev.Phase)
                {
                    case DriveByPhase.DrivingToTarget:
                        UpdateDriveToTarget(ev, speed, dismountDist);
                        break;
                        
                    case DriveByPhase.Dismounted:
                        UpdateDismounted(ev, dismountDuration);
                        break;
                        
                    case DriveByPhase.DrivingAway:
                        UpdateDriveAway(ev, speed);
                        if (ev.Phase == DriveByPhase.DrivingAway && 
                            Vector3.Distance(ev.Vehicle.transform.position, ev.ExitPosition) < 30f)
                        {
                            CleanUpEvent(ev);
                            _activeEvents.RemoveAt(i);
                        }
                        break;
                }
            }
        }
        
        private void UpdateDriveToTarget(DriveByEvent ev, float speed, float dismountDist)
        {
            // Update target position if target is still alive
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null && target.IsAlive())
                ev.TargetPosition = target.transform.position;
            
            float distToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
            
            // Close enough to dismount?
            if (distToTarget <= dismountDist)
            {
                DismountNPCs(ev);
                ev.Phase = DriveByPhase.Dismounted;
                ev.DismountTimer = Time.realtimeSinceStartup;
                return;
            }
            
            // Smart driving towards target
            SmartDrive(ev, ev.TargetPosition, speed);
        }
        
        private void UpdateDismounted(DriveByEvent ev, float dismountDuration)
        {
            // Keep NPCs targeting the player
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && !npc.IsMounted() && target != null && target.IsAlive())
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
            }
            
            // Time to get back in the car?
            if (Time.realtimeSinceStartup - ev.DismountTimer >= dismountDuration)
            {
                RemountNPCs(ev);
                ev.Phase = DriveByPhase.DrivingAway;
                ev.ExitPosition = GetGroundPosition(ev.ExitPosition);
            }
        }
        
        private void UpdateDriveAway(DriveByEvent ev, float speed)
        {
            SmartDrive(ev, ev.ExitPosition, speed * 1.2f); // Drive faster when leaving
        }
        
        private void SmartDrive(DriveByEvent ev, Vector3 destination, float speed)
        {
            Vector3 currentPos = ev.Vehicle.transform.position;
            Vector3 direction = (destination - currentPos).normalized;
            direction.y = 0; // Keep horizontal
            
            // Check for obstacles ahead
            Vector3 avoidanceDir = GetAvoidanceDirection(currentPos, direction);
            if (avoidanceDir != Vector3.zero)
                direction = Vector3.Lerp(direction, avoidanceDir, 0.7f).normalized;
            
            // Get ground position for next move
            Vector3 nextPos = currentPos + (direction * speed * 0.1f);
            nextPos = GetGroundPosition(nextPos);
            
            // Ensure we stay on ground (not in water/air)
            if (nextPos.y < WaterSystem.OceanLevel + 1f)
                nextPos.y = WaterSystem.OceanLevel + 1f;
            
            // Smooth movement
            ev.Vehicle.transform.position = Vector3.Lerp(currentPos, nextPos, 0.5f);
            
            // Smooth rotation
            if (direction != Vector3.zero)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                ev.Vehicle.transform.rotation = Quaternion.Slerp(ev.Vehicle.transform.rotation, targetRotation, 0.1f);
            }
            
            ev.Vehicle.SendNetworkUpdate();
            
            // Stuck detection
            if (Vector3.Distance(currentPos, ev.LastPosition) < 0.1f)
            {
                ev.StuckTimer += 0.1f;
                if (ev.StuckTimer > StuckThreshold)
                {
                    // Teleport slightly forward and to the side
                    Vector3 unstuck = currentPos + (ev.Vehicle.transform.right * 3f) + (direction * 3f);
                    ev.Vehicle.transform.position = GetGroundPosition(unstuck);
                    ev.StuckTimer = 0f;
                }
            }
            else
            {
                ev.StuckTimer = 0f;
            }
            ev.LastPosition = ev.Vehicle.transform.position;
        }
        
        private Vector3 GetAvoidanceDirection(Vector3 pos, Vector3 forward)
        {
            // Raycast to check for obstacles
            RaycastHit hit;
            
            // Check straight ahead
            if (Physics.Raycast(pos + Vector3.up, forward, out hit, ObstacleCheckDistance, 
                LayerMask.GetMask("World", "Construction", "Deployed")))
            {
                // Obstacle ahead - try left or right
                Vector3 leftDir = Quaternion.Euler(0, -45, 0) * forward;
                Vector3 rightDir = Quaternion.Euler(0, 45, 0) * forward;
                
                bool leftClear = !Physics.Raycast(pos + Vector3.up, leftDir, ObstacleCheckDistance,
                    LayerMask.GetMask("World", "Construction", "Deployed"));
                bool rightClear = !Physics.Raycast(pos + Vector3.up, rightDir, ObstacleCheckDistance,
                    LayerMask.GetMask("World", "Construction", "Deployed"));
                
                if (leftClear && !rightClear) return leftDir;
                if (rightClear && !leftClear) return rightDir;
                if (leftClear && rightClear)
                {
                    // Both clear, pick randomly
                    return UnityEngine.Random.value > 0.5f ? leftDir : rightDir;
                }
                
                // Both blocked, try harder turns
                leftDir = Quaternion.Euler(0, -90, 0) * forward;
                rightDir = Quaternion.Euler(0, 90, 0) * forward;
                leftClear = !Physics.Raycast(pos + Vector3.up, leftDir, ObstacleCheckDistance);
                if (leftClear) return leftDir;
                return rightDir;
            }
            
            return Vector3.zero; // No obstacle, continue forward
        }
        
        private void DismountNPCs(DriveByEvent ev)
        {
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && npc.IsMounted())
                {
                    npc.DismountObject();
                    npc.Brain.SetEnabled(true);
                    
                    // Set combat target
                    BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                    if (target != null)
                    {
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                    }
                }
            }
        }
        
        private void RemountNPCs(DriveByEvent ev)
        {
            int seatIndex = 0;
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && !npc.IsMounted())
                {
                    // Teleport to vehicle first
                    npc.transform.position = ev.Vehicle.transform.position;
                    
                    // Mount in next available seat
                    if (ev.Vehicle.mountPoints != null && seatIndex < ev.Vehicle.mountPoints.Count)
                    {
                        BaseMountable mountable = ev.Vehicle.mountPoints[seatIndex].mountable;
                        if (mountable != null) 
                        {
                            mountable.MountPlayer(npc);
                            if (seatIndex == 0) // Driver
                                npc.Brain.SetEnabled(false);
                        }
                    }
                    seatIndex++;
                }
            }
        }

        private void CleanUpEvent(DriveByEvent ev)
        {
            if (ev == null) return;
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed) npc.Kill();
            }
            if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed) ev.Vehicle.Kill();
        }

        #endregion

        #region Admin Commands

        [ChatCommand("testdriveby")]
        private void CmdTestDriveBy(BasePlayer player)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                SendReply(player, "Admin access denied.");
                return;
            }

            if (HoodWars == null) { SendReply(player, "HoodWars not loaded."); return; }

            string currentZone = HoodWars.Call<string>("GetNeighborhoodNameAt", player.transform.position) ?? "Neutral";
            if (currentZone == "Neutral")
            {
                SendReply(player, "Stand in a gang territory to test.");
                return;
            }

            SendReply(player, $"Triggering {currentZone} test drive-by...");
            StartDriveBy(player, currentZone);
        }

        #endregion

        #region Helpers

        private Dictionary<ulong, float> _cooldowns = new Dictionary<ulong, float>();

        private Vector3 ParseVector3(object obj)
        {
            // If it's already a Vector3, return it
            if (obj is Vector3) return (Vector3)obj;
            
            // If it's a dictionary (serialized config format)
            if (obj is Dictionary<string, object> dict)
            {
                float x = dict.ContainsKey("x") ? Convert.ToSingle(dict["x"]) : 0f;
                float y = dict.ContainsKey("y") ? Convert.ToSingle(dict["y"]) : 0f;
                float z = dict.ContainsKey("z") ? Convert.ToSingle(dict["z"]) : 0f;
                return new Vector3(x, y, z);
            }
            
            // If it's a string like "(100, 10, 200)"
            if (obj is string str)
            {
                str = str.Trim('(', ')');
                var parts = str.Split(',');
                if (parts.Length >= 3)
                {
                    float.TryParse(parts[0].Trim(), out float x);
                    float.TryParse(parts[1].Trim(), out float y);
                    float.TryParse(parts[2].Trim(), out float z);
                    return new Vector3(x, y, z);
                }
            }
            
            return Vector3.zero;
        }

        private bool IsOnCooldown(ulong id)
        {
            if (!_cooldowns.ContainsKey(id)) return false;
            return Time.realtimeSinceStartup < _cooldowns[id];
        }

        private void SetCooldown(ulong id)
        {
            var settings = Config["Settings"] as Dictionary<string, object>;
            float mins = settings != null ? Convert.ToSingle(settings["Cooldown per Player (Minutes)"]) : 10f;
            _cooldowns[id] = Time.realtimeSinceStartup + (mins * 60f);
        }

        #endregion
    }
}