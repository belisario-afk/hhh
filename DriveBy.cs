using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DriveBy", "Gemini", "1.3.5")]
    [Description("Triggered NPC drive-bys when rivals enter enemy territory. Fully optimized for 2026 uMod API.")]
    public class DriveBy : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars;

        private class DriveByEvent
        {
            public BaseVehicle Vehicle;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public List<Vector3> Path;
            public int CurrentPathIndex;
            public ulong TargetID;
            public string GangOwner;
        }

        private List<DriveByEvent> _activeEvents = new List<DriveByEvent>();
        private Timer _eventTimer;

        private const string PrefabSedan = "assets/content/vehicles/sedan_a/sedan_b.prefab";
        private const string PrefabScientist = "assets/prefabs/npc/scientist/scientistnpc_roaming.prefab";
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
                ["Vehicle Speed"] = 14f,
                ["NPC Accuracy (0.0 to 1.0)"] = 0.65f
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
            
            // Note: Update these coordinates to match your map's actual road system.
            Config["Spawn Points"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = new List<object> { new Vector3(-600, 10, 600), new Vector3(-400, 10, 400) },
                ["Northside Vagos"] = new List<object> { new Vector3(600, 10, 600), new Vector3(400, 10, 400) },
                ["Southside Sureños"] = new List<object> { new Vector3(-600, 10, -600), new Vector3(-400, 10, -400) },
                ["Eastside Disciples"] = new List<object> { new Vector3(600, 10, -600), new Vector3(400, 10, -400) }
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
            var spawnPoints = Config["Spawn Points"] as Dictionary<string, object>;
            if (spawnPoints == null || !spawnPoints.ContainsKey(territoryGang)) return;

            List<object> pathPoints = spawnPoints[territoryGang] as List<object>;
            if (pathPoints == null || pathPoints.Count < 2) return;

            // Parse Vector3 from config (handles both direct Vector3 and dictionary format)
            List<Vector3> parsedPath = new List<Vector3>();
            foreach (var point in pathPoints)
            {
                Vector3 vec = ParseVector3(point);
                parsedPath.Add(vec);
            }
            
            if (parsedPath.Count < 2) return;

            Vector3 spawnPos = parsedPath[0];
            BaseVehicle vehicle = GameManager.server.CreateEntity(PrefabSedan, spawnPos, Quaternion.identity) as BaseVehicle;
            if (vehicle == null) return;

            vehicle.Spawn();
            
            DriveByEvent ev = new DriveByEvent
            {
                Vehicle = vehicle,
                Path = parsedPath,
                CurrentPathIndex = 0,
                TargetID = target.userID,
                GangOwner = territoryGang
            };

            // seatIndex 0: Driver, 1: Front Passenger, 2: Rear Left
            SpawnGangNPC(ev, 0, true); 
            SpawnGangNPC(ev, 1, false); 
            SpawnGangNPC(ev, 2, false); 

            _activeEvents.Add(ev);
            SetCooldown(target.userID);
            
            PrintToChat($"<color=#ff4444>[STREET NEWS]</color> Drive-by in progress in {territoryGang}! Rivals repping on site.");
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
            float speed = settings != null ? Convert.ToSingle(settings["Vehicle Speed"]) : 12f;
            
            for (int i = _activeEvents.Count - 1; i >= 0; i--)
            {
                var ev = _activeEvents[i];
                if (ev?.Vehicle == null || ev.Vehicle.IsDestroyed)
                {
                    CleanUpEvent(ev);
                    _activeEvents.RemoveAt(i);
                    continue;
                }

                Vector3 targetPoint = ev.Path[ev.CurrentPathIndex];
                ev.Vehicle.transform.position = Vector3.MoveTowards(ev.Vehicle.transform.position, targetPoint, speed * 0.1f);
                ev.Vehicle.transform.LookAt(targetPoint);
                ev.Vehicle.SendNetworkUpdate();

                if (Vector3.Distance(ev.Vehicle.transform.position, targetPoint) < 1.5f)
                {
                    ev.CurrentPathIndex++;
                    if (ev.CurrentPathIndex >= ev.Path.Count)
                    {
                        CleanUpEvent(ev);
                        _activeEvents.RemoveAt(i);
                    }
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