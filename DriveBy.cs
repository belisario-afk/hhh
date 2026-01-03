using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "2.0.6")]
    [Description("Premium AI drive-bys: Continuous steering, escape despawn, and destroys natural obstacles.")]
    public class DriveBy : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars;

        private enum DriveByPhase
        {
            Chasing,             // Car chasing player (main state)
            StoppingToDisembark, // Slowing down near target
            Dismounted,          // NPCs on foot shooting
            Remounting           // NPCs getting back in car (then back to Chasing)
        }

        private class DriveByEvent
        {
            public BasicCar Vehicle;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public Vector3 TargetPosition;
            public Vector3 SpawnPosition;       // Where car started
            public Vector3 LastVehiclePos;      // For stuck detection
            public ulong TargetID;
            public string GangOwner;
            public DriveByPhase Phase = DriveByPhase.Chasing;
            public float PhaseTimer;
            public float StuckTimer;            // Time when vehicle got stuck
            public float LastShootTime;         // For periodic shooting
            public int LastShooterIndex;         // For alternating fire between NPCs
            public bool VehicleDestroyed = false;
        }

        private List<DriveByEvent> _activeEvents = new List<DriveByEvent>();
        private HashSet<ulong> _driveByNPCs = new HashSet<ulong>();  // Track our NPCs for damage handling
        private HashSet<ulong> _driveByVehicles = new HashSet<ulong>();  // Track our vehicles for collision handling
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
        
        // Natural resource prefabs that can be destroyed by the car
        private static HashSet<string> _destructiblePrefabs = new HashSet<string>
        {
            "tree", "oak", "birch", "pine", "beech", "palm", "swamp",
            "dead_log", "driftwood", "log_pile",
            "stone-ore", "metal-ore", "sulfur-ore",
            "collectable",
            "bush", "grass", "hemp", "corn", "pumpkin", "potato", "berry",
            "minecart", "barrel", "crate"
        };

        protected override void LoadDefaultConfig()
        {
            Config["Settings"] = new Dictionary<string, object>
            {
                ["Detection Interval (Seconds)"] = 30,
                ["Cooldown per Player (Minutes)"] = 10,
                ["Dismount Distance"] = 15f,         // How close to stop for dismount
                ["Chase Distance"] = 40f,            // If player moves this far while dismounted, chase them
                ["Escape Distance"] = 150f,          // If player escapes this far, despawn the event
                ["Shoot Duration (Seconds)"] = 10f,
                ["Min Shoot Distance"] = 10f,         // Minimum distance to shoot
                ["Max Shoot Distance"] = 60f,         // Maximum distance to shoot
                ["Shoot Interval"] = 0.6f,            // Seconds between each NPC shot (alternating fire)
                ["Spawn Distance From Border"] = 100f,
                ["Vehicle Speed"] = 18f,             // Max vehicle speed (aggressive)
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
            _driveByNPCs.Clear();
            _driveByVehicles.Clear();
            foreach (var ev in _activeEvents) CleanUpEvent(ev, true);
        }
        
        // API: Check if an NPC is a DriveBy NPC (for other plugins like HoodWars)
        private bool API_IsDriveByNPC(ulong netId)
        {
            return _driveByNPCs.Contains(netId);
        }
        
        // Hook to ensure DriveBy NPCs can take damage - MUST return null to not block
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Check if this is one of our DriveBy NPCs
            if (entity is ScientistNPC npc && npc.net != null && _driveByNPCs.Contains(npc.net.ID.Value))
            {
                // Log for debugging
                float damage = info?.damageTypes?.Total() ?? 0f;
                Puts($"[DriveBy] NPC {npc.net.ID} taking {damage:F1} damage from {info?.Initiator?.GetType().Name ?? "unknown"}");
                
                // Force the NPC to take damage by applying it directly if needed
                // This ensures damage goes through even if NPC has protection
                if (info != null && damage > 0)
                {
                    // Scale damage for mounted NPCs (they should take MORE damage when exposed in vehicle)
                    if (npc.IsMounted())
                    {
                        info.damageTypes.ScaleAll(1.2f); // 20% more damage when in vehicle
                    }
                }
            }
            // Don't return anything - void return means damage proceeds normally
        }
        
        // Handle collisions with natural resources - destroy trees, ore, etc.
        private void OnEntityEnter(TriggerBase trigger, BaseEntity entity)
        {
            // Check if this is one of our drive-by vehicles
            var vehicle = trigger.GetComponentInParent<BasicCar>();
            if (vehicle == null || vehicle.net == null) return;
            
            // Check if this vehicle belongs to a drive-by event using fast lookup
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;
            
            // Check if the entity is a natural resource that can be destroyed
            if (IsDestructibleResource(entity))
            {
                entity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }
        
        // Alternative collision hook using physics
        private void OnCollision(BaseEntity entity, Collision collision)
        {
            // Check if this is one of our drive-by vehicles
            var vehicle = entity as BasicCar;
            if (vehicle == null || vehicle.net == null) return;
            
            // Check if this vehicle belongs to a drive-by event using fast lookup
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;
            
            // Check the collided object
            var collidedEntity = collision?.gameObject?.GetComponentInParent<BaseEntity>();
            if (collidedEntity != null && IsDestructibleResource(collidedEntity))
            {
                collidedEntity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }
        
        // Detect when vehicle hits something and destroy natural resources
        private void OnVehicleHit(BaseVehicle vehicle, HitInfo info)
        {
            if (vehicle == null || vehicle.net == null) return;
            
            // Check if this is our drive-by vehicle
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;
            
            // Check what was hit
            var hitEntity = info?.HitEntity as BaseEntity;
            if (hitEntity != null && IsDestructibleResource(hitEntity))
            {
                hitEntity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }
        
        // Check if an entity is a natural resource that can be destroyed
        private bool IsDestructibleResource(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return false;
            
            string prefabName = entity.ShortPrefabName?.ToLower() ?? "";
            string fullPrefabName = entity.PrefabName?.ToLower() ?? "";
            
            // NEVER destroy player-placed items
            if (entity.OwnerID != 0) return false;
            
            // Check if it's a tree
            if (entity is TreeEntity) return true;
            
            // Check if it's an ore node
            if (entity is OreResourceEntity) return true;
            
            // Check if it's a collectable
            if (entity is CollectibleEntity) return true;
            
            // Check if it's a resource entity (hemp, stone, etc.)
            if (entity is ResourceEntity) return true;
            
            // Check if it matches any known destructible prefabs
            foreach (var pattern in _destructiblePrefabs)
            {
                if (prefabName.Contains(pattern) || fullPrefabName.Contains(pattern))
                {
                    return true;
                }
            }
            
            // Spawned loot containers/barrels only
            if (entity is LootContainer) return true;
            
            return false;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Check if a drive-by NPC died
            var npc = entity as ScientistNPC;
            if (npc == null) return;
            
            // Remove from tracking
            if (npc.net != null)
            {
                _driveByNPCs.Remove(npc.net.ID.Value);
            }
            
            foreach (var ev in _activeEvents)
            {
                if (ev.Shooters.Contains(npc))
                {
                    ev.Shooters.Remove(npc);
                    
                    // If any NPC dies, despawn the car but keep remaining NPCs fighting
                    if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed && !ev.VehicleDestroyed)
                    {
                        ev.VehicleDestroyed = true;
                        Vector3 carPos = ev.Vehicle.transform.position;
                        
                        // Kill vehicle first
                        ev.Vehicle.Kill();
                        
                        // Dismount any NPCs still in the car and place them on ground
                        foreach (var shooter in ev.Shooters)
                        {
                            if (shooter != null && !shooter.IsDestroyed)
                            {
                                if (shooter.IsMounted())
                                {
                                    shooter.DismountObject();
                                }
                                
                                // Teleport to valid ground position immediately
                                PlaceNPCOnGround(shooter, carPos, ev);
                            }
                        }
                    }
                    break;
                }
            }
        }
        
        private void PlaceNPCOnGround(ScientistNPC npc, Vector3 nearPos, DriveByEvent ev)
        {
            if (npc == null || npc.IsDestroyed) return;
            
            // Find a valid ground position on NavMesh
            NavMeshHit navHit;
            Vector3 groundPos = nearPos;
            
            // Try to find a NavMesh position nearby
            if (NavMesh.SamplePosition(nearPos, out navHit, 15f, NavMesh.AllAreas))
            {
                groundPos = navHit.position;
            }
            else
            {
                // Fallback: raycast to find ground
                RaycastHit hit;
                Vector3 above = nearPos + Vector3.up * 10f;
                if (Physics.Raycast(above, Vector3.down, out hit, 50f, LayerMask.GetMask("Terrain", "World")))
                {
                    groundPos = hit.point + Vector3.up * 0.1f;
                }
            }
            
            // Move NPC to ground position
            npc.transform.position = groundPos;
            
            // Small delay then enable combat AI
            timer.Once(0.3f, () => {
                if (npc != null && !npc.IsDestroyed)
                    EnableNPCCombat(npc, ev);
            });
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
            
            // Face towards the target (flat rotation on Y axis only)
            Vector3 dirToTarget = (target.transform.position - spawnPos);
            dirToTarget.y = 0;
            dirToTarget.Normalize();
            Quaternion rotation = Quaternion.LookRotation(dirToTarget);
            
            Puts($"[DriveBy] Spawn facing direction: {dirToTarget}, angle to target: {Vector3.Angle(dirToTarget, Vector3.forward)}");
            Puts($"[DriveBy] Target at {target.transform.position}, spawn at {spawnPos}");
            
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
                
                DriveByEvent ev = new DriveByEvent
                {
                    Vehicle = vehicle,
                    TargetPosition = target.transform.position,
                    SpawnPosition = spawnPos,
                    LastVehiclePos = spawnPos,
                    TargetID = target.userID,
                    GangOwner = territoryGang,
                    Phase = DriveByPhase.Chasing,  // Start directly in chase mode
                    PhaseTimer = Time.realtimeSinceStartup,
                    StuckTimer = 0f,
                    LastShootTime = 0f,
                    LastShooterIndex = 0
                };
                
                // Track this vehicle for collision handling
                if (vehicle.net != null)
                {
                    _driveByVehicles.Add(vehicle.net.ID.Value);
                }

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
                        Vector3 fwd = ev.Vehicle.transform.forward;
                        Vector3 toTarget = (ev.TargetPosition - pos).normalized;
                        float angle = Vector3.SignedAngle(fwd, toTarget, Vector3.up);
                        float distToTarget = Vector3.Distance(pos, ev.TargetPosition);
                        Puts($"[DriveBy] Tick {tickCount}: pos={pos:F1}, speed={speed:F1}, phase={ev.Phase}, angleToTarget={angle:F1}°, distToTarget={distToTarget:F1}m");
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
            
            // Track this NPC as a DriveBy NPC (for damage handling)
            if (npc.net != null)
            {
                _driveByNPCs.Add(npc.net.ID.Value);
            }
            
            // Make sure NPC can take damage - set health properly
            npc.InitializeHealth(150f, 150f);  // Health/MaxHealth
            npc.startHealth = 150f;
            npc.SetMaxHealth(150f);
            npc.SetHealth(150f);
            
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
            
            Puts($"[DriveBy] Enabling combat for NPC {npc.net?.ID}");
            
            // Find valid NavMesh position
            NavMeshHit navHit;
            Vector3 pos = npc.transform.position;
            if (NavMesh.SamplePosition(pos, out navHit, 20f, NavMesh.AllAreas))
            {
                npc.transform.position = navHit.position;
                Puts($"[DriveBy] Moved NPC to NavMesh at {navHit.position}");
            }
            else
            {
                Puts($"[DriveBy] WARNING: No NavMesh found near {pos}");
            }
            
            // Enable NavMesh for proper movement - configure like normal scientist
            var navAgent = npc.GetComponent<NavMeshAgent>();
            if (navAgent != null)
            {
                navAgent.enabled = true;
                navAgent.Warp(npc.transform.position);
                navAgent.stoppingDistance = 5f;  // Stop 5m from target
                navAgent.speed = 4.5f;  // Normal run speed
                navAgent.acceleration = 6f;  // Smooth acceleration
                navAgent.angularSpeed = 120f;  // Normal turning
                navAgent.autoBraking = true;
                navAgent.autoRepath = true;  // Important for pathfinding
            }
            
            // Enable brain for proper AI behavior - let it use default states
            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);
                
                // Configure navigator for proper movement
                if (npc.Brain.Navigator != null)
                {
                    npc.Brain.Navigator.CanUseNavMesh = true;
                    npc.Brain.Navigator.CanUseAStar = true;
                    npc.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                }
            }
            
            // Configure NPC for combat - like a normal hostile scientist
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);
            
            // Get target
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null && target.IsAlive())
            {
                Puts($"[DriveBy] Setting target to {target.displayName}");
                
                // Set target in memory - this makes NPC naturally hostile
                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
                
                // Also set as current threat/target
                if (npc.Brain?.Events?.Memory?.Entity != null)
                {
                    npc.Brain.Events.Memory.Entity.Set(target, 0);
                }
                
                // Set initial destination to chase target
                if (npc.Brain?.Navigator != null)
                {
                    npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                }
            }
            
            // Start periodic combat update for this NPC - let brain handle most behavior
            StartCombatBehavior(npc, ev);
        }
        
        private void StartCombatBehavior(ScientistNPC npc, DriveByEvent ev)
        {
            // Update NPC combat every 1 second - less aggressive, let brain handle most behavior
            timer.Repeat(1f, 0, () =>
            {
                if (npc == null || npc.IsDestroyed || ev.Shooters == null || !ev.Shooters.Contains(npc))
                    return;
                
                // Only update if dismounted
                if (npc.IsMounted()) return;
                
                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target == null || !target.IsAlive()) return;
                
                float distToTarget = Vector3.Distance(npc.transform.position, target.transform.position);
                
                // Just update the target position - let brain handle movement
                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }
                
                // Update destination periodically if target moved far
                if (distToTarget > 8f && npc.Brain?.Navigator != null)
                {
                    npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                }
                
                // Manual shooting only when stationary and in range - let brain handle movement shooting
                if (distToTarget < 25f && distToTarget > 3f)
                {
                    // Face the target
                    Vector3 lookDir = (target.transform.position - npc.transform.position).normalized;
                    npc.SetAimDirection(lookDir);
                    
                    // Check if we should shoot (line of sight)
                    var heldEntity = npc.GetHeldEntity() as BaseProjectile;
                    if (heldEntity != null)
                    {
                        Vector3 npcEyes = npc.eyes?.position ?? (npc.transform.position + Vector3.up * 1.5f);
                        Vector3 targetPos = target.transform.position + Vector3.up * 1f;
                        
                        if (!Physics.Linecast(npcEyes, targetPos, LayerMask.GetMask("World", "Construction")))
                        {
                            if (heldEntity.primaryMagazine.contents > 0)
                            {
                                npc.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                                heldEntity.ServerUse();
                            }
                            else
                            {
                                // Reload
                                heldEntity.primaryMagazine.contents = heldEntity.primaryMagazine.capacity;
                            }
                        }
                    }
                }
            });
        }

        private void UpdateActiveDriveBys()
        {
            var settings = Config["Settings"] as Dictionary<string, object>;
            float dismountDist = settings != null && settings.ContainsKey("Dismount Distance") 
                ? Convert.ToSingle(settings["Dismount Distance"]) : 15f;
            float chaseDist = settings != null && settings.ContainsKey("Chase Distance") 
                ? Convert.ToSingle(settings["Chase Distance"]) : 40f;
            float escapeDist = settings != null && settings.ContainsKey("Escape Distance") 
                ? Convert.ToSingle(settings["Escape Distance"]) : 150f;
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
                
                // Calculate distance to target for escape check
                Vector3 eventCenter = ev.Vehicle != null && !ev.Vehicle.IsDestroyed 
                    ? ev.Vehicle.transform.position 
                    : (ev.Shooters.FirstOrDefault(s => s != null && !s.IsDestroyed)?.transform.position ?? ev.SpawnPosition);
                float distToTarget = Vector3.Distance(eventCenter, ev.TargetPosition);
                
                // ESCAPE CHECK - If player gets too far away, despawn the entire event
                if (distToTarget > escapeDist)
                {
                    Puts($"[DriveBy] Player escaped! Distance: {distToTarget:F1}m > {escapeDist}m. Despawning event.");
                    CleanUpEvent(ev, true);
                    _activeEvents.RemoveAt(i);
                    
                    // Notify the player
                    if (target != null)
                    {
                        target.ChatMessage("<color=#44ff44>[ESCAPE]</color> You got away from the drive-by!");
                    }
                    continue;
                }
                
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
                    case DriveByPhase.Chasing:
                        // Always chase the player - this is the main state
                        DriveVehicle(ev, ev.TargetPosition, maxSpeed);
                        UpdateNPCTargets(ev);
                        
                        float chaseDistToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (chaseDistToTarget <= dismountDist)
                        {
                            // Close enough - dismount and fight
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
                        }
                        else if (Time.realtimeSinceStartup - ev.PhaseTimer > shootDuration)
                        {
                            // Shoot duration over - remount and chase again
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
                            // Always go back to chasing - never drive away!
                            ev.Phase = DriveByPhase.Chasing;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                }
            }
        }
        
        private void MakeNPCsShootFromVehicle(DriveByEvent ev)
        {
            var settings = Config["Settings"] as Dictionary<string, object>;
            float minShootDist = settings != null && settings.ContainsKey("Min Shoot Distance") 
                ? Convert.ToSingle(settings["Min Shoot Distance"]) : 10f;
            float maxShootDist = settings != null && settings.ContainsKey("Max Shoot Distance") 
                ? Convert.ToSingle(settings["Max Shoot Distance"]) : 60f;
            float shootInterval = settings != null && settings.ContainsKey("Shoot Interval") 
                ? Convert.ToSingle(settings["Shoot Interval"]) : 0.6f;
            
            // Alternating fire - only one NPC shoots at a time
            if (Time.realtimeSinceStartup - ev.LastShootTime < shootInterval) return;
            ev.LastShootTime = Time.realtimeSinceStartup;
            
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;
            
            // Get list of valid shooters (non-driver, alive NPCs)
            var validShooters = new List<ScientistNPC>();
            foreach (var npc in ev.Shooters)
            {
                if (npc == null || npc.IsDestroyed) continue;
                // Skip driver (index 0)
                if (ev.Shooters.IndexOf(npc) == 0) continue;
                validShooters.Add(npc);
            }
            
            if (validShooters.Count == 0) return;
            
            // Cycle through shooters (alternating fire)
            ev.LastShooterIndex = (ev.LastShooterIndex + 1) % validShooters.Count;
            var shooter = validShooters[ev.LastShooterIndex];
            
            // Check distance to target
            float distToTarget = Vector3.Distance(shooter.transform.position, target.transform.position);
            if (distToTarget < minShootDist || distToTarget > maxShootDist) return;
            
            // Check line of sight
            Vector3 npcEyes = shooter.eyes?.position ?? (shooter.transform.position + Vector3.up * 1.5f);
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;
            
            if (Physics.Linecast(npcEyes, targetPos, LayerMask.GetMask("World", "Construction", "Terrain")))
                return; // Blocked
            
            // Face the target
            Vector3 lookDir = (target.transform.position - shooter.transform.position).normalized;
            shooter.SetAimDirection(lookDir);
            
            // Trigger attack
            var heldEntity = shooter.GetHeldEntity() as BaseProjectile;
            if (heldEntity != null && heldEntity.primaryMagazine.contents > 0)
            {
                // Fire the weapon
                shooter.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                heldEntity.ServerUse();
            }
            else if (heldEntity != null && heldEntity.primaryMagazine.contents == 0)
            {
                // Reload
                heldEntity.primaryMagazine.contents = heldEntity.primaryMagazine.capacity;
            }
        }
        
        private void RecoverFromStuck(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            
            Puts($"[DriveBy] Vehicle stuck, attempting recovery...");
            
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb == null) return;
            
            Vector3 vehiclePos = ev.Vehicle.transform.position;
            Vector3 forward = ev.Vehicle.transform.forward;
            Vector3 backward = -forward;
            Vector3 right = ev.Vehicle.transform.right;
            
            float stuckDuration = ev.StuckTimer > 0 ? Time.realtimeSinceStartup - ev.StuckTimer : 0f;
            
            // CHECK FOR WATER before any reverse maneuver!
            bool waterBehind = IsWaterAhead(vehiclePos, backward, 10f);
            bool waterLeft = IsWaterAhead(vehiclePos, -right, 8f);
            bool waterRight = IsWaterAhead(vehiclePos, right, 8f);
            bool waterAhead = IsWaterAhead(vehiclePos, forward, 10f);
            
            // Stage 1 (0-2s): Try turning/reversing (but NOT into water!)
            if (stuckDuration < 2f)
            {
                if (!waterBehind)
                {
                    rb.velocity = backward * 6f;
                    float turnDir = UnityEngine.Random.value > 0.5f ? 1f : -1f;
                    // Don't turn towards water
                    if (turnDir > 0 && waterRight) turnDir = -1f;
                    if (turnDir < 0 && waterLeft) turnDir = 1f;
                    rb.AddTorque(Vector3.up * turnDir * 400f, ForceMode.Impulse);
                }
                else
                {
                    // Can't reverse - try turning in place
                    float turnDir = !waterRight ? 1f : (!waterLeft ? -1f : 0f);
                    rb.AddTorque(Vector3.up * turnDir * 600f, ForceMode.Impulse);
                    if (!waterAhead)
                    {
                        rb.velocity = forward * 4f;
                    }
                }
            }
            // Stage 2 (2-4s): More aggressive turn
            else if (stuckDuration < 4f)
            {
                if (!waterBehind)
                {
                    rb.velocity = backward * 8f;
                    float turnDir = !waterRight ? 1f : (!waterLeft ? -1f : (UnityEngine.Random.value > 0.5f ? 1f : -1f));
                    rb.AddTorque(Vector3.up * turnDir * 600f, ForceMode.Impulse);
                }
                else
                {
                    // Still can't reverse - aggressive forward turn
                    float turnDir = !waterRight ? 1f : (!waterLeft ? -1f : 0f);
                    rb.AddTorque(Vector3.up * turnDir * 800f, ForceMode.Impulse);
                    if (!waterAhead)
                    {
                        rb.velocity = forward * 6f;
                    }
                }
            }
            // Stage 3 (4s+): Teleport to a better position (NEVER into water)
            else
            {
                // Find direction towards target
                Vector3 toTarget = (ev.TargetPosition - vehiclePos).normalized;
                
                // Try multiple teleport positions - prioritize away from water
                for (int i = 0; i < 10; i++)
                {
                    Vector3 offset = toTarget * 15f + new Vector3(
                        UnityEngine.Random.Range(-10f, 10f), 
                        3f, 
                        UnityEngine.Random.Range(-10f, 10f)
                    );
                    Vector3 testPos = vehiclePos + offset;
                    Vector3 groundPos = GetFlatGroundPosition(testPos);
                    
                    if (groundPos != Vector3.zero && !IsInWater(groundPos) && IsFlatEnough(groundPos))
                    {
                        // Double-check this position isn't near water
                        if (groundPos.y > WaterSystem.OceanLevel + 3f)
                        {
                            // Face towards target
                            Vector3 lookDir = (ev.TargetPosition - groundPos);
                            lookDir.y = 0;
                            if (lookDir.magnitude > 1f)
                            {
                                ev.Vehicle.transform.rotation = Quaternion.LookRotation(lookDir.normalized);
                            }
                            
                            ev.Vehicle.transform.position = groundPos;
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                            
                            Puts($"[DriveBy] Teleported vehicle to safe position {groundPos}");
                            ev.StuckTimer = 0f;
                            return;
                        }
                    }
                }
                
                Puts($"[DriveBy] Could not find valid teleport position away from water!");
                // Last resort: teleport to target area
                Vector3 nearTarget = ev.TargetPosition + new Vector3(UnityEngine.Random.Range(-20f, 20f), 0, UnityEngine.Random.Range(-20f, 20f));
                Vector3 safePos = GetFlatGroundPosition(nearTarget);
                if (safePos != Vector3.zero && safePos.y > WaterSystem.OceanLevel + 3f)
                {
                    ev.Vehicle.transform.position = safePos;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    Puts($"[DriveBy] Emergency teleport to {safePos}");
                    ev.StuckTimer = 0f;
                }
            }
        }
        
        private void DriveVehicle(DriveByEvent ev, Vector3 destination, float maxSpeed = 15f)
        {
            if (ev.Vehicle == null) return;
            
            Vector3 vehiclePos = ev.Vehicle.transform.position;
            Vector3 toTarget = destination - vehiclePos;
            toTarget.y = 0;
            float distToTarget = toTarget.magnitude;
            
            if (distToTarget < 3f) return;
            
            // Calculate steering - IMPROVED with predictive steering
            Vector3 forward = ev.Vehicle.transform.forward;
            forward.y = 0;
            forward.Normalize();
            
            Vector3 right = ev.Vehicle.transform.right;
            right.y = 0;
            right.Normalize();
            
            Vector3 backward = -forward;
            
            float angle = Vector3.SignedAngle(forward, toTarget.normalized, Vector3.up);
            
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            float currentSpeed = rb != null ? rb.velocity.magnitude : 0f;
            
            // Get current vehicle slope
            RaycastHit groundHit;
            float groundSlope = 0f;
            bool goingUphill = false;
            if (Physics.Raycast(vehiclePos + Vector3.up * 2f, Vector3.down, out groundHit, 10f, LayerMask.GetMask("Terrain")))
            {
                groundSlope = Vector3.Angle(groundHit.normal, Vector3.up);
                // Check if we're going uphill or downhill
                Vector3 slopeDir = Vector3.Cross(Vector3.Cross(groundHit.normal, Vector3.up), groundHit.normal);
                goingUphill = Vector3.Dot(forward, slopeDir) < 0;
            }
            
            // CRITICAL: Check for water in ALL directions
            bool waterAhead = IsWaterAhead(vehiclePos, forward, 15f);
            bool waterBehind = IsWaterAhead(vehiclePos, backward, 10f);
            bool waterLeft = IsWaterAhead(vehiclePos, -right, 8f);
            bool waterRight = IsWaterAhead(vehiclePos, right, 8f);
            bool currentlyNearWater = vehiclePos.y < WaterSystem.OceanLevel + 3f;
            
            // Calculate throttle and steering with more stable logic
            float throttle = 1f;
            float steering = 0f;
            bool shouldReverse = false;
            
            // WATER AVOIDANCE - HIGHEST PRIORITY - avoid water at all costs!
            if (waterAhead || currentlyNearWater)
            {
                // Water ahead or we're near water - DON'T GO FORWARD!
                if (!waterBehind && !waterLeft && !waterRight)
                {
                    // Safe to reverse and turn - do a proper turn away from water
                    shouldReverse = true;
                    throttle = -0.7f;
                    // Turn towards target while reversing
                    steering = angle > 0 ? -0.9f : 0.9f;
                    Puts($"[DriveBy] Water ahead! Reversing and turning. WaterBehind:{waterBehind}");
                }
                else if (!waterLeft)
                {
                    // Turn left (go forward but turn hard left)
                    throttle = 0.4f;
                    steering = -1f;
                    Puts($"[DriveBy] Water ahead! Turning left.");
                }
                else if (!waterRight)
                {
                    // Turn right
                    throttle = 0.4f;
                    steering = 1f;
                    Puts($"[DriveBy] Water ahead! Turning right.");
                }
                else if (!waterBehind)
                {
                    // Only safe direction is back - reverse straight
                    shouldReverse = true;
                    throttle = -0.8f;
                    steering = 0f;
                    Puts($"[DriveBy] Water all around except behind! Reversing.");
                }
                else
                {
                    // Surrounded by water - emergency! Try to get to higher ground
                    // Find direction to highest nearby point
                    Vector3 escapeDir = FindEscapeFromWater(vehiclePos);
                    if (escapeDir != Vector3.zero)
                    {
                        float escapeAngle = Vector3.SignedAngle(forward, escapeDir, Vector3.up);
                        throttle = 0.6f;
                        steering = Mathf.Clamp(escapeAngle / 30f, -1f, 1f);
                    }
                    Puts($"[DriveBy] EMERGENCY: Surrounded by water!");
                }
            }
            // NEVER reverse into water - check before any reverse maneuver
            else if (waterBehind)
            {
                // Water behind - NEVER REVERSE
                shouldReverse = false;
                
                // IMPROVED STEERING: Smoother response, less aggressive reversing
                float absAngle = Mathf.Abs(angle);
                
                // Since we can't reverse, we need to turn in place or go forward
                if (absAngle > 120f)
                {
                    // Very wrong direction - but CAN'T reverse due to water
                    // Do a tight forward turn instead
                    throttle = 0.4f;
                    steering = angle > 0 ? 1f : -1f;
                }
                else if (absAngle > 70f)
                {
                    throttle = 0.5f;
                    steering = angle > 0 ? 1f : -1f;
                }
                else if (absAngle > 40f)
                {
                    throttle = 0.7f;
                    steering = Mathf.Sign(angle) * 0.85f;
                }
                else if (absAngle > 20f)
                {
                    throttle = 0.9f;
                    steering = angle / 25f;
                }
                else
                {
                    throttle = 1f;
                    steering = angle / 45f;
                }
            }
            else
            {
                // Normal driving - no water concerns
                float absAngle = Mathf.Abs(angle);
                
                // CONTINUOUS STEERING - Keep turning until facing the player
                // Steering is HELD at full lock while angle is significant
                
                // Only reverse when TRULY facing the wrong way (170+ degrees)
                if (absAngle > 170f)
                {
                    // Almost completely backwards - do a 3-point turn
                    throttle = -0.8f;
                    shouldReverse = true;
                    steering = angle > 0 ? -1f : 1f;  // Full lock while reversing
                }
                else if (absAngle > 90f)
                {
                    // Very wrong direction - FULL LOCK turn until facing target
                    throttle = 0.35f;  // Slow forward
                    steering = angle > 0 ? 1f : -1f;  // FULL LOCK - held continuously
                }
                else if (absAngle > 60f)
                {
                    // Still need significant turn - keep full lock
                    throttle = 0.5f;
                    steering = angle > 0 ? 1f : -1f;  // FULL LOCK
                }
                else if (absAngle > 40f)
                {
                    // Moderate turn needed - strong steering
                    throttle = 0.7f;
                    steering = angle > 0 ? 0.95f : -0.95f;  // Strong steering
                }
                else if (absAngle > 20f)
                {
                    // Small correction
                    throttle = 0.9f;
                    steering = angle / 22f;  // Proportional steering
                }
                else
                {
                    // Nearly aligned - minor adjustments
                    throttle = 1f;
                    steering = angle / 45f;
                }
            }
            
            // UPHILL BOOST - more power when going uphill
            if (goingUphill && groundSlope > 10f && !shouldReverse)
            {
                throttle = Mathf.Max(throttle, 0.8f);
            }
            
            // Speed-based steering adjustment
            if (!shouldReverse && currentSpeed > 10f)
            {
                steering *= 0.6f;
            }
            else if (!shouldReverse && currentSpeed < 4f)
            {
                steering *= 1.2f;
            }
            
            // OBSTACLE AVOIDANCE - but NOT if we're avoiding water (water takes priority)
            if (!shouldReverse && !waterAhead && !currentlyNearWater)
            {
                bool obstacleAhead = ShouldAvoidAhead(vehiclePos, forward);
                bool obstacleLeft = ShouldAvoidAhead(vehiclePos, (forward - right * 0.5f).normalized);
                bool obstacleRight = ShouldAvoidAhead(vehiclePos, (forward + right * 0.5f).normalized);
                
                if (obstacleAhead)
                {
                    if (obstacleLeft && !obstacleRight)
                    {
                        steering = 0.9f;
                        throttle = 0.5f;
                    }
                    else if (obstacleRight && !obstacleLeft)
                    {
                        steering = -0.9f;
                        throttle = 0.5f;
                    }
                    else if (!obstacleLeft && !obstacleRight)
                    {
                        steering = angle > 0 ? 0.7f : -0.7f;
                        throttle = 0.5f;
                    }
                    else if (!waterBehind)
                    {
                        // Only reverse if no water behind
                        throttle = -0.6f;
                        steering = UnityEngine.Random.value > 0.5f ? 0.7f : -0.7f;
                        shouldReverse = true;
                    }
                }
            }
            
            // Clamp steering
            steering = Mathf.Clamp(steering, -1f, 1f);
            
            // Apply inputs to BasicCar - engine always on
            ev.Vehicle.SetFlag(BaseEntity.Flags.Reserved5, true);
            
            if (rb != null)
            {
                // IMPROVED PHYSICS - better hill climbing
                float baseDriveForce = shouldReverse ? 2500f : 5500f;
                
                // Extra power for hills
                if (goingUphill && groundSlope > 5f)
                {
                    baseDriveForce += groundSlope * 100f;
                }
                
                Vector3 force = ev.Vehicle.transform.forward * throttle * baseDriveForce;
                rb.AddForce(force, ForceMode.Force);
                
                // IMPROVED STEERING TORQUE - smoother, speed-sensitive
                float baseTorque = 1200f;
                float speedFactor = Mathf.Clamp01(1f - (currentSpeed / 25f));
                float torque = baseTorque * (0.5f + speedFactor * 0.7f);
                rb.AddTorque(Vector3.up * steering * torque, ForceMode.Force);
                
                // GRIP/DOWNFORCE
                float downforce = 400f + (goingUphill ? groundSlope * 20f : 0f);
                rb.AddForce(Vector3.down * downforce, ForceMode.Force);
                
                // LATERAL GRIP - reduce sideways sliding
                Vector3 lateralVelocity = Vector3.Project(rb.velocity, right);
                rb.AddForce(-lateralVelocity * 1.2f, ForceMode.VelocityChange);
                
                // Speed limiting
                if (currentSpeed > maxSpeed)
                {
                    rb.velocity = rb.velocity.normalized * maxSpeed;
                }
                
                // BOOST when far from target and well-aligned (and not near water)
                if (distToTarget > 60f && Mathf.Abs(angle) < 25f && !shouldReverse && !goingUphill && !waterAhead && !currentlyNearWater)
                {
                    rb.AddForce(ev.Vehicle.transform.forward * 1500f, ForceMode.Force);
                }
            }
        }
        
        // Check if there's water in a specific direction
        private bool IsWaterAhead(Vector3 pos, Vector3 direction, float distance)
        {
            float waterLevel = WaterSystem.OceanLevel;
            
            // Check multiple points along the path
            for (float d = 3f; d <= distance; d += 3f)
            {
                Vector3 checkPos = pos + direction * d;
                checkPos.y = 500f;
                
                RaycastHit hit;
                if (Physics.Raycast(checkPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
                {
                    if (hit.point.y < waterLevel + 1.5f)
                    {
                        return true; // Water detected!
                    }
                }
                else
                {
                    // No terrain hit - might be off map or over water
                    return true;
                }
            }
            
            return false;
        }
        
        // Find a direction to escape from water
        private Vector3 FindEscapeFromWater(Vector3 pos)
        {
            float waterLevel = WaterSystem.OceanLevel;
            float bestHeight = pos.y;
            Vector3 bestDir = Vector3.zero;
            
            // Check 8 directions
            Vector3[] directions = {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized
            };
            
            foreach (var dir in directions)
            {
                Vector3 checkPos = pos + dir * 20f;
                checkPos.y = 500f;
                
                RaycastHit hit;
                if (Physics.Raycast(checkPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
                {
                    if (hit.point.y > bestHeight && hit.point.y > waterLevel + 2f)
                    {
                        bestHeight = hit.point.y;
                        bestDir = dir;
                    }
                }
            }
            
            return bestDir;
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
            
            // Check for obstacles at different heights
            Vector3[] checkHeights = { Vector3.up * 0.5f, Vector3.up * 1.5f, Vector3.up * 2.5f };
            
            foreach (var heightOffset in checkHeights)
            {
                if (Physics.Raycast(pos + heightOffset, forward, out hit, 10f, 
                    LayerMask.GetMask("World", "Construction", "Deployed", "Tree")))
                {
                    return true;
                }
            }
            
            // Check terrain steepness ahead - more permissive for hills
            float[] checkDistances = { 6f, 12f };
            
            foreach (var dist in checkDistances)
            {
                Vector3 aheadPos = pos + forward * dist;
                aheadPos.y = 500f;
                if (Physics.Raycast(aheadPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
                {
                    float angle = Vector3.Angle(hit.normal, Vector3.up);
                    // Allow slopes up to 35 degrees (more permissive for hills)
                    if (angle > 35f) return true;
                    
                    // Check for big height difference (cliff/drop) - only at close range
                    if (dist < 8f)
                    {
                        float heightDiff = Mathf.Abs(hit.point.y - pos.y);
                        if (heightDiff > 6f) return true; // More permissive
                    }
                    
                    // Check if going into water
                    if (hit.point.y < WaterSystem.OceanLevel + 1f) return true;
                }
            }
            
            return false;
        }
        
        private void DismountShooters(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            Vector3 carPos = ev.Vehicle.transform.position;
            
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && !npc.IsDestroyed && npc.IsMounted())
                {
                    npc.DismountObject();
                    
                    // Use PlaceNPCOnGround to properly position and enable combat
                    PlaceNPCOnGround(npc, carPos, ev);
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
                            if (npc.Brain != null)
                            {
                                npc.Brain.SetEnabled(true);
                                
                                // Set target using Brain's memory system
                                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                                if (target != null && npc.Brain.Senses?.Memory != null)
                                {
                                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                                }
                            }
                            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
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
                    // Update memory
                    if (npc.Brain?.Senses?.Memory != null)
                    {
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                    }
                    
                    // If dismounted (not in vehicle), actively chase the target
                    if (!npc.IsMounted() && npc.Brain?.Navigator != null)
                    {
                        npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Fast);
                    }
                }
            }
        }

        private void CleanUpEvent(DriveByEvent ev, bool killAll)
        {
            if (ev == null) return;
            
            // Remove vehicle tracking
            if (ev.Vehicle != null && ev.Vehicle.net != null)
            {
                _driveByVehicles.Remove(ev.Vehicle.net.ID.Value);
            }
            
            // Remove NPC tracking
            foreach (var npc in ev.Shooters)
            {
                if (npc != null && npc.net != null)
                {
                    _driveByNPCs.Remove(npc.net.ID.Value);
                }
            }
            
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