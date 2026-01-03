using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "1.7.0")]
    [Description("Premium AI drive-bys: drive-by shooting, chase mode, U-turn, return, then dismount attack.")]
    public class DriveBy : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars;

        private enum DriveByPhase
        {
            FirstPass,           // Initial drive-by shooting pass
            UTurn,               // Doing U-turn
            ReturnPass,          // Coming back towards target
            Chasing,             // Chasing player if they run
            StoppingToDisembark, // Slowing down near target
            Dismounted,          // NPCs on foot shooting
            Remounting,          // NPCs getting back in car
            DrivingAway          // Car driving away to exit
        }

        private class DriveByEvent
        {
            public BasicCar Vehicle;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public Vector3 TargetPosition;
            public Vector3 SpawnPosition;       // Where car started (for U-turn)
            public Vector3 ExitPosition;
            public Vector3 UTurnPoint;          // Point past target for U-turn
            public Vector3 LastVehiclePos;      // For stuck detection
            public ulong TargetID;
            public string GangOwner;
            public DriveByPhase Phase = DriveByPhase.FirstPass;
            public float PhaseTimer;
            public float StuckTimer;            // Time when vehicle got stuck
            public float LastShootTime;         // For periodic shooting
            public bool VehicleDestroyed = false;
            public bool FirstPassComplete = false;
        }

        private List<DriveByEvent> _activeEvents = new List<DriveByEvent>();
        private Timer _eventTimer;

        private const string PrefabSedan = "assets/content/vehicles/sedan_a/sedantest.entity.prefab";
        private const string PrefabScientist = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
        private const string PermAdmin = "hoodwars.admin";

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
                ["Drive-By Pass Distance"] = 80f,    // How far past target before U-turn
                ["Dismount Distance"] = 15f,         // How close to stop for dismount
                ["Chase Distance"] = 40f,            // If player moves this far, chase them
                ["Shoot Duration (Seconds)"] = 10f,
                ["Spawn Distance From Border"] = 100f,
                ["Vehicle Speed"] = 15f,             // Max vehicle speed
                ["Stuck Recovery Time"] = 2f         // Seconds before attempting stuck recovery
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

        #region Hooks

        private void OnServerInitialized()
        {
            // Update at 5Hz for smooth vehicle control
            _eventTimer = timer.Every(0.2f, UpdateActiveDriveBys);
            
            var settings = Config["Settings"] as Dictionary<string, object>;
            float interval = settings != null ? Convert.ToSingle(settings["Detection Interval (Seconds)"]) : 30f;
            
            timer.Every(interval, CheckForIntruders);
        }

        private void Unload()
        {
            _eventTimer?.Destroy();
            foreach (var ev in _activeEvents) CleanUpEvent(ev, true);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Check if a drive-by NPC died
            var npc = entity as ScientistNPC;
            if (npc == null) return;
            
            foreach (var ev in _activeEvents)
            {
                if (ev.Shooters.Contains(npc))
                {
                    ev.Shooters.Remove(npc);
                    
                    // If any NPC dies, despawn the car but keep remaining NPCs fighting
                    if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed && !ev.VehicleDestroyed)
                    {
                        ev.VehicleDestroyed = true;
                        // Dismount any NPCs still in the car
                        foreach (var shooter in ev.Shooters)
                        {
                            if (shooter != null && !shooter.IsDestroyed && shooter.IsMounted())
                            {
                                shooter.DismountObject();
                                EnableNPCCombat(shooter, ev);
                            }
                        }
                        ev.Vehicle.Kill();
                    }
                    break;
                }
            }
        }

        private void CheckForIntruders()
        {
            if (HoodWars == null) return;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsSleeping() || !player.IsAlive() || player.IsAdmin) continue;

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
            Puts($"[DriveBy] StartDriveBy called - target: {target?.displayName}, gang: {territoryGang}");
            
            // Find a road/flat spawn position from territory border
            Vector3 spawnPos = GetRoadSpawnPosition(territoryGang, target.transform.position);
            Puts($"[DriveBy] Spawn position: {spawnPos}");
            
            if (spawnPos == Vector3.zero)
            {
                Puts($"[DriveBy] ERROR: Could not find valid road spawn for {territoryGang}");
                return;
            }
            
            Vector3 exitPos = GetExitPosition(territoryGang, target.transform.position);
            Quaternion rotation = Quaternion.LookRotation((target.transform.position - spawnPos).normalized);
            rotation.x = 0;
            rotation.z = 0;
            
            Puts($"[DriveBy] Spawning sedan at {spawnPos}...");
            var entity = GameManager.server.CreateEntity(PrefabSedan, spawnPos, rotation);
            Puts($"[DriveBy] CreateEntity returned: {entity?.GetType().Name ?? "NULL"}");
            
            BasicCar vehicle = entity as BasicCar;
            if (vehicle == null)
            {
                Puts($"[DriveBy] ERROR: Vehicle is null! Entity was: {entity?.GetType().Name ?? "NULL"}");
                if (entity != null) entity.Kill();
                return;
            }

            Puts($"[DriveBy] Calling vehicle.Spawn()...");
            vehicle.Spawn();
            Puts($"[DriveBy] Vehicle spawned: {vehicle?.net?.ID}, IsDestroyed: {vehicle?.IsDestroyed}");
            
            // Wait a frame for vehicle to fully initialize
            NextTick(() => {
                Puts($"[DriveBy] NextTick callback - vehicle valid: {vehicle != null && !vehicle.IsDestroyed}");
                if (vehicle == null || vehicle.IsDestroyed)
                {
                    Puts("[DriveBy] ERROR: Vehicle was destroyed before initialization!");
                    return;
                }
                
                Puts($"[DriveBy] Vehicle mountPoints: {vehicle.mountPoints?.Count ?? -1}");
                if (vehicle.mountPoints == null || vehicle.mountPoints.Count < 3)
                {
                    Puts($"[DriveBy] ERROR: Vehicle has insufficient mount points!");
                    return;
                }
                
                // Calculate U-turn point (past target)
                var settings = Config["Settings"] as Dictionary<string, object>;
                float passDistance = settings != null && settings.ContainsKey("Drive-By Pass Distance") 
                    ? Convert.ToSingle(settings["Drive-By Pass Distance"]) : 80f;
                
                Vector3 dirToTarget = (target.transform.position - spawnPos).normalized;
                Vector3 uTurnPoint = target.transform.position + dirToTarget * passDistance;
                uTurnPoint = GetFlatGroundPosition(uTurnPoint);
                if (uTurnPoint == Vector3.zero) uTurnPoint = target.transform.position + dirToTarget * passDistance;
                
                DriveByEvent ev = new DriveByEvent
                {
                    Vehicle = vehicle,
                    TargetPosition = target.transform.position,
                    SpawnPosition = spawnPos,
                    UTurnPoint = uTurnPoint,
                    ExitPosition = exitPos,
                    LastVehiclePos = spawnPos,
                    TargetID = target.userID,
                    GangOwner = territoryGang,
                    Phase = DriveByPhase.FirstPass,
                    PhaseTimer = Time.realtimeSinceStartup,
                    StuckTimer = 0f,
                    LastShootTime = 0f
                };

                // Spawn 3 NPCs and mount them properly
                // We need to mount them AFTER vehicle is ready
                Puts($"[DriveBy] Spawning NPCs...");
                SpawnAndMountNPC(ev, vehicle, 0, true);   // Driver
                SpawnAndMountNPC(ev, vehicle, 1, false);  // Shooter 1
                SpawnAndMountNPC(ev, vehicle, 2, false);  // Shooter 2
                
                Puts($"[DriveBy] NPCs created: {ev.Shooters.Count}");

                _activeEvents.Add(ev);
                SetCooldown(target.userID);
                
                // Debug: Report vehicle position every second for 10 seconds
                int tickCount = 0;
                timer.Repeat(1f, 10, () => {
                    tickCount++;
                    if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed)
                    {
                        var pos = ev.Vehicle.transform.position;
                        var rb = ev.Vehicle.GetComponent<Rigidbody>();
                        float speed = rb != null ? rb.velocity.magnitude : 0f;
                        Puts($"[DriveBy] Tick {tickCount}: Vehicle at {pos:F1}, speed={speed:F1}, phase={ev.Phase}, NPCs alive={ev.Shooters.Count(s => s != null && !s.IsDestroyed)}");
                    }
                    else
                    {
                        Puts($"[DriveBy] Tick {tickCount}: Vehicle DESTROYED!");
                    }
                });
                
                Puts($"[DriveBy] Drive-by event started successfully! Active events: {_activeEvents.Count}");
                
                // Tell player the spawn position so they can teleport there if needed
                if (target != null)
                {
                    target.ChatMessage($"<color=#ff4444>[DEBUG]</color> Drive-by spawned at {spawnPos:F0}. Use: teleportpos {spawnPos.x:F0} {spawnPos.y:F0} {spawnPos.z:F0}");
                }
                
                PrintToChat($"<color=#ff4444>[STREET NEWS]</color> Drive-by in progress in {territoryGang} territory!");
            });
        }
        
        private Vector3 GetRoadSpawnPosition(string gang, Vector3 targetPos)
        {
            var borderConfig = Config["Border Spawns"] as Dictionary<string, object>;
            var settings = Config["Settings"] as Dictionary<string, object>;
            float spawnDist = settings != null && settings.ContainsKey("Spawn Distance From Border") 
                ? Convert.ToSingle(settings["Spawn Distance From Border"]) : 100f;
            
            string direction = "west";
            if (borderConfig != null && borderConfig.ContainsKey(gang))
                direction = borderConfig[gang].ToString().ToLower();
            
            float worldSize = TerrainMeta.Size.x / 2f;
            
            // Start closer to target and search outward for valid spawn (50-150m away)
            for (float distFromTarget = 50f; distFromTarget <= 150f; distFromTarget += 25f)
            {
                Vector3 basePos;
                switch (direction)
                {
                    case "west":
                        basePos = new Vector3(targetPos.x - distFromTarget, 0, targetPos.z);
                        break;
                    case "east":
                        basePos = new Vector3(targetPos.x + distFromTarget, 0, targetPos.z);
                        break;
                    case "north":
                        basePos = new Vector3(targetPos.x, 0, targetPos.z + distFromTarget);
                        break;
                    case "south":
                        basePos = new Vector3(targetPos.x, 0, targetPos.z - distFromTarget);
                        break;
                    default:
                        basePos = new Vector3(targetPos.x - distFromTarget, 0, targetPos.z);
                        break;
                }
                
                // Clamp to world bounds
                basePos.x = Mathf.Clamp(basePos.x, -worldSize + 50f, worldSize - 50f);
                basePos.z = Mathf.Clamp(basePos.z, -worldSize + 50f, worldSize - 50f);
                
                // Search for a good spawn point nearby
                for (int i = 0; i < 15; i++)
                {
                    Vector3 testPos = basePos + new Vector3(
                        UnityEngine.Random.Range(-30f, 30f), 
                        0, 
                        UnityEngine.Random.Range(-30f, 30f)
                    );
                    
                    testPos = GetFlatGroundPosition(testPos);
                    if (testPos != Vector3.zero && !IsInWater(testPos) && IsFlatEnough(testPos))
                    {
                        Puts($"[DriveBy] Found valid spawn at dist {distFromTarget}: {testPos}");
                        return testPos;
                    }
                }
            }
            
            // Last resort: spawn near the target itself
            for (int i = 0; i < 20; i++)
            {
                Vector3 testPos = targetPos + new Vector3(
                    UnityEngine.Random.Range(-100f, 100f), 
                    0, 
                    UnityEngine.Random.Range(-100f, 100f)
                );
                
                testPos = GetFlatGroundPosition(testPos);
                if (testPos != Vector3.zero && !IsInWater(testPos) && IsFlatEnough(testPos))
                {
                    Puts($"[DriveBy] Found fallback spawn near target: {testPos}");
                    return testPos;
                }
            }
            
            Puts($"[DriveBy] ERROR: Could not find any valid spawn position!");
            return Vector3.zero;
        }
        
        private Vector3 GetFlatGroundPosition(Vector3 pos)
        {
            RaycastHit hit;
            pos.y = 500f;
            if (Physics.Raycast(pos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain", "World")))
            {
                Vector3 groundPos = hit.point + Vector3.up * 0.5f;
                
                // Make sure position is above water level
                float waterLevel = WaterSystem.OceanLevel;
                if (groundPos.y < waterLevel + 1f)
                {
                    Puts($"[DriveBy] Ground position {groundPos.y} is below water level {waterLevel}, skipping");
                    return Vector3.zero;
                }
                
                return groundPos;
            }
            return Vector3.zero;
        }
        
        private bool IsInWater(Vector3 pos)
        {
            return pos.y < WaterSystem.OceanLevel + 0.5f;
        }
        
        private bool IsFlatEnough(Vector3 pos)
        {
            // Check if terrain is relatively flat (slope < 20 degrees)
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out hit, 5f, LayerMask.GetMask("Terrain")))
            {
                float angle = Vector3.Angle(hit.normal, Vector3.up);
                return angle < 20f;
            }
            return true;
        }
        
        private Vector3 GetExitPosition(string gang, Vector3 targetPos)
        {
            var borderConfig = Config["Border Spawns"] as Dictionary<string, object>;
            string direction = "west";
            if (borderConfig != null && borderConfig.ContainsKey(gang))
                direction = borderConfig[gang].ToString().ToLower();
            
            float worldSize = TerrainMeta.Size.x / 2f;
            
            // Exit opposite from spawn
            switch (direction)
            {
                case "west":
                    return new Vector3(worldSize - 50f, 0, targetPos.z);
                case "east":
                    return new Vector3(-worldSize + 50f, 0, targetPos.z);
                case "north":
                    return new Vector3(targetPos.x, 0, -worldSize + 50f);
                case "south":
                    return new Vector3(targetPos.x, 0, worldSize - 50f);
            }
            return targetPos + Vector3.forward * 200f;
        }

        private void SpawnAndMountNPC(DriveByEvent ev, BasicCar vehicle, int seatIndex, bool isDriver)
        {
            Puts($"[DriveBy] SpawnAndMountNPC: seatIndex={seatIndex}, isDriver={isDriver}");
            
            if (vehicle == null || vehicle.IsDestroyed)
            {
                Puts($"[DriveBy] ERROR: Vehicle is null/destroyed in SpawnAndMountNPC");
                return;
            }
            if (vehicle.mountPoints == null || seatIndex >= vehicle.mountPoints.Count)
            {
                Puts($"[DriveBy] ERROR: Invalid mount point index {seatIndex} (total: {vehicle.mountPoints?.Count ?? 0})");
                return;
            }
            
            var mountPoint = vehicle.mountPoints[seatIndex];
            if (mountPoint?.mountable == null)
            {
                Puts($"[DriveBy] ERROR: Mount point {seatIndex} has no mountable");
                return;
            }
            
            // Spawn NPC directly at the mount point position
            Vector3 mountPos = mountPoint.mountable.transform.position;
            Puts($"[DriveBy] Creating NPC at {mountPos}...");
            
            var npcEntity = GameManager.server.CreateEntity(PrefabScientist, mountPos, vehicle.transform.rotation);
            Puts($"[DriveBy] CreateEntity returned: {npcEntity?.GetType().Name ?? "NULL"}");
            
            ScientistNPC npc = npcEntity as ScientistNPC;
            if (npc == null)
            {
                Puts($"[DriveBy] ERROR: NPC is null! Entity was: {npcEntity?.GetType().Name ?? "NULL"}");
                if (npcEntity != null) npcEntity.Kill();
                return;
            }

            npc.Spawn();
            Puts($"[DriveBy] NPC spawned: {npc.net?.ID}");
            npc.inventory.Strip();

            // Dress NPC in gang colors
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

            // Disable NavMeshAgent before mounting
            var navAgent = npc.GetComponent<NavMeshAgent>();
            if (navAgent != null) navAgent.enabled = false;
            
            // Mount NPC to vehicle seat immediately
            mountPoint.mountable.MountPlayer(npc);
            
            // Configure AI - all NPCs should be able to attack, but driver focuses on "driving"
            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);
            }
            
            // Set hostility so NPCs will attack - use ScientistNPC specific methods
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null)
            {
                // Use Brain's memory system to set target
                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
                
                // Also try to set hostile target via Brain's Events if available
                if (npc.Brain?.Events != null)
                {
                    npc.Brain.Events.Memory.Entity.Set(target, 0);
                }
            }

            ev.Shooters.Add(npc);
        }
        
        private void EnableNPCCombat(ScientistNPC npc, DriveByEvent ev)
        {
            if (npc == null || npc.IsDestroyed) return;
            
            // Find valid NavMesh position
            NavMeshHit navHit;
            Vector3 pos = npc.transform.position;
            if (NavMesh.SamplePosition(pos, out navHit, 20f, NavMesh.AllAreas))
            {
                npc.transform.position = navHit.position;
            }
            
            // Enable NavMesh for proper movement
            var navAgent = npc.GetComponent<NavMeshAgent>();
            if (navAgent != null)
            {
                navAgent.enabled = true;
                navAgent.Warp(npc.transform.position);
            }
            
            npc.Brain.SetEnabled(true);
            
            // Set target
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null && target.IsAlive())
            {
                npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
            }
        }

        private void UpdateActiveDriveBys()
        {
            var settings = Config["Settings"] as Dictionary<string, object>;
            float dismountDist = settings != null && settings.ContainsKey("Dismount Distance") 
                ? Convert.ToSingle(settings["Dismount Distance"]) : 15f;
            float chaseDist = settings != null && settings.ContainsKey("Chase Distance") 
                ? Convert.ToSingle(settings["Chase Distance"]) : 40f;
            float shootDuration = settings != null && settings.ContainsKey("Shoot Duration (Seconds)") 
                ? Convert.ToSingle(settings["Shoot Duration (Seconds)"]) : 10f;
            float maxSpeed = settings != null && settings.ContainsKey("Vehicle Speed") 
                ? Convert.ToSingle(settings["Vehicle Speed"]) : 15f;
            float stuckRecoveryTime = settings != null && settings.ContainsKey("Stuck Recovery Time") 
                ? Convert.ToSingle(settings["Stuck Recovery Time"]) : 2f;
            
            for (int i = _activeEvents.Count - 1; i >= 0; i--)
            {
                var ev = _activeEvents[i];
                
                // Clean up if all NPCs dead
                if (ev.Shooters.Count == 0 || ev.Shooters.All(s => s == null || s.IsDestroyed))
                {
                    CleanUpEvent(ev, true);
                    _activeEvents.RemoveAt(i);
                    continue;
                }
                
                // Update target position
                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target != null && target.IsAlive())
                    ev.TargetPosition = target.transform.position;
                
                // If vehicle was destroyed, just let NPCs fight
                if (ev.VehicleDestroyed || ev.Vehicle == null || ev.Vehicle.IsDestroyed)
                {
                    ev.VehicleDestroyed = true;
                    UpdateNPCTargets(ev);
                    continue;
                }
                
                // Force NPCs to shoot at target while in vehicle
                MakeNPCsShootFromVehicle(ev);
                
                // Stuck detection
                float distMoved = Vector3.Distance(ev.Vehicle.transform.position, ev.LastVehiclePos);
                if (distMoved < 0.5f && ev.Phase != DriveByPhase.StoppingToDisembark && 
                    ev.Phase != DriveByPhase.Dismounted && ev.Phase != DriveByPhase.Remounting)
                {
                    if (ev.StuckTimer == 0f)
                        ev.StuckTimer = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - ev.StuckTimer > stuckRecoveryTime)
                    {
                        RecoverFromStuck(ev);
                        ev.StuckTimer = 0f;
                    }
                }
                else
                {
                    ev.StuckTimer = 0f;
                    ev.LastVehiclePos = ev.Vehicle.transform.position;
                }

                switch (ev.Phase)
                {
                    case DriveByPhase.FirstPass:
                        // Drive past the target (drive-by shooting)
                        DriveVehicle(ev, ev.UTurnPoint, maxSpeed);
                        UpdateNPCTargets(ev);
                        
                        // Check if past target and near U-turn point
                        float distToUTurn = Vector3.Distance(ev.Vehicle.transform.position, ev.UTurnPoint);
                        if (distToUTurn < 30f)
                        {
                            ev.Phase = DriveByPhase.UTurn;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                        
                    case DriveByPhase.UTurn:
                        // Slow down and turn around
                        DriveVehicle(ev, ev.TargetPosition, maxSpeed * 0.5f);
                        
                        // Check if facing back towards target
                        Vector3 toTarget = (ev.TargetPosition - ev.Vehicle.transform.position).normalized;
                        float dotProduct = Vector3.Dot(ev.Vehicle.transform.forward, toTarget);
                        if (dotProduct > 0.7f || Time.realtimeSinceStartup - ev.PhaseTimer > 5f)
                        {
                            ev.Phase = DriveByPhase.ReturnPass;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                        
                    case DriveByPhase.ReturnPass:
                        // Drive back towards target
                        DriveVehicle(ev, ev.TargetPosition, maxSpeed);
                        UpdateNPCTargets(ev);
                        
                        float distToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (distToTarget <= dismountDist)
                        {
                            ev.Phase = DriveByPhase.StoppingToDisembark;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            StopVehicle(ev);
                        }
                        break;
                    
                    case DriveByPhase.Chasing:
                        // Chase the player - stay mounted and shoot
                        DriveVehicle(ev, ev.TargetPosition, maxSpeed);
                        UpdateNPCTargets(ev);
                        
                        float chaseDistToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (chaseDistToTarget <= dismountDist)
                        {
                            // Caught up - dismount and fight
                            ev.Phase = DriveByPhase.StoppingToDisembark;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            StopVehicle(ev);
                        }
                        break;
                        
                    case DriveByPhase.StoppingToDisembark:
                        // Brief pause before dismount
                        StopVehicle(ev);
                        if (Time.realtimeSinceStartup - ev.PhaseTimer > 1f)
                        {
                            DismountShooters(ev);
                            ev.Phase = DriveByPhase.Dismounted;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                        
                    case DriveByPhase.Dismounted:
                        UpdateNPCTargets(ev);
                        StopVehicle(ev);
                        
                        // Check if player ran away - if so, remount and chase!
                        float distWhileDismounted = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (distWhileDismounted > chaseDist)
                        {
                            // Player ran! Remount and chase
                            ev.Phase = DriveByPhase.Remounting;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            RemountShooters(ev);
                            // Will transition to Chasing after remount
                        }
                        else if (Time.realtimeSinceStartup - ev.PhaseTimer > shootDuration)
                        {
                            ev.Phase = DriveByPhase.Remounting;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            RemountShooters(ev);
                        }
                        break;
                        
                    case DriveByPhase.Remounting:
                        StopVehicle(ev);
                        bool allMounted = ev.Shooters.All(s => s == null || s.IsDestroyed || s.IsMounted());
                        if (allMounted || Time.realtimeSinceStartup - ev.PhaseTimer > 3f)
                        {
                            // Check if we should chase or leave
                            float distAfterRemount = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                            if (distAfterRemount > chaseDist)
                            {
                                // Chase the player!
                                ev.Phase = DriveByPhase.Chasing;
                                ev.PhaseTimer = Time.realtimeSinceStartup;
                            }
                            else
                            {
                                ev.Phase = DriveByPhase.DrivingAway;
                                ev.PhaseTimer = Time.realtimeSinceStartup;
                            }
                        }
                        break;
                        
                    case DriveByPhase.DrivingAway:
                        Vector3 exitGround = GetFlatGroundPosition(ev.ExitPosition);
                        if (exitGround == Vector3.zero) exitGround = ev.ExitPosition;
                        DriveVehicle(ev, exitGround, maxSpeed);
                        
                        // Check if reached exit
                        if (Vector3.Distance(ev.Vehicle.transform.position, exitGround) < 50f)
                        {
                            CleanUpEvent(ev, true);
                            _activeEvents.RemoveAt(i);
                        }
                        break;
                }
            }
        }
        
        private void MakeNPCsShootFromVehicle(DriveByEvent ev)
        {
            // Force NPCs to attack even while mounted
            if (Time.realtimeSinceStartup - ev.LastShootTime < 0.5f) return;
            ev.LastShootTime = Time.realtimeSinceStartup;
            
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;
            
            foreach (var npc in ev.Shooters)
            {
                if (npc == null || npc.IsDestroyed) continue;
                
                // Check if NPC can see target
                float distToTarget = Vector3.Distance(npc.transform.position, target.transform.position);
                if (distToTarget > 80f) continue; // Too far
                
                // Check line of sight
                Vector3 npcEyes = npc.eyes?.position ?? (npc.transform.position + Vector3.up * 1.5f);
                Vector3 targetPos = target.transform.position + Vector3.up * 1f;
                
                if (Physics.Linecast(npcEyes, targetPos, LayerMask.GetMask("World", "Construction", "Terrain")))
                    continue; // Blocked
                
                // Face the target
                Vector3 lookDir = (target.transform.position - npc.transform.position).normalized;
                npc.SetAimDirection(lookDir);
                
                // Trigger attack
                var heldEntity = npc.GetHeldEntity() as BaseProjectile;
                if (heldEntity != null && heldEntity.primaryMagazine.contents > 0)
                {
                    // Fire the weapon
                    npc.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                    
                    // Manually trigger a shot if needed
                    if (heldEntity.primaryMagazine.contents > 0)
                    {
                        heldEntity.ServerUse();
                    }
                }
                else if (heldEntity != null && heldEntity.primaryMagazine.contents == 0)
                {
                    // Reload
                    heldEntity.primaryMagazine.contents = heldEntity.primaryMagazine.capacity;
                }
            }
        }
        
        private void RecoverFromStuck(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            
            Puts($"[DriveBy] Vehicle stuck, attempting recovery...");
            
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb == null) return;
            
            // Try reversing briefly
            rb.velocity = -ev.Vehicle.transform.forward * 5f;
            
            // Add some random rotation to unstick
            rb.AddTorque(Vector3.up * UnityEngine.Random.Range(-200f, 200f), ForceMode.Impulse);
            
            // If very stuck, teleport slightly
            if (ev.StuckTimer > 0 && Time.realtimeSinceStartup - ev.StuckTimer > 4f)
            {
                Vector3 newPos = ev.Vehicle.transform.position + 
                    new Vector3(UnityEngine.Random.Range(-5f, 5f), 2f, UnityEngine.Random.Range(-5f, 5f));
                Vector3 groundPos = GetFlatGroundPosition(newPos);
                if (groundPos != Vector3.zero)
                {
                    ev.Vehicle.transform.position = groundPos;
                    Puts($"[DriveBy] Teleported vehicle to {groundPos}");
                }
            }
        }
        
        private void DriveVehicle(DriveByEvent ev, Vector3 destination, float maxSpeed = 12f)
        {
            if (ev.Vehicle == null) return;
            
            Vector3 toTarget = destination - ev.Vehicle.transform.position;
            toTarget.y = 0;
            
            if (toTarget.magnitude < 5f) return;
            
            // Calculate steering
            Vector3 forward = ev.Vehicle.transform.forward;
            forward.y = 0;
            float angle = Vector3.SignedAngle(forward, toTarget.normalized, Vector3.up);
            
            // Apply throttle and steering via vehicle physics
            float throttle = 1f;
            float steering = Mathf.Clamp(angle / 45f, -1f, 1f);
            
            // Reduce speed on turns
            if (Mathf.Abs(angle) > 30f)
                throttle = 0.5f;
            
            // Check for obstacles/steep terrain ahead
            if (ShouldAvoidAhead(ev.Vehicle.transform.position, forward))
            {
                throttle = 0.3f;
                steering = angle > 0 ? -0.8f : 0.8f; // Turn away
            }
            
            // Apply inputs to BasicCar
            ev.Vehicle.SetFlag(BaseEntity.Flags.Reserved5, throttle > 0.5f); // Engine running
            
            // Use physics to control car smoothly
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Apply force for movement
                Vector3 driveForce = ev.Vehicle.transform.forward * throttle * 2000f;
                rb.AddForce(driveForce, ForceMode.Force);
                
                // Apply torque for steering
                rb.AddTorque(Vector3.up * steering * 500f, ForceMode.Force);
                
                // Limit max speed
                if (rb.velocity.magnitude > maxSpeed)
                {
                    rb.velocity = rb.velocity.normalized * maxSpeed;
                }
            }
        }
        
        private void StopVehicle(DriveByEvent ev)
        {
            if (ev.Vehicle == null) return;
            
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        
        private bool ShouldAvoidAhead(Vector3 pos, Vector3 forward)
        {
            RaycastHit hit;
            
            // Check for obstacles
            if (Physics.Raycast(pos + Vector3.up, forward, out hit, 10f, 
                LayerMask.GetMask("World", "Construction", "Deployed")))
            {
                return true;
            }
            
            // Check terrain steepness ahead
            Vector3 aheadPos = pos + forward * 8f;
            aheadPos.y = 500f;
            if (Physics.Raycast(aheadPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
            {
                float angle = Vector3.Angle(hit.normal, Vector3.up);
                if (angle > 25f) return true; // Too steep
                
                // Check for big height difference
                float heightDiff = Mathf.Abs(hit.point.y - pos.y);
                if (heightDiff > 3f) return true; // Too much elevation change
            }
            
            return false;
        }
        
        private void DismountShooters(DriveByEvent ev)
        {
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && npc.IsMounted())
                {
                    npc.DismountObject();
                    
                    // Small delay then enable combat
                    timer.Once(0.5f, () => {
                        if (npc != null && !npc.IsDestroyed)
                            EnableNPCCombat(npc, ev);
                    });
                }
            }
        }
        
        private void RemountShooters(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            
            int seatIndex = 0;
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && !npc.IsMounted())
                {
                    // Disable NavMesh
                    var navAgent = npc.GetComponent<NavMeshAgent>();
                    if (navAgent != null) navAgent.enabled = false;
                    
                    // Mount to vehicle
                    if (ev.Vehicle.mountPoints != null && seatIndex < ev.Vehicle.mountPoints.Count)
                    {
                        var mountPoint = ev.Vehicle.mountPoints[seatIndex];
                        if (mountPoint?.mountable != null)
                        {
                            npc.transform.position = mountPoint.mountable.transform.position;
                            mountPoint.mountable.MountPlayer(npc);
                            
                            // All NPCs should be able to shoot while mounted
                            npc.Brain.SetEnabled(true);
                            npc.SetFact(BaseNpc.Facts.IsAggro, 1);
                            npc.SetFact(BaseNpc.Facts.HasEnemy, 1);
                        }
                    }
                }
                seatIndex++;
            }
        }
        
        private void UpdateNPCTargets(DriveByEvent ev)
        {
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            foreach (var npc in ev.Shooters)
            {
                // Update targets for both mounted (drive-by) and dismounted (on-foot) NPCs
                if (npc != null && !npc.IsDestroyed && target != null && target.IsAlive())
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
            }
        }

        private void CleanUpEvent(DriveByEvent ev, bool killAll)
        {
            if (ev == null) return;
            
            if (killAll)
            {
                foreach (var npc in ev.Shooters)
                {
                    if (npc != null && !npc.IsDestroyed) npc.Kill();
                }
            }
            
            if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed) 
                ev.Vehicle.Kill();
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

            Puts($"[DriveBy] CmdTestDriveBy called by {player.displayName}");
            
            if (HoodWars == null)
            {
                SendReply(player, "HoodWars not loaded.");
                Puts("[DriveBy] ERROR: HoodWars plugin not loaded!");
                return;
            }

            string currentZone = HoodWars.Call<string>("GetNeighborhoodNameAt", player.transform.position) ?? "Neutral";
            Puts($"[DriveBy] Player zone: {currentZone}");
            
            if (currentZone == "Neutral")
            {
                SendReply(player, "Stand in a gang territory to test.");
                return;
            }

            SendReply(player, $"Triggering {currentZone} test drive-by at your position...");
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