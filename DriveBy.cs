using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "2.3.0")]
    [Description("Premium AI drive-bys: brute-force car chase, escape despawn, destroys natural obstacles but never player builds.")]
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
            public int LastShooterIndex;        // For alternating fire between NPCs
            public bool VehicleDestroyed = false;
            public float LastGoodFacingTime;    // Last time vehicle was roughly facing target
        }

        private List<DriveByEvent> _activeEvents = new List<DriveByEvent>();
        private HashSet<ulong> _driveByNPCs = new HashSet<ulong>();      // Track our NPCs for damage handling
        private HashSet<ulong> _driveByVehicles = new HashSet<ulong>();  // Track our vehicles for collision/obstacle handling
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
                ["Min Shoot Distance"] = 10f,        // Minimum distance to shoot
                ["Max Shoot Distance"] = 60f,        // Maximum distance to shoot
                ["Shoot Interval"] = 0.6f,           // Seconds between each NPC shot (alternating fire)
                ["Spawn Distance From Border"] = 100f,
                ["Vehicle Speed"] = 22f,             // Max vehicle speed (aggressive)
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
            Config["Border Spawns"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = "west",
                ["Northside Vagos"] = "north",
                ["Southside Sureños"] = "south",
                ["Eastside Disciples"] = "east"
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
            // Timing: main AI / movement loop at 0.2s (5Hz) – good balance of performance and reactivity
            _eventTimer = timer.Every(0.2f, UpdateActiveDriveBys);

            var settings = Config["Settings"] as Dictionary<string, object>;
            float interval = settings != null ? Convert.ToSingle(settings["Detection Interval (Seconds)"]) : 30f;

            // Intruder check at configured interval
            timer.Every(interval, CheckForIntruders);
        }

        private void Unload()
        {
            _eventTimer?.Destroy();
            _driveByNPCs.Clear();
            _driveByVehicles.Clear();
            foreach (var ev in _activeEvents) CleanUpEvent(ev, true);
        }

        private bool API_IsDriveByNPC(ulong netId)
        {
            return _driveByNPCs.Contains(netId);
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity is ScientistNPC npc && npc.net != null && _driveByNPCs.Contains(npc.net.ID.Value))
            {
                float damage = info?.damageTypes?.Total() ?? 0f;
                Puts($"[DriveBy] NPC {npc.net.ID} taking {damage:F1} damage from {info?.Initiator?.GetType().Name ?? "unknown"}");

                if (info != null && damage > 0 && npc.IsMounted())
                {
                    info.damageTypes.ScaleAll(1.2f);
                }
            }
        }

        private void OnEntityEnter(TriggerBase trigger, BaseEntity entity)
        {
            var vehicle = trigger.GetComponentInParent<BasicCar>();
            if (vehicle == null || vehicle.net == null) return;
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;

            if (IsDestructibleResource(entity))
            {
                entity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }

        private void OnCollision(BaseEntity entity, Collision collision)
        {
            var vehicle = entity as BasicCar;
            if (vehicle == null || vehicle.net == null) return;
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;

            var collidedEntity = collision?.gameObject?.GetComponentInParent<BaseEntity>();
            if (collidedEntity != null && IsDestructibleResource(collidedEntity))
            {
                collidedEntity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }

        private void OnVehicleHit(BaseVehicle vehicle, HitInfo info)
        {
            if (vehicle == null || vehicle.net == null || info == null) return;
            if (!_driveByVehicles.Contains(vehicle.net.ID.Value)) return;

            var hitEntity = info.HitEntity as BaseEntity;
            if (hitEntity != null && IsDestructibleResource(hitEntity))
            {
                hitEntity.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }

        private bool IsDestructibleResource(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return false;

            // NEVER destroy player-placed items or buildings
            if (entity.OwnerID != 0) return false;

            string prefabName = entity.ShortPrefabName?.ToLower() ?? string.Empty;
            string fullPrefabName = entity.PrefabName?.ToLower() ?? string.Empty;

            if (entity is TreeEntity) return true;
            if (entity is OreResourceEntity) return true;
            if (entity is CollectibleEntity) return true;
            if (entity is ResourceEntity) return true;
            if (entity is LootContainer) return true;

            foreach (var pattern in _destructiblePrefabs)
            {
                if (prefabName.Contains(pattern) || fullPrefabName.Contains(pattern))
                    return true;
            }

            return false;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var npc = entity as ScientistNPC;
            if (npc == null) return;

            if (npc.net != null)
            {
                _driveByNPCs.Remove(npc.net.ID.Value);
            }

            foreach (var ev in _activeEvents)
            {
                if (!ev.Shooters.Contains(npc)) continue;

                ev.Shooters.Remove(npc);

                // If any NPC dies, mark vehicle destroyed and drop remaining NPCs
                if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed && !ev.VehicleDestroyed)
                {
                    ev.VehicleDestroyed = true;
                    Vector3 carPos = ev.Vehicle.transform.position;

                    if (ev.Vehicle.net != null)
                    {
                        _driveByVehicles.Remove(ev.Vehicle.net.ID.Value);
                    }

                    foreach (var shooter in ev.Shooters)
                    {
                        try
                        {
                            if (shooter != null && !shooter.IsDestroyed && shooter.IsMounted())
                            {
                                shooter.DismountObject();
                            }
                        }
                        catch (Exception ex)
                        {
                            Puts($"[DriveBy] Error dismounting NPC: {ex.Message}");
                        }
                    }

                    timer.Once(0.1f, () =>
                    {
                        // We no longer call Kill() here, CleanUpEvent will nuke/disable the car
                        ev.Vehicle = null;

                        foreach (var shooter in ev.Shooters)
                        {
                            if (shooter != null && !shooter.IsDestroyed)
                            {
                                PlaceNPCOnGround(shooter, carPos, ev);
                            }
                        }
                    });
                }
                break;
            }
        }

        private void PlaceNPCOnGround(ScientistNPC npc, Vector3 nearPos, DriveByEvent ev)
        {
            if (npc == null || npc.IsDestroyed) return;

            NavMeshHit navHit;
            Vector3 groundPos = nearPos;

            if (NavMesh.SamplePosition(nearPos, out navHit, 15f, NavMesh.AllAreas))
            {
                groundPos = navHit.position;
            }
            else
            {
                RaycastHit hit;
                Vector3 above = nearPos + Vector3.up * 10f;
                if (Physics.Raycast(above, Vector3.down, out hit, 50f, LayerMask.GetMask("Terrain", "World")))
                {
                    groundPos = hit.point + Vector3.up * 0.1f;
                }
                else
                {
                    // Can't find a safe ground position – give up to avoid NavMesh spam
                    return;
                }
            }

            npc.transform.position = groundPos;

            timer.Once(0.3f, () =>
            {
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

            Vector3 spawnPos = GetRoadSpawnPosition(territoryGang, target.transform.position);
            Puts($"[DriveBy] Spawn position: {spawnPos}");

            if (spawnPos == Vector3.zero)
            {
                Puts($"[DriveBy] ERROR: Could not find valid road spawn for {territoryGang}");
                return;
            }

            Vector3 dirToTarget = (target.transform.position - spawnPos);
            dirToTarget.y = 0;
            dirToTarget.Normalize();
            Quaternion rotation = Quaternion.LookRotation(dirToTarget);

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

            NextTick(() =>
            {
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
                    Phase = DriveByPhase.Chasing,
                    PhaseTimer = Time.realtimeSinceStartup,
                    StuckTimer = 0f,
                    LastShootTime = 0f,
                    LastShooterIndex = 0,
                    LastGoodFacingTime = Time.realtimeSinceStartup
                };

                if (vehicle.net != null)
                {
                    _driveByVehicles.Add(vehicle.net.ID.Value);
                }

                Puts($"[DriveBy] Spawning NPCs...");
                SpawnAndMountNPC(ev, vehicle, 0, true);   // Driver
                SpawnAndMountNPC(ev, vehicle, 1, false);  // Shooter 1
                SpawnAndMountNPC(ev, vehicle, 2, false);  // Shooter 2

                Puts($"[DriveBy] NPCs created: {ev.Shooters.Count}");

                _activeEvents.Add(ev);
                SetCooldown(target.userID);

                // Debug timing: 1Hz report for first 10 seconds
                int tickCount = 0;
                timer.Repeat(1f, 10, () =>
                {
                    tickCount++;
                    if (ev.Vehicle != null && !ev.Vehicle.IsDestroyed)
                    {
                        var pos = ev.Vehicle.transform.position;
                        var rb = ev.Vehicle.GetComponent<Rigidbody>();
                        float speed = rb != null ? rb.velocity.magnitude : 0f;
                        Vector3 fwd = ev.Vehicle.transform.forward;
                        Vector3 toT = (ev.TargetPosition - pos).normalized;
                        float angle = Vector3.SignedAngle(fwd, toT, Vector3.up);
                        float distToTarget = Vector3.Distance(pos, ev.TargetPosition);
                        Puts($"[DriveBy] Tick {tickCount}: pos={pos:F1}, speed={speed:F1}, phase={ev.Phase}, angleToTarget={angle:F1}°, distToTarget={distToTarget:F1}m");
                    }
                    else
                    {
                        Puts($"[DriveBy] Tick {tickCount}: Vehicle DESTROYED!");
                    }
                });

                Puts($"[DriveBy] Drive-by event started successfully! Active events: {_activeEvents.Count}");

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

                basePos.x = Mathf.Clamp(basePos.x, -worldSize + 50f, worldSize - 50f);
                basePos.z = Mathf.Clamp(basePos.z, -worldSize + 50f, worldSize - 50f);

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

            if (npc.net != null)
            {
                _driveByNPCs.Add(npc.net.ID.Value);
            }

            npc.InitializeHealth(150f, 150f);
            npc.startHealth = 150f;
            npc.SetMaxHealth(150f);
            npc.SetHealth(150f);

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

            var navAgent = npc.GetComponent<NavMeshAgent>();
            if (navAgent != null) navAgent.enabled = false;

            mountPoint.mountable.MountPlayer(npc);

            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);
            }

            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null)
            {
                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }

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

            NavMeshHit navHit;
            Vector3 pos = npc.transform.position;
            if (NavMesh.SamplePosition(pos, out navHit, 20f, NavMesh.AllAreas))
            {
                npc.transform.position = navHit.position;
                Puts($"[DriveBy] Moved NPC to NavMesh at {navHit.position}");
            }
            else
            {
                Puts($"[DriveBy] WARNING: No NavMesh found near {pos}, not enabling NavMeshAgent to avoid spam");
                return;
            }

            var navAgent = npc.GetComponent<NavMeshAgent>();
            if (navAgent != null)
            {
                navAgent.enabled = true;
                navAgent.Warp(npc.transform.position);
                navAgent.stoppingDistance = 5f;
                navAgent.speed = 4.5f;
                navAgent.acceleration = 6f;
                navAgent.angularSpeed = 120f;
                navAgent.autoBraking = true;
                navAgent.autoRepath = true;
            }

            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);

                if (npc.Brain.Navigator != null)
                {
                    npc.Brain.Navigator.CanUseNavMesh = true;
                    npc.Brain.Navigator.CanUseAStar = true;
                    npc.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                }
            }

            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null && target.IsAlive())
            {
                Puts($"[DriveBy] Setting target to {target.displayName}");

                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }

                if (npc.Brain?.Events?.Memory?.Entity != null)
                {
                    npc.Brain.Events.Memory.Entity.Set(target, 0);
                }

                if (npc.Brain?.Navigator != null)
                {
                    npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                }
            }

            StartCombatBehavior(npc, ev);
        }

        private void StartCombatBehavior(ScientistNPC npc, DriveByEvent ev)
        {
            timer.Repeat(1f, 0, () =>
            {
                if (npc == null || npc.IsDestroyed || ev.Shooters == null || !ev.Shooters.Contains(npc))
                    return;

                if (npc.IsMounted()) return;

                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target == null || !target.IsAlive()) return;

                float distToTarget = Vector3.Distance(npc.transform.position, target.transform.position);

                if (npc.Brain?.Senses?.Memory != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                }

                if (distToTarget > 8f && npc.Brain?.Navigator != null)
                {
                    npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                }

                if (distToTarget < 25f && distToTarget > 3f)
                {
                    Vector3 lookDir = (target.transform.position - npc.transform.position).normalized;
                    npc.SetAimDirection(lookDir);

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
                ? Convert.ToSingle(settings["Vehicle Speed"]) : 22f;
            float stuckRecoveryTime = settings != null && settings.ContainsKey("Stuck Recovery Time")
                ? Convert.ToSingle(settings["Stuck Recovery Time"]) : 2f;

            for (int i = _activeEvents.Count - 1; i >= 0; i--)
            {
                var ev = _activeEvents[i];

                if (ev.Shooters.Count == 0 || ev.Shooters.All(s => s == null || s.IsDestroyed))
                {
                    CleanUpEvent(ev, true);
                    _activeEvents.RemoveAt(i);
                    continue;
                }

                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target != null && target.IsAlive())
                    ev.TargetPosition = target.transform.position;

                Vector3 eventCenter = ev.Vehicle != null && !ev.Vehicle.IsDestroyed
                    ? ev.Vehicle.transform.position
                    : (ev.Shooters.FirstOrDefault(s => s != null && !s.IsDestroyed)?.transform.position ?? ev.SpawnPosition);
                float distToTarget = Vector3.Distance(eventCenter, ev.TargetPosition);

                if (distToTarget > escapeDist)
                {
                    Puts($"[DriveBy] Player escaped! Distance: {distToTarget:F1}m > {escapeDist}m. Despawning event.");
                    CleanUpEvent(ev, true);
                    _activeEvents.RemoveAt(i);

                    if (target != null)
                    {
                        target.ChatMessage("<color=#44ff44>[ESCAPE]</color> You got away from the drive-by!");
                    }
                    continue;
                }

                if (ev.VehicleDestroyed || ev.Vehicle == null || ev.Vehicle.IsDestroyed)
                {
                    ev.VehicleDestroyed = true;
                    UpdateNPCTargets(ev);
                    continue;
                }

                DestroyObstaclesInFront(ev);
                MakeNPCsShootFromVehicle(ev);

                float distMoved = Vector3.Distance(ev.Vehicle.transform.position, ev.LastVehiclePos);
                if (distMoved < 0.5f && ev.Phase == DriveByPhase.Chasing)
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
                        DriveVehicle(ev, ev.TargetPosition, maxSpeed);
                        UpdateNPCTargets(ev);

                        float chaseDistToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (chaseDistToTarget <= dismountDist)
                        {
                            ev.Phase = DriveByPhase.StoppingToDisembark;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            StopVehicle(ev);
                        }
                        break;

                    case DriveByPhase.StoppingToDisembark:
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

                        float distWhileDismounted = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
                        if (distWhileDismounted > chaseDist)
                        {
                            ev.Phase = DriveByPhase.Remounting;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            RemountShooters(ev);
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

            if (Time.realtimeSinceStartup - ev.LastShootTime < shootInterval) return;
            ev.LastShootTime = Time.realtimeSinceStartup;

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;

            var validShooters = new List<ScientistNPC>();
            foreach (var npc in ev.Shooters)
            {
                if (npc == null || npc.IsDestroyed) continue;
                if (ev.Shooters.IndexOf(npc) == 0) continue; // skip driver
                validShooters.Add(npc);
            }

            if (validShooters.Count == 0) return;

            ev.LastShooterIndex = (ev.LastShooterIndex + 1) % validShooters.Count;
            var shooter = validShooters[ev.LastShooterIndex];

            float distToTarget = Vector3.Distance(shooter.transform.position, target.transform.position);
            if (distToTarget < minShootDist || distToTarget > maxShootDist) return;

            Vector3 npcEyes = shooter.eyes?.position ?? (shooter.transform.position + Vector3.up * 1.5f);
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;

            if (Physics.Linecast(npcEyes, targetPos, LayerMask.GetMask("World", "Construction", "Terrain")))
                return;

            Vector3 lookDir = (target.transform.position - shooter.transform.position).normalized;
            shooter.SetAimDirection(lookDir);

            var heldEntity = shooter.GetHeldEntity() as BaseProjectile;
            if (heldEntity != null && heldEntity.primaryMagazine.contents > 0)
            {
                shooter.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                heldEntity.ServerUse();
            }
            else if (heldEntity != null && heldEntity.primaryMagazine.contents == 0)
            {
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

            bool waterBehind = IsWaterAhead(vehiclePos, backward, 10f);
            bool waterLeft = IsWaterAhead(vehiclePos, -right, 8f);
            bool waterRight = IsWaterAhead(vehiclePos, right, 8f);
            bool waterAhead = IsWaterAhead(vehiclePos, forward, 10f);

            if (stuckDuration < 2f)
            {
                if (!waterBehind)
                {
                    rb.velocity = backward * 6f;
                    float turnDir = UnityEngine.Random.value > 0.5f ? 1f : -1f;
                    if (turnDir > 0 && waterRight) turnDir = -1f;
                    if (turnDir < 0 && waterLeft) turnDir = 1f;
                    rb.AddTorque(Vector3.up * turnDir * 400f, ForceMode.Impulse);
                }
                else
                {
                    float turnDir = !waterRight ? 1f : (!waterLeft ? -1f : 0f);
                    rb.AddTorque(Vector3.up * turnDir * 600f, ForceMode.Impulse);
                    if (!waterAhead)
                    {
                        rb.velocity = forward * 4f;
                    }
                }
            }
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
                    float turnDir = !waterRight ? 1f : (!waterLeft ? -1f : 0f);
                    rb.AddTorque(Vector3.up * turnDir * 800f, ForceMode.Impulse);
                    if (!waterAhead)
                    {
                        rb.velocity = forward * 6f;
                    }
                }
            }
            else
            {
                Vector3 toTarget = (ev.TargetPosition - vehiclePos).normalized;

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
                        if (groundPos.y > WaterSystem.OceanLevel + 3f)
                        {
                            Vector3 lookDir = (ev.TargetPosition - groundPos);
                            lookDir.y = 0;
                            if (lookDir.magnitude > 1f)
                            {
                                ev.Vehicle.transform.rotation = Quaternion.LookRotation(lookDir.normalized);
                            }

                            ev.Vehicle.transform.position = groundPos;
                            rb.velocity = VectorZero();
                            rb.angularVelocity = VectorZero();

                            Puts($"[DriveBy] Teleported vehicle to safe position {groundPos}");
                            ev.StuckTimer = 0f;
                            return;
                        }
                    }
                }

                Puts($"[DriveBy] Could not find valid teleport position away from water!");
                Vector3 nearTarget = ev.TargetPosition + new Vector3(UnityEngine.Random.Range(-20f, 20f), 0, UnityEngine.Random.Range(-20f, 20f));
                Vector3 safePos = GetFlatGroundPosition(nearTarget);
                if (safePos != Vector3.zero && safePos.y > WaterSystem.OceanLevel + 3f)
                {
                    ev.Vehicle.transform.position = safePos;
                    rb.velocity = VectorZero();
                    rb.angularVelocity = VectorZero();
                    Puts($"[DriveBy] Emergency teleport to {safePos}");
                    ev.StuckTimer = 0f;
                }
            }
        }

        /// <summary>
        /// BRUTE-FORCE chase: always full throttle TOWARDS player, never reverse away unless basically stuck at water.
        /// 0.2s timing from UpdateActiveDriveBys gives smooth steering.
        /// </summary>
        private void DriveVehicle(DriveByEvent ev, Vector3 destination, float maxSpeed = 22f)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;

            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb == null) return;

            Vector3 carPos = ev.Vehicle.transform.position;

            // Horizontal vector to target
            Vector3 toTarget = destination - carPos;
            toTarget.y = 0;
            float distToTarget = toTarget.magnitude;
            if (distToTarget < 2f) return;

            Vector3 forward = ev.Vehicle.transform.forward;
            forward.y = 0;
            forward.Normalize();

            // Angle (signed) between forward and target
            float angle = Vector3.SignedAngle(forward, toTarget.normalized, Vector3.up);
            float absAngle = Mathf.Abs(angle);

            float now = Time.realtimeSinceStartup;
            const float goodAngleThreshold = 30f;
            const float maxBadFacingTime = 6f;   // seconds before we hard snap

            // Track how long we've been "not facing" the target
            if (absAngle < goodAngleThreshold)
            {
                ev.LastGoodFacingTime = now;
            }
            else
            {
                if (now - ev.LastGoodFacingTime > maxBadFacingTime)
                {
                    // BRUTE‑FORCE SNAP: face the player directly
                    Vector3 faceDir = toTarget.normalized;
                    faceDir.y = 0;
                    if (faceDir.sqrMagnitude > 0.1f)
                    {
                        ev.Vehicle.transform.rotation = Quaternion.LookRotation(faceDir);
                        rb.velocity = VectorZero();
                        rb.angularVelocity = VectorZero();
                        Puts("[DriveBy] HARD SNAP: rotated sedan to face target.");
                    }
                    ev.LastGoodFacingTime = now;
                }
            }

            float currentSpeed = rb.velocity.magnitude;

            // Simple slope info for downforce/power
            float groundSlope = 0f;
            bool goingUphill = false;
            RaycastHit groundHit;
            if (Physics.Raycast(carPos + Vector3.up * 2f, Vector3.down, out groundHit, 10f, LayerMask.GetMask("Terrain")))
            {
                groundSlope = Vector3.Angle(groundHit.normal, Vector3.up);
                Vector3 slopeDir = Vector3.Cross(Vector3.Cross(groundHit.normal, Vector3.up), groundHit.normal);
                goingUphill = Vector3.Dot(forward, slopeDir) < 0;
            }

            // Basic water check in front and under
            bool waterAhead = IsWaterAhead(carPos, forward, 12f);
            bool currentlyNearWater = carPos.y < WaterSystem.OceanLevel + 2.5f;

            // ==== BRUTE‑FORCE INPUTS ====
            float throttleInput = 1f; // almost always full gas FORWARD
            float steerInput = Mathf.Clamp(angle / 20f, -1f, 1f); // proportional steering
            bool reversing = false;

            // Only reverse for water if we're basically stuck at water
            if ((waterAhead || currentlyNearWater) && currentSpeed < 2f)
            {
                reversing = true;
                throttleInput = -0.6f;
                steerInput = angle > 0 ? -1f : 1f;
            }

            // Little uphill help
            if (!reversing && goingUphill && groundSlope > 8f)
                throttleInput = Mathf.Max(throttleInput, 0.9f);

            // Stronger steering at low speed
            if (!reversing && currentSpeed < 5f)
                steerInput *= 1.6f;

            steerInput = Mathf.Clamp(steerInput, -1f, 1f);
            throttleInput = Mathf.Clamp(throttleInput, -1f, 1f);

            // Engine ON
            ev.Vehicle.SetFlag(BaseEntity.Flags.Reserved5, true);

            // Try to feed inputs into BasicCar (if it supports this)
            try
            {
                ev.Vehicle.SendMessage("SetThrottleInput", throttleInput, SendMessageOptions.DontRequireReceiver);
                ev.Vehicle.SendMessage("SetSteerInput", steerInput, SendMessageOptions.DontRequireReceiver);
                ev.Vehicle.SendMessage("SetBrakeInput", 0f, SendMessageOptions.DontRequireReceiver);
            }
            catch { }

            // ==== RAW PHYSICS FORCES ====
            float baseDriveForce = reversing ? 2200f : 6500f;
            if (goingUphill && groundSlope > 5f && !reversing)
                baseDriveForce += groundSlope * 170f;

            Vector3 driveForce = ev.Vehicle.transform.forward * throttleInput * baseDriveForce;
            rb.AddForce(driveForce, ForceMode.Force);

            // Strong yaw torque to turn the body
            float yawTorqueBase = 2300f;
            float speedFactor = Mathf.Clamp01(1f - (currentSpeed / 35f));
            float yawTorque = yawTorqueBase * (0.6f + speedFactor * 0.7f);
            rb.AddTorque(Vector3.up * steerInput * yawTorque, ForceMode.Force);

            // Extra yaw when angle is big
            if (absAngle > 45f && Mathf.Abs(steerInput) > 0.7f)
            {
                float extra = (absAngle / 180f) * 1100f;
                rb.AddTorque(Vector3.up * steerInput * extra, ForceMode.Force);
            }

            // Downforce so it grips instead of flipping
            float downforce = 480f + (goingUphill ? groundSlope * 25f : 0f);
            rb.AddForce(Vector3.down * downforce, ForceMode.Force);

            // Reduce sideways sliding a bit (but not fully)
            Vector3 right = ev.Vehicle.transform.right;
            right.y = 0;
            Vector3 lateralVel = Vector3.Project(rb.velocity, right);
            rb.AddForce(-lateralVel * 0.85f, ForceMode.VelocityChange);

            // Hard speed cap
            if (currentSpeed > maxSpeed)
                rb.velocity = rb.velocity.normalized * maxSpeed;
        }

        private bool IsWaterAhead(Vector3 pos, Vector3 direction, float distance)
        {
            float waterLevel = WaterSystem.OceanLevel;

            for (float d = 3f; d <= distance; d += 3f)
            {
                Vector3 checkPos = pos + direction * d;
                checkPos.y = 500f;

                RaycastHit hit;
                if (Physics.Raycast(checkPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
                {
                    if (hit.point.y < waterLevel + 1.5f)
                    {
                        return true;
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

        private Vector3 FindEscapeFromWater(Vector3 pos)
        {
            float waterLevel = WaterSystem.OceanLevel;
            float bestHeight = pos.y;
            Vector3 bestDir = Vector3.zero;

            Vector3[] directions =
            {
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
                rb.velocity = VectorZero();
                rb.angularVelocity = VectorZero();
            }

            try
            {
                ev.Vehicle.SendMessage("SetThrottleInput", 0f, SendMessageOptions.DontRequireReceiver);
                ev.Vehicle.SendMessage("SetSteerInput", 0f, SendMessageOptions.DontRequireReceiver);
                ev.Vehicle.SendMessage("SetBrakeInput", 1f, SendMessageOptions.DontRequireReceiver);
            }
            catch { }
        }

        private bool ShouldAvoidAhead(Vector3 pos, Vector3 forward)
        {
            RaycastHit hit;
            Vector3[] checkHeights = { Vector3.up * 0.5f, Vector3.up * 1.5f, Vector3.up * 2.5f };

            foreach (var heightOffset in checkHeights)
            {
                if (Physics.Raycast(pos + heightOffset, forward, out hit, 10f,
                        LayerMask.GetMask("World", "Construction", "Deployed", "Tree")))
                {
                    return true;
                }
            }

            float[] checkDistances = { 6f, 12f };
            foreach (var dist in checkDistances)
            {
                Vector3 aheadPos = pos + forward * dist;
                aheadPos.y = 500f;
                if (Physics.Raycast(aheadPos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain")))
                {
                    float angle = Vector3.Angle(hit.normal, Vector3.up);
                    if (angle > 35f) return true;

                    if (dist < 8f)
                    {
                        float heightDiff = Mathf.Abs(hit.point.y - pos.y);
                        if (heightDiff > 6f) return true;
                    }

                    if (hit.point.y < WaterSystem.OceanLevel + 1f) return true;
                }
            }

            return false;
        }

        private void DestroyObstaclesInFront(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;

            Vector3 pos = ev.Vehicle.transform.position;
            Vector3 forward = ev.Vehicle.transform.forward;

            Vector3 start = pos + Vector3.up * 1f;
            Vector3 end = start + forward * 4f;
            float radius = 1.5f;

            var hits = Physics.OverlapCapsule(
                start,
                end,
                radius,
                LayerMask.GetMask("Default", "Tree", "Deployed", "Construction", "Terrain", "World")
            );

            foreach (var col in hits)
            {
                var ent = col.GetComponentInParent<BaseEntity>();
                if (ent == null) continue;

                if (IsDestructibleResource(ent))
                {
                    ent.Kill(BaseNetworkable.DestroyMode.Gib);
                }
            }
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
                    var navAgent = npc.GetComponent<NavMeshAgent>();
                    if (navAgent != null) navAgent.enabled = false;

                    if (ev.Vehicle.mountPoints != null && seatIndex < ev.Vehicle.mountPoints.Count)
                    {
                        var mountPoint = ev.Vehicle.mountPoints[seatIndex];
                        if (mountPoint?.mountable != null)
                        {
                            npc.transform.position = mountPoint.mountable.transform.position;
                            mountPoint.mountable.MountPlayer(npc);

                            if (npc.Brain != null)
                            {
                                npc.Brain.SetEnabled(true);

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
                if (npc != null && !npc.IsDestroyed && target != null && target.IsAlive())
                {
                    if (npc.Brain?.Senses?.Memory != null)
                    {
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
                    }

                    if (!npc.IsMounted() && npc.Brain?.Navigator != null)
                    {
                        npc.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Fast);
                    }
                }
            }
        }

        /// <summary>
        /// Fully hardened cleanup: never calls BasicCar.Kill() (buggy in your logs),
        /// instead disables, dismounts, and buries the sedan so it is effectively gone.
        /// </summary>
        private void CleanUpEvent(DriveByEvent ev, bool killAll)
        {
            if (ev == null)
                return;

            // 1) Remove from tracking sets
            try
            {
                if (ev.Vehicle != null && ev.Vehicle.net != null)
                {
                    _driveByVehicles.Remove(ev.Vehicle.net.ID.Value);
                }
            }
            catch (Exception ex)
            {
                Puts($"[DriveBy] CleanUpEvent: error removing vehicle from tracking: {ex.Message}");
            }

            try
            {
                if (ev.Shooters != null)
                {
                    foreach (var npc in ev.Shooters)
                    {
                        try
                        {
                            if (npc != null && npc.net != null)
                            {
                                _driveByNPCs.Remove(npc.net.ID.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            Puts($"[DriveBy] CleanUpEvent: error removing NPC from tracking: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Puts($"[DriveBy] CleanUpEvent: error iterating shooters: {ex.Message}");
            }

            // 2) Kill NPCs if requested
            if (killAll && ev.Shooters != null)
            {
                foreach (var npc in ev.Shooters)
                {
                    try
                    {
                        if (npc == null) continue;

                        if (!npc.IsDestroyed)
                        {
                            if (npc.IsMounted())
                            {
                                npc.DismountObject();
                            }
                            npc.Kill();
                        }
                    }
                    catch (Exception ex)
                    {
                        Puts($"[DriveBy] Error killing NPC: {ex.Message}");
                    }
                }
            }

            // 3) Handle the vehicle WITHOUT calling Kill() at all
            try
            {
                var vehicle = ev.Vehicle;
                if (vehicle != null)
                {
                    // Dismount any remaining occupants just in case
                    try
                    {
                        if (vehicle.mountPoints != null)
                        {
                            foreach (var mp in vehicle.mountPoints)
                            {
                                try
                                {
                                    if (mp?.mountable == null) continue;
                                    var mounted = mp.mountable.GetMounted();
                                    if (mounted != null)
                                    {
                                        mounted.DismountObject();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Puts($"[DriveBy] CleanUpEvent: error dismounting occupant: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Puts($"[DriveBy] CleanUpEvent: error iterating mountPoints: {ex.Message}");
                    }

                    try
                    {
                        // Stop physics
                        var rb = vehicle.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.velocity = VectorZero();
                            rb.angularVelocity = VectorZero();

                            // Remove Rigidbody completely so nothing can move it
                            UnityEngine.Object.Destroy(rb);
                        }

                        // Disable all colliders so it cannot be hit or interacted with
                        var cols = vehicle.GetComponentsInChildren<Collider>();
                        foreach (var c in cols)
                            c.enabled = false;

                        // Disable the BasicCar behaviour so it stops ticking
                        var carComponent = vehicle.GetComponent<BasicCar>();
                        if (carComponent != null)
                            carComponent.enabled = false;

                        // Teleport it far below the map so players never see it again
                        vehicle.transform.position = new Vector3(0f, -10000f, 0f);

                        Puts("[DriveBy] CleanUpEvent: vehicle disabled, colliders off, teleported underground (no Kill() used).");
                    }
                    catch (Exception ex)
                    {
                        Puts($"[DriveBy] CleanUpEvent: error disabling/burying vehicle: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Puts($"[DriveBy] Error in vehicle cleanup: {ex.Message}");
            }

            // 4) Clear references so this event is fully dead
            if (ev.Shooters != null)
                ev.Shooters.Clear();

            ev.Vehicle = null;
            ev.VehicleDestroyed = true;
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

        private static Vector3 VectorZero() => Vector3.zero;

        private Vector3 ParseVector3(object obj)
        {
            if (obj is Vector3) return (Vector3)obj;

            if (obj is Dictionary<string, object> dict)
            {
                float x = dict.ContainsKey("x") ? Convert.ToSingle(dict["x"]) : 0f;
                float y = dict.ContainsKey("y") ? Convert.ToSingle(dict["y"]) : 0f;
                float z = dict.ContainsKey("z") ? Convert.ToSingle(dict["z"]) : 0f;
                return new Vector3(x, y, z);
            }

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
