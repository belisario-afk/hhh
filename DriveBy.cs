using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "1.5.0")]
    [Description("Premium AI drive-bys with proper vehicle physics, dismount attacks, terrain awareness.")]
    public class DriveBy : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars;

        private enum DriveByPhase
        {
            DrivingToTarget,    // Car driving towards target
            Stopped,             // Car stopped, NPCs getting out
            Dismounted,          // NPCs on foot shooting
            Remounting,          // NPCs getting back in car
            DrivingAway          // Car driving away
        }

        private class DriveByEvent
        {
            public BasicCar Vehicle;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public Vector3 TargetPosition;
            public Vector3 ExitPosition;
            public ulong TargetID;
            public string GangOwner;
            public DriveByPhase Phase = DriveByPhase.DrivingToTarget;
            public float PhaseTimer;
            public bool VehicleDestroyed = false;
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
                ["Dismount Distance"] = 30f,
                ["Shoot Duration (Seconds)"] = 10f,
                ["Spawn Distance From Border"] = 100f
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
            // Find a road/flat spawn position from territory border
            Vector3 spawnPos = GetRoadSpawnPosition(territoryGang, target.transform.position);
            if (spawnPos == Vector3.zero)
            {
                Puts($"[DriveBy] Could not find valid road spawn for {territoryGang}");
                return;
            }
            
            Vector3 exitPos = GetExitPosition(territoryGang, target.transform.position);
            Quaternion rotation = Quaternion.LookRotation((target.transform.position - spawnPos).normalized);
            rotation.x = 0;
            rotation.z = 0;
            
            BasicCar vehicle = GameManager.server.CreateEntity(PrefabSedan, spawnPos, rotation) as BasicCar;
            if (vehicle == null) return;

            vehicle.Spawn();
            
            DriveByEvent ev = new DriveByEvent
            {
                Vehicle = vehicle,
                TargetPosition = target.transform.position,
                ExitPosition = exitPos,
                TargetID = target.userID,
                GangOwner = territoryGang,
                Phase = DriveByPhase.DrivingToTarget,
                PhaseTimer = Time.realtimeSinceStartup
            };

            // Spawn 3 NPCs properly seated in vehicle
            // Seat 0 = Driver, Seat 1 = Front passenger, Seat 2 = Rear
            SpawnSeatedNPC(ev, 0, true);   // Driver
            SpawnSeatedNPC(ev, 1, false);  // Shooter 1
            SpawnSeatedNPC(ev, 2, false);  // Shooter 2

            _activeEvents.Add(ev);
            SetCooldown(target.userID);
            
            PrintToChat($"<color=#ff4444>[STREET NEWS]</color> Drive-by in progress in {territoryGang} territory!");
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
            Vector3 basePos = targetPos;
            
            // Start from border direction
            switch (direction)
            {
                case "west":
                    basePos = new Vector3(-worldSize + spawnDist, 0, targetPos.z);
                    break;
                case "east":
                    basePos = new Vector3(worldSize - spawnDist, 0, targetPos.z);
                    break;
                case "north":
                    basePos = new Vector3(targetPos.x, 0, worldSize - spawnDist);
                    break;
                case "south":
                    basePos = new Vector3(targetPos.x, 0, -worldSize + spawnDist);
                    break;
            }
            
            // Find flat ground - search for a good spawn point
            for (int i = 0; i < 10; i++)
            {
                Vector3 testPos = basePos + new Vector3(
                    UnityEngine.Random.Range(-20f, 20f), 
                    0, 
                    UnityEngine.Random.Range(-20f, 20f)
                );
                
                testPos = GetFlatGroundPosition(testPos);
                if (testPos != Vector3.zero && !IsInWater(testPos) && IsFlatEnough(testPos))
                {
                    return testPos;
                }
            }
            
            // Fallback to simple ground position
            return GetFlatGroundPosition(basePos);
        }
        
        private Vector3 GetFlatGroundPosition(Vector3 pos)
        {
            RaycastHit hit;
            pos.y = 500f;
            if (Physics.Raycast(pos, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain", "World")))
            {
                return hit.point + Vector3.up * 0.5f;
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

        private void SpawnSeatedNPC(DriveByEvent ev, int seatIndex, bool isDriver)
        {
            // Spawn NPC near vehicle first
            Vector3 spawnPos = ev.Vehicle.transform.position + ev.Vehicle.transform.right * 2f;
            spawnPos = GetFlatGroundPosition(spawnPos);
            if (spawnPos == Vector3.zero) spawnPos = ev.Vehicle.transform.position;
            
            ScientistNPC npc = GameManager.server.CreateEntity(PrefabScientist, spawnPos, Quaternion.identity) as ScientistNPC;
            if (npc == null) return;

            npc.Spawn();
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

            // Mount NPC to vehicle seat
            if (ev.Vehicle.mountPoints != null && seatIndex < ev.Vehicle.mountPoints.Count)
            {
                var mountPoint = ev.Vehicle.mountPoints[seatIndex];
                if (mountPoint?.mountable != null)
                {
                    // Teleport to mount position first
                    npc.transform.position = mountPoint.mountable.transform.position;
                    mountPoint.mountable.MountPlayer(npc);
                }
            }
            
            // Configure AI
            if (isDriver)
            {
                npc.Brain.SetEnabled(false); // Driver doesn't shoot
            }
            else
            {
                npc.Brain.SetEnabled(true);
                BasePlayer target = BasePlayer.FindByID(ev.TargetID);
                if (target != null)
                {
                    npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);
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
                ? Convert.ToSingle(settings["Dismount Distance"]) : 30f;
            float shootDuration = settings != null && settings.ContainsKey("Shoot Duration (Seconds)") 
                ? Convert.ToSingle(settings["Shoot Duration (Seconds)"]) : 10f;
            
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
                
                // If vehicle was destroyed, just let NPCs fight
                if (ev.VehicleDestroyed || ev.Vehicle == null || ev.Vehicle.IsDestroyed)
                {
                    ev.VehicleDestroyed = true;
                    // Keep updating NPC targets
                    UpdateNPCTargets(ev);
                    continue;
                }

                switch (ev.Phase)
                {
                    case DriveByPhase.DrivingToTarget:
                        DriveTowardsTarget(ev, dismountDist);
                        break;
                        
                    case DriveByPhase.Stopped:
                        // Brief pause before dismount
                        if (Time.realtimeSinceStartup - ev.PhaseTimer > 1f)
                        {
                            DismountShooters(ev);
                            ev.Phase = DriveByPhase.Dismounted;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                        
                    case DriveByPhase.Dismounted:
                        UpdateNPCTargets(ev);
                        if (Time.realtimeSinceStartup - ev.PhaseTimer > shootDuration)
                        {
                            ev.Phase = DriveByPhase.Remounting;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                            RemountShooters(ev);
                        }
                        break;
                        
                    case DriveByPhase.Remounting:
                        // Wait for NPCs to get back in
                        bool allMounted = ev.Shooters.All(s => s == null || s.IsDestroyed || s.IsMounted());
                        if (allMounted || Time.realtimeSinceStartup - ev.PhaseTimer > 3f)
                        {
                            ev.Phase = DriveByPhase.DrivingAway;
                            ev.PhaseTimer = Time.realtimeSinceStartup;
                        }
                        break;
                        
                    case DriveByPhase.DrivingAway:
                        DriveAway(ev);
                        
                        // Check if reached exit
                        Vector3 exitGround = GetFlatGroundPosition(ev.ExitPosition);
                        if (exitGround != Vector3.zero && 
                            Vector3.Distance(ev.Vehicle.transform.position, exitGround) < 50f)
                        {
                            CleanUpEvent(ev, true);
                            _activeEvents.RemoveAt(i);
                        }
                        break;
                }
            }
        }
        
        private void DriveTowardsTarget(DriveByEvent ev, float dismountDist)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            
            // Update target position if player moved
            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target != null && target.IsAlive())
                ev.TargetPosition = target.transform.position;
            
            float distToTarget = Vector3.Distance(ev.Vehicle.transform.position, ev.TargetPosition);
            
            // Close enough - stop and dismount
            if (distToTarget <= dismountDist)
            {
                StopVehicle(ev);
                ev.Phase = DriveByPhase.Stopped;
                ev.PhaseTimer = Time.realtimeSinceStartup;
                return;
            }
            
            // Drive towards target using vehicle input
            DriveVehicle(ev, ev.TargetPosition);
        }
        
        private void DriveAway(DriveByEvent ev)
        {
            if (ev.Vehicle == null || ev.Vehicle.IsDestroyed) return;
            
            Vector3 exitGround = GetFlatGroundPosition(ev.ExitPosition);
            if (exitGround == Vector3.zero) exitGround = ev.ExitPosition;
            
            DriveVehicle(ev, exitGround);
        }
        
        private void DriveVehicle(DriveByEvent ev, Vector3 destination)
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
            
            // Use reflection or physics to control car
            var rb = ev.Vehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Apply force for movement
                Vector3 driveForce = ev.Vehicle.transform.forward * throttle * 2000f;
                rb.AddForce(driveForce, ForceMode.Force);
                
                // Apply torque for steering
                rb.AddTorque(Vector3.up * steering * 500f, ForceMode.Force);
                
                // Limit max speed
                if (rb.velocity.magnitude > 15f)
                {
                    rb.velocity = rb.velocity.normalized * 15f;
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
                            
                            if (seatIndex == 0) // Driver
                                npc.Brain.SetEnabled(false);
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
                if (npc != null && !npc.IsDestroyed && !npc.IsMounted() && target != null && target.IsAlive())
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