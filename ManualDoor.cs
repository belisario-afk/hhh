using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ManualDoor", "Gemini", "3.5.0")]
    [Description("Spawns permanent, non-decaying doors with claim timers, eviction, and admin move/rotate GUI. Integrates with HoodWars for gang-based hotel rooms.")]
    public class ManualDoor : RustPlugin
    {
        private const string DoorPrefab = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab";
        private const string DoubleDoorPrefab = "assets/prefabs/building/door.double.hinged/door.double.hinged.metal.prefab";
        private const string CodeLockPrefab = "assets/prefabs/locks/keypad/lock.code.prefab";

        private const string AdminPermission = "manualdoor.admin";

        private const float ClaimDuration = 1800f;  // 30 minutes
        private const float Warn10 = 600f;          // 10 minutes
        private const float Warn5 = 300f;           // 5 minutes

        private StoredData data;
        private DynamicConfigFile dataFile;

        // HoodWars plugin reference for gang integration
        [PluginReference]
        private Plugin HoodWars;

        // doorNetId -> timer
        private readonly Dictionary<ulong, Timer> claimTimers = new Dictionary<ulong, Timer>();

        // playerId -> doorNetId being edited
        private readonly Dictionary<ulong, ulong> editingDoor = new Dictionary<ulong, ulong>();

        // playerId -> move step
        private readonly Dictionary<ulong, float> moveStep = new Dictionary<ulong, float>();

        // playerId -> rotate step
        private readonly Dictionary<ulong, float> rotateStep = new Dictionary<ulong, float>();

        // playerId -> claim UI id
        private readonly Dictionary<ulong, string> claimUIs = new Dictionary<ulong, string>();

        // doorNetId that we intentionally kill during move/rotate
        private readonly HashSet<ulong> intendedKills = new HashSet<ulong>();

        #region Data classes

        private class StoredData
        {
            public Dictionary<ulong, DoorInfo> Doors = new Dictionary<ulong, DoorInfo>();
        }

        private class DoorInfo
        {
            public float PosX;
            public float PosY;
            public float PosZ;

            public float RotX;
            public float RotY;
            public float RotZ;
            public float RotW;

            public ulong OwnerId;
            public ulong ClaimedBy;
            public double ClaimExpiry;

            public List<ulong> EvictedPlayers = new List<ulong>();
            public string LockCode;
            public bool IsDoubleDoor; // true for double door, false for single door

            public Vector3 GetPosition() => new Vector3(PosX, PosY, PosZ);
            public Quaternion GetRotation() => new Quaternion(RotX, RotY, RotZ, RotW);

            public void SetPosition(Vector3 pos)
            {
                PosX = pos.x;
                PosY = pos.y;
                PosZ = pos.z;
            }

            public void SetRotation(Quaternion rot)
            {
                RotX = rot.x;
                RotY = rot.y;
                RotZ = rot.z;
                RotW = rot.w;
            }

            public DoorInfo Clone()
            {
                return new DoorInfo
                {
                    PosX = PosX,
                    PosY = PosY,
                    PosZ = PosZ,
                    RotX = RotX,
                    RotY = RotY,
                    RotZ = RotZ,
                    RotW = RotW,
                    OwnerId = OwnerId,
                    ClaimedBy = ClaimedBy,
                    ClaimExpiry = ClaimExpiry,
                    EvictedPlayers = new List<ulong>(EvictedPlayers),
                    LockCode = LockCode,
                    IsDoubleDoor = IsDoubleDoor
                };
            }
        }

        private class CodeLockState
        {
            public string Code;
            public bool IsLocked;
            public List<ulong> Whitelist = new List<ulong>();
            public List<ulong> Guests = new List<ulong>();
        }

        #endregion

        #region Oxide hooks

        private void Init()
        {
            permission.RegisterPermission(AdminPermission, this);

            dataFile = Interface.Oxide.DataFileSystem.GetFile("ManualDoors");
            try
            {
                data = dataFile.ReadObject<StoredData>() ?? new StoredData();
            }
            catch
            {
                data = new StoredData();
            }

            if (data.Doors == null)
                data.Doors = new Dictionary<ulong, DoorInfo>();
        }

        private void OnServerInitialized()
        {
            var removeIds = new List<ulong>();

            foreach (var kvp in data.Doors.ToList())
            {
                var id = kvp.Key;
                var info = kvp.Value;

                var existingDoor = BaseNetworkable.serverEntities
                    .OfType<BaseEntity>()
                    .FirstOrDefault(e => e.net != null && e.net.ID.Value == id);

                if (existingDoor == null)
                {
                    var spawned = SpawnDoorFromInfo(info);
                    if (spawned != null)
                    {
                        data.Doors.Remove(id);
                        data.Doors[spawned.net.ID.Value] = info;
                    }
                    else
                    {
                        removeIds.Add(id);
                    }
                }
                else
                {
                    RestoreClaimTimer(existingDoor, info);
                }
            }

            foreach (var id in removeIds)
                data.Doors.Remove(id);

            SaveData();

            timer.Every(1f, UpdateAllClaimUIs);
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyClaimUI(player);
                DestroyAdminUI(player);
            }

            foreach (var t in claimTimers.Values)
                t?.Destroy();

            claimTimers.Clear();
            SaveData();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            DestroyClaimUI(player);
            DestroyAdminUI(player);

            editingDoor.Remove(player.userID);
            moveStep.Remove(player.userID);
            rotateStep.Remove(player.userID);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || entity.net == null)
                return null;

            // no decay on our doors
            if (data.Doors.ContainsKey(entity.net.ID.Value) &&
                info.damageTypes.Has(Rust.DamageType.Decay))
            {
                return false;
            }

            return null;
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null)
                return;

            var id = entity.net.ID.Value;

            if (intendedKills.Contains(id))
            {
                intendedKills.Remove(id);
                return;
            }

            if (data.Doors.ContainsKey(id))
            {
                data.Doors.Remove(id);

                if (claimTimers.TryGetValue(id, out var t))
                {
                    t?.Destroy();
                    claimTimers.Remove(id);
                }

                // if any admin was editing this door, close their UI
                foreach (var kvp in editingDoor.ToList())
                {
                    if (kvp.Value == id)
                    {
                        var player = BasePlayer.FindByID(kvp.Key);
                        if (player != null)
                        {
                            DestroyAdminUI(player);
                            SendReply(player, "<color=#ff6666>The door you were editing was destroyed.</color>");
                        }

                        editingDoor.Remove(kvp.Key);
                    }
                }

                SaveData();
            }
        }

        /// <summary>
        /// Prevent players from removing our doors or their code locks via pickup (E + hammer).
        /// </summary>
        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return null;

            // If it's a code lock with a ManualDoor parent, block
            if (entity is CodeLock codeLock)
            {
                var parent = codeLock.GetParentEntity();
                if (parent != null && parent.net != null && data.Doors.ContainsKey(parent.net.ID.Value))
                {
                    SendReply(player, "<color=#ff6666>You cannot remove this lock.</color>");
                    return false;
                }

                return null;
            }

            // If it's a ManualDoor itself, block pickup (must use /removedoor)
            if (entity.net != null && data.Doors.ContainsKey(entity.net.ID.Value))
            {
                SendReply(player, "<color=#ff6666>You cannot pick up this door. Use /removedoor as admin.</color>");
                return false;
            }

            return null;
        }

        private object CanUseLockedEntity(BasePlayer player, CodeLock codeLock)
        {
            if (player == null || codeLock == null)
                return null;

            var parent = codeLock.GetParentEntity();
            if (parent == null || parent.net == null)
                return null;

            if (!data.Doors.TryGetValue(parent.net.ID.Value, out var info))
                return null;

            if (info.ClaimedBy == 0 || GetTimeRemaining(info) <= 0)
                return null; // unclaimed/expired, allow normal behaviour

            // authorized/whitelisted are fine
            if (info.ClaimedBy == player.userID)
                return true;
            if (codeLock.whitelistPlayers.Contains(player.userID))
                return true;
            if (codeLock.guestPlayers.Contains(player.userID))
                return true;

            return null;
        }

        /// <summary>
        /// OPTIONAL: for Oxide versions that fire this hook when a code is set.
        /// If your server doesn't call it, it will just sit unused (you still have /claimdoor).
        /// </summary>
        private void OnCodeLockCodeEntered(CodeLock codeLock, BasePlayer player, string code)
        {
            TryClaimFromCode(codeLock, player, code);
        }

        private void TryClaimFromCode(CodeLock codeLock, BasePlayer player, string code)
        {
            if (player == null || codeLock == null)
                return;

            var parent = codeLock.GetParentEntity();
            if (parent == null || parent.net == null)
                return;

            if (!data.Doors.TryGetValue(parent.net.ID.Value, out var info))
                return; // not our door

            var remaining = GetTimeRemaining(info);
            var unclaimedOrExpired = info.ClaimedBy == 0 || remaining <= 0;

            if (!unclaimedOrExpired)
                return; // DO NOT reset timer or code on already claimed doors

            if (info.EvictedPlayers.Contains(player.userID))
            {
                SendReply(player, "<color=#ff6666>You were recently evicted from this door and cannot reclaim it.</color>");
                return;
            }

            // Check HoodWars gang restrictions if plugin is loaded
            if (!CanPlayerClaimDoor(player, parent))
            {
                return; // HoodWars blocked the claim
            }

            ClaimDoor(parent, info, player, code);
        }

        // Check with HoodWars if player can claim this door based on gang affiliation
        private bool CanPlayerClaimDoor(BasePlayer player, BaseEntity door)
        {
            if (HoodWars == null || !HoodWars.IsLoaded)
                return true; // HoodWars not loaded, allow claim

            // Call HoodWars API to check if player can claim in this HQ
            var result = HoodWars.Call("API_CanPlayerClaimInHQ", player, door.transform.position);
            if (result is bool canClaim)
            {
                if (!canClaim)
                {
                    // Get the gang name for a more helpful message
                    var gangName = HoodWars.Call("API_GetHQGangName", door.transform.position);
                    SendReply(player, $"<color=#ff6666>You cannot claim doors in {gangName} HQ. Only gang members can claim hotel rooms in their own HQ.</color>");
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Chat commands

        [ChatCommand("spawndoor")]
        private void CmdSpawnDoor(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f))
            {
                SendReply(player, "<color=#ffcc00>Look at the ground to place the door.</color>");
                return;
            }

            var rot = Quaternion.Euler(0f, player.viewAngles.y + 180f, 0f);
            var door = SpawnPermanentDoor(hit.point, rot, player.userID, false);

            if (door != null)
                SendReply(player, "<color=#66ff66>Single door spawned. Use /dooredit while looking at it to adjust.</color>");
        }

        [ChatCommand("spawndoubledoor")]
        private void CmdSpawnDoubleDoor(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f))
            {
                SendReply(player, "<color=#ffcc00>Look at the ground to place the door.</color>");
                return;
            }

            var rot = Quaternion.Euler(0f, player.viewAngles.y + 180f, 0f);
            var door = SpawnPermanentDoor(hit.point, rot, player.userID, true);

            if (door != null)
                SendReply(player, "<color=#66ff66>Double door spawned. Use /dooredit while looking at it to adjust.</color>");
        }

        [ChatCommand("removedoor")]
        private void CmdRemoveDoor(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 5f))
            {
                SendReply(player, "<color=#ffcc00>Look at a ManualDoor.</color>");
                return;
            }

            var ent = hit.GetEntity();
            if (ent is CodeLock)
                ent = ent.GetParentEntity();

            if (ent == null || ent.net == null || !data.Doors.ContainsKey(ent.net.ID.Value))
            {
                SendReply(player, "<color=#ffcc00>That is not a ManualDoor.</color>");
                return;
            }

            data.Doors.Remove(ent.net.ID.Value);
            SaveData();
            ent.Kill();

            SendReply(player, "<color=#66ff66>ManualDoor removed.</color>");
        }

        /// <summary>
        /// Manual claim command:
        /// - If unclaimed or expired: claim + NEW random 4-digit code + start timer + UI.
        /// - If already claimed: NO reset, just message.
        /// - Also creates CodeLock if missing.
        /// </summary>
        [ChatCommand("claimdoor")]
        private void CmdClaimDoor(BasePlayer player, string cmd, string[] args)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 4f))
            {
                SendReply(player, "<color=#ffcc00>Look at a ManualDoor to claim it.</color>");
                return;
            }

            var ent = hit.GetEntity();
            if (ent is CodeLock)
                ent = ent.GetParentEntity();

            if (ent == null || ent.net == null || !data.Doors.TryGetValue(ent.net.ID.Value, out var info))
            {
                SendReply(player, "<color=#ffcc00>That is not a ManualDoor.</color>");
                return;
            }

            var remaining = GetTimeRemaining(info);
            var unclaimedOrExpired = info.ClaimedBy == 0 || remaining <= 0;

            if (!unclaimedOrExpired)
            {
                SendReply(player, "<color=#ff6666>This door is already claimed. Timer will not be reset.</color>");
                return;
            }

            if (info.EvictedPlayers.Contains(player.userID))
            {
                SendReply(player, "<color=#ff6666>You were recently evicted from this door and cannot reclaim it.</color>");
                return;
            }

            // Check HoodWars gang restrictions
            if (!CanPlayerClaimDoor(player, ent))
            {
                return; // HoodWars blocked the claim
            }

            // Attach lock if missing
            var codeLock = ent.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
            if (codeLock == null)
            {
                var lockEnt = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero, Quaternion.identity);
                if (lockEnt == null)
                {
                    SendReply(player, "<color=#ff6666>Failed to create code lock.</color>");
                    return;
                }

                lockEnt.SetParent(ent, "lock");
                lockEnt.OwnerID = info.OwnerId != 0 ? info.OwnerId : player.userID;
                lockEnt.Spawn();
                codeLock = lockEnt as CodeLock;
            }

            // Always generate a new random code for /claimdoor
            var code = UnityEngine.Random.Range(1000, 9999).ToString();

            ClaimDoor(ent, info, player, code);
            SendReply(player, $"<color=#66ff66>Door claimed. Your code is <b>{code}</b>.</color>");
        }

        [ChatCommand("dooredit")]
        private void CmdDoorEdit(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 5f))
            {
                SendReply(player, "<color=#ffcc00>Look at a ManualDoor.</color>");
                return;
            }

            var ent = hit.GetEntity();
            if (ent is CodeLock)
                ent = ent.GetParentEntity();

            if (ent == null || ent.net == null || !data.Doors.ContainsKey(ent.net.ID.Value))
            {
                SendReply(player, "<color=#ffcc00>That is not a ManualDoor.</color>");
                return;
            }

            editingDoor[player.userID] = ent.net.ID.Value;
            moveStep[player.userID] = 0.1f;
            rotateStep[player.userID] = 15f;

            CreateAdminUI(player);
            SendReply(player, "<color=#66ff66>Door edit mode active. Use the GUI to move/rotate.</color>");
        }

        [ChatCommand("doorinfo")]
        private void CmdDoorInfo(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 5f))
            {
                SendReply(player, "<color=#ffcc00>Look at a ManualDoor.</color>");
                return;
            }

            var ent = hit.GetEntity();
            if (ent is CodeLock)
                ent = ent.GetParentEntity();

            if (ent == null || ent.net == null || !data.Doors.TryGetValue(ent.net.ID.Value, out var info))
            {
                SendReply(player, "<color=#ffcc00>That is not a ManualDoor.</color>");
                return;
            }

            string claimant = "None";
            if (info.ClaimedBy != 0)
            {
                var p = BasePlayer.FindByID(info.ClaimedBy);
                claimant = p != null ? p.displayName : info.ClaimedBy.ToString();
            }

            var remaining = GetTimeRemaining(info);
            var timeStr = remaining > 0 ? FormatTime((int)remaining) : "Expired / Unclaimed";

            SendReply(player, "<color=#66ccff>=== ManualDoor Info ===</color>");
            SendReply(player, $"<color=#ffffff>ID: {ent.net.ID.Value}</color>");
            SendReply(player, $"<color=#ffffff>Claimed By: {claimant}</color>");
            SendReply(player, $"<color=#ffffff>Time Remaining: {timeStr}</color>");
            SendReply(player, $"<color=#ffffff>Evicted Players: {info.EvictedPlayers.Count}</color>");
        }

        [ChatCommand("resetdoor")]
        private void CmdResetDoor(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendReply(player, "<color=#ff6666>Permission denied.</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 5f))
            {
                SendReply(player, "<color=#ffcc00>Look at a ManualDoor.</color>");
                return;
            }

            var ent = hit.GetEntity();
            if (ent is CodeLock)
                ent = ent.GetParentEntity();

            if (ent == null || ent.net == null || !data.Doors.TryGetValue(ent.net.ID.Value, out var info))
            {
                SendReply(player, "<color=#ffcc00>That is not a ManualDoor.</color>");
                return;
            }

            info.ClaimedBy = 0;
            info.ClaimExpiry = 0;
            info.EvictedPlayers.Clear();
            info.LockCode = null;

            var cl = ent.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
            if (cl != null)
            {
                cl.code = "";
                cl.SetFlag(BaseEntity.Flags.Locked, false);
                cl.whitelistPlayers.Clear();
                cl.guestPlayers.Clear();
                cl.SendNetworkUpdate();
            }

            if (claimTimers.TryGetValue(ent.net.ID.Value, out var t))
            {
                t?.Destroy();
                claimTimers.Remove(ent.net.ID.Value);
            }

            SaveData();
            SendReply(player, "<color=#66ff66>Door claim reset.</color>");
        }

        #endregion

        #region Console commands (admin GUI)

        [ConsoleCommand("mdoor.moveup")]
        private void CmdMoveUp(ConsoleSystem.Arg arg) => HandleMove(arg, "up");

        [ConsoleCommand("mdoor.movedown")]
        private void CmdMoveDown(ConsoleSystem.Arg arg) => HandleMove(arg, "down");

        [ConsoleCommand("mdoor.moveforward")]
        private void CmdMoveForward(ConsoleSystem.Arg arg) => HandleMove(arg, "forward");

        [ConsoleCommand("mdoor.moveback")]
        private void CmdMoveBack(ConsoleSystem.Arg arg) => HandleMove(arg, "back");

        [ConsoleCommand("mdoor.moveleft")]
        private void CmdMoveLeft(ConsoleSystem.Arg arg) => HandleMove(arg, "left");

        [ConsoleCommand("mdoor.moveright")]
        private void CmdMoveRight(ConsoleSystem.Arg arg) => HandleMove(arg, "right");

        [ConsoleCommand("mdoor.rotateleft")]
        private void CmdRotateLeft(ConsoleSystem.Arg arg) => HandleRotate(arg, "left");

        [ConsoleCommand("mdoor.rotateright")]
        private void CmdRotateRight(ConsoleSystem.Arg arg) => HandleRotate(arg, "right");

        [ConsoleCommand("mdoor.step1")]
        private void CmdStep1(ConsoleSystem.Arg arg) => HandleMoveStep(arg, 0.01f);

        [ConsoleCommand("mdoor.step2")]
        private void CmdStep2(ConsoleSystem.Arg arg) => HandleMoveStep(arg, 0.1f);

        [ConsoleCommand("mdoor.step3")]
        private void CmdStep3(ConsoleSystem.Arg arg) => HandleMoveStep(arg, 0.5f);

        [ConsoleCommand("mdoor.step4")]
        private void CmdStep4(ConsoleSystem.Arg arg) => HandleMoveStep(arg, 1.0f);

        [ConsoleCommand("mdoor.rot1")]
        private void CmdRot1(ConsoleSystem.Arg arg) => HandleRotateStep(arg, 5f);

        [ConsoleCommand("mdoor.rot2")]
        private void CmdRot2(ConsoleSystem.Arg arg) => HandleRotateStep(arg, 15f);

        [ConsoleCommand("mdoor.rot3")]
        private void CmdRot3(ConsoleSystem.Arg arg) => HandleRotateStep(arg, 45f);

        [ConsoleCommand("mdoor.rot4")]
        private void CmdRot4(ConsoleSystem.Arg arg) => HandleRotateStep(arg, 90f);

        [ConsoleCommand("mdoor.close")]
        private void CmdCloseUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            DestroyAdminUI(player);
            editingDoor.Remove(player.userID);
            moveStep.Remove(player.userID);
            rotateStep.Remove(player.userID);

            SendReply(player, "<color=#66ff66>Door edit mode closed.</color>");
        }

        private void HandleMove(ConsoleSystem.Arg arg, string dir)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission)) return;

            if (!editingDoor.TryGetValue(player.userID, out var doorId))
            {
                SendReply(player, "<color=#ff6666>No door selected. Use /dooredit first.</color>");
                return;
            }

            var step = moveStep.TryGetValue(player.userID, out var s) ? s : 0.1f;

            var fwd = player.eyes.HeadForward();
            fwd.y = 0f;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            right.Normalize();

            var offset = Vector3.zero;

            switch (dir)
            {
                case "up": offset = Vector3.up * step; break;
                case "down": offset = Vector3.down * step; break;
                case "forward": offset = fwd * step; break;
                case "back": offset = -fwd * step; break;
                case "left": offset = -right * step; break;
                case "right": offset = right * step; break;
            }

            MoveDoor(player, doorId, offset);
        }

        private void HandleRotate(ConsoleSystem.Arg arg, string dir)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission)) return;

            if (!editingDoor.TryGetValue(player.userID, out var doorId))
            {
                SendReply(player, "<color=#ff6666>No door selected. Use /dooredit first.</color>");
                return;
            }

            var step = rotateStep.TryGetValue(player.userID, out var s) ? s : 15f;
            var angle = dir == "left" ? -step : step;

            RotateDoor(player, doorId, angle);
        }

        private void HandleMoveStep(ConsoleSystem.Arg arg, float step)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission)) return;

            moveStep[player.userID] = Mathf.Clamp(step, 0.01f, 5f);
            CreateAdminUI(player);
        }

        private void HandleRotateStep(ConsoleSystem.Arg arg, float step)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission)) return;

            rotateStep[player.userID] = Mathf.Clamp(step, 1f, 180f);
            CreateAdminUI(player);
        }

        #endregion

        #region Door manipulation

        private void MoveDoor(BasePlayer player, ulong doorId, Vector3 offset)
        {
            if (!data.Doors.TryGetValue(doorId, out var info))
            {
                SendReply(player, "<color=#ff6666>Door no longer exists.</color>");
                DestroyAdminUI(player);
                editingDoor.Remove(player.userID);
                return;
            }

            var state = GetCodeLockState(doorId);
            var newPos = info.GetPosition() + offset;
            var rot = info.GetRotation();
            var newInfo = info.Clone();
            newInfo.SetPosition(newPos);

            intendedKills.Add(doorId);
            KillDoorById(doorId);
            data.Doors.Remove(doorId);

            if (claimTimers.TryGetValue(doorId, out var t))
            {
                t?.Destroy();
                claimTimers.Remove(doorId);
            }

            var newDoor = SpawnDoorWithState(newPos, rot, newInfo, state);
            if (newDoor == null)
                return;

            var newId = newDoor.net.ID.Value;
            data.Doors[newId] = newInfo;
            editingDoor[player.userID] = newId;

            if (newInfo.ClaimedBy != 0 && GetTimeRemaining(newInfo) > 0)
                StartClaimTimer(newDoor, newInfo);

            SaveData();
        }

        private void RotateDoor(BasePlayer player, ulong doorId, float angle)
        {
            if (!data.Doors.TryGetValue(doorId, out var info))
            {
                SendReply(player, "<color=#ff6666>Door no longer exists.</color>");
                DestroyAdminUI(player);
                editingDoor.Remove(player.userID);
                return;
            }

            var state = GetCodeLockState(doorId);
            var pos = info.GetPosition();
            var newRot = info.GetRotation() * Quaternion.Euler(0f, angle, 0f);
            var newInfo = info.Clone();
            newInfo.SetRotation(newRot);

            intendedKills.Add(doorId);
            KillDoorById(doorId);
            data.Doors.Remove(doorId);

            if (claimTimers.TryGetValue(doorId, out var t))
            {
                t?.Destroy();
                claimTimers.Remove(doorId);
            }

            var newDoor = SpawnDoorWithState(pos, newRot, newInfo, state);
            if (newDoor == null)
                return;

            var newId = newDoor.net.ID.Value;
            data.Doors[newId] = newInfo;
            editingDoor[player.userID] = newId;

            if (newInfo.ClaimedBy != 0 && GetTimeRemaining(newInfo) > 0)
                StartClaimTimer(newDoor, newInfo);

            SaveData();
        }

        private CodeLockState GetCodeLockState(ulong doorId)
        {
            var door = BaseNetworkable.serverEntities
                .OfType<BaseEntity>()
                .FirstOrDefault(e => e.net != null && e.net.ID.Value == doorId);

            if (door == null)
                return null;

            var cl = door.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
            if (cl == null)
                return null;

            return new CodeLockState
            {
                Code = cl.code,
                IsLocked = cl.IsLocked(),
                Whitelist = cl.whitelistPlayers.ToList(),
                Guests = cl.guestPlayers.ToList()
            };
        }

        private void KillDoorById(ulong doorId)
        {
            var door = BaseNetworkable.serverEntities
                .OfType<BaseEntity>()
                .FirstOrDefault(e => e.net != null && e.net.ID.Value == doorId);

            door?.Kill();
        }

        #endregion

        #region Door spawn

        private BaseEntity SpawnPermanentDoor(Vector3 pos, Quaternion rot, ulong ownerId, bool isDoubleDoor)
        {
            var prefab = isDoubleDoor ? DoubleDoorPrefab : DoorPrefab;
            var ent = GameManager.server.CreateEntity(prefab, pos, rot);
            if (ent == null)
                return null;

            ent.OwnerID = ownerId;

            var gw = ent.GetComponent<GroundWatch>();
            if (gw != null) gw.enabled = false;

            var stab = ent.GetComponent<StabilityEntity>();
            if (stab != null) stab.grounded = true;

            if (ent is DecayEntity de)
                de.decay = null;

            ent.Spawn();

            var info = new DoorInfo
            {
                OwnerId = ownerId,
                ClaimedBy = 0,
                ClaimExpiry = 0,
                IsDoubleDoor = isDoubleDoor
            };
            info.SetPosition(pos);
            info.SetRotation(rot);

            data.Doors[ent.net.ID.Value] = info;
            SaveData();

            // NOTE: NO lock attached here anymore. Lock is created on /claimdoor (if missing).
            return ent;
        }

        private BaseEntity SpawnDoorFromInfo(DoorInfo info)
        {
            var prefab = info.IsDoubleDoor ? DoubleDoorPrefab : DoorPrefab;
            var ent = GameManager.server.CreateEntity(prefab, info.GetPosition(), info.GetRotation());
            if (ent == null)
                return null;

            ent.OwnerID = info.OwnerId;

            var gw = ent.GetComponent<GroundWatch>();
            if (gw != null) gw.enabled = false;

            var stab = ent.GetComponent<StabilityEntity>();
            if (stab != null) stab.grounded = true;

            if (ent is DecayEntity de)
                de.decay = null;

            ent.Spawn();

            // If we had a lock code saved, reattach a lock and restore that code
            if (!string.IsNullOrEmpty(info.LockCode))
            {
                var cl = AttachCodeLock(ent, info.OwnerId);
                if (cl != null)
                {
                    cl.code = info.LockCode;
                    cl.SetFlag(BaseEntity.Flags.Locked, true);
                    if (info.ClaimedBy != 0)
                        cl.whitelistPlayers.Add(info.ClaimedBy);
                    cl.SendNetworkUpdate();
                }
            }

            RestoreClaimTimer(ent, info);
            return ent;
        }

        private BaseEntity SpawnDoorWithState(Vector3 pos, Quaternion rot, DoorInfo info, CodeLockState state)
        {
            var prefab = info.IsDoubleDoor ? DoubleDoorPrefab : DoorPrefab;
            var ent = GameManager.server.CreateEntity(prefab, pos, rot);
            if (ent == null)
                return null;

            ent.OwnerID = info.OwnerId;

            var gw = ent.GetComponent<GroundWatch>();
            if (gw != null) gw.enabled = false;

            var stab = ent.GetComponent<StabilityEntity>();
            if (stab != null) stab.grounded = true;

            if (ent is DecayEntity de)
                de.decay = null;

            ent.Spawn();

            if (state != null && !string.IsNullOrEmpty(state.Code))
            {
                var lockEnt = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero, Quaternion.identity);
                if (lockEnt != null)
                {
                    lockEnt.SetParent(ent, "lock");
                    lockEnt.OwnerID = info.OwnerId;
                    lockEnt.Spawn();

                    var cl = lockEnt as CodeLock;
                    if (cl != null)
                    {
                        cl.code = state.Code;
                        cl.SetFlag(BaseEntity.Flags.Locked, state.IsLocked);
                        foreach (var id in state.Whitelist)
                            cl.whitelistPlayers.Add(id);
                        foreach (var id in state.Guests)
                            cl.guestPlayers.Add(id);
                        cl.SendNetworkUpdate();
                    }
                }
            }

            return ent;
        }

        private CodeLock AttachCodeLock(BaseEntity door, ulong ownerId)
        {
            var lockEnt = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero, Quaternion.identity);
            if (lockEnt == null)
                return null;

            lockEnt.SetParent(door, "lock");
            lockEnt.OwnerID = ownerId;
            lockEnt.Spawn();

            return lockEnt as CodeLock;
        }

        #endregion

        #region Claim system

        private void ClaimDoor(BaseEntity door, DoorInfo info, BasePlayer player, string code)
        {
            info.EvictedPlayers.Clear();
            info.ClaimedBy = player.userID;
            info.ClaimExpiry = GetCurrentTime() + ClaimDuration;
            info.LockCode = code;

            var cl = door.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
            if (cl != null)
            {
                cl.code = code;
                cl.SetFlag(BaseEntity.Flags.Locked, true);
                cl.whitelistPlayers.Clear();
                cl.whitelistPlayers.Add(player.userID);
                cl.SendNetworkUpdate();
            }

            SaveData();
            StartClaimTimer(door, info);

            SendReply(player, "<color=#66ff66>You claimed this door for 30 minutes.</color>");
            CreateClaimUI(player, door.net.ID.Value);
        }

        private void StartClaimTimer(BaseEntity door, DoorInfo info)
        {
            var id = door.net.ID.Value;

            if (claimTimers.TryGetValue(id, out var old))
            {
                old?.Destroy();
                claimTimers.Remove(id);
            }

            var warned10 = false;
            var warned5 = false;

            claimTimers[id] = timer.Every(1f, () =>
            {
                if (!data.Doors.TryGetValue(id, out var di))
                {
                    if (claimTimers.TryGetValue(id, out var t))
                    {
                        t?.Destroy();
                        claimTimers.Remove(id);
                    }
                    return;
                }

                var remaining = GetTimeRemaining(di);

                if (!warned10 && remaining <= Warn10 && remaining > Warn10 - 2)
                {
                    warned10 = true;
                    NotifyClaimant(di.ClaimedBy, "<color=#ffcc00>Warning: your door claim expires in 10 minutes.</color>");
                }

                if (!warned5 && remaining <= Warn5 && remaining > Warn5 - 2)
                {
                    warned5 = true;
                    NotifyClaimant(di.ClaimedBy, "<color=#ff6600>Warning: your door claim expires in 5 minutes.</color>");
                }

                if (remaining <= 0)
                {
                    ExpireClaim(id, di);

                    if (claimTimers.TryGetValue(id, out var t))
                    {
                        t?.Destroy();
                        claimTimers.Remove(id);
                    }
                }
            });
        }

        private void RestoreClaimTimer(BaseEntity door, DoorInfo info)
        {
            if (info.ClaimedBy == 0)
                return;

            var remaining = GetTimeRemaining(info);
            if (remaining <= 0)
            {
                ExpireClaim(door.net.ID.Value, info);
            }
            else
            {
                StartClaimTimer(door, info);
            }
        }

        private void ExpireClaim(ulong doorId, DoorInfo info)
        {
            if (info.ClaimedBy != 0)
            {
                info.EvictedPlayers.Add(info.ClaimedBy);
                NotifyClaimant(info.ClaimedBy, "<color=#ff6666>Your door claim has expired. The door is now free to claim.</color>");

                var p = BasePlayer.FindByID(info.ClaimedBy);
                if (p != null)
                    DestroyClaimUI(p);
            }

            info.ClaimedBy = 0;
            info.ClaimExpiry = 0;
            info.LockCode = null;

            var door = BaseNetworkable.serverEntities
                .OfType<BaseEntity>()
                .FirstOrDefault(e => e.net != null && e.net.ID.Value == doorId);

            if (door != null)
            {
                var cl = door.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
                if (cl != null)
                {
                    cl.code = "";
                    cl.SetFlag(BaseEntity.Flags.Locked, false);
                    cl.whitelistPlayers.Clear();
                    cl.guestPlayers.Clear();
                    cl.SendNetworkUpdate();
                }
            }

            SaveData();
        }

        private void NotifyClaimant(ulong userId, string msg)
        {
            var p = BasePlayer.FindByID(userId);
            if (p != null && p.IsConnected)
                SendReply(p, msg);
        }

        // API method for HoodWars to force-expire a claim (for testing eviction timer)
        private bool API_ForceExpireClaim(ulong doorId)
        {
            if (!data.Doors.TryGetValue(doorId, out var info))
                return false;

            if (info.ClaimedBy == 0)
                return false;

            ExpireClaim(doorId, info);
            return true;
        }

        // API method to check if an entity is a door managed by this plugin
        private bool API_IsManagedDoor(ulong netId)
        {
            return data.Doors.ContainsKey(netId);
        }

        // API method to get door info for HoodWars
        private Dictionary<string, object> API_GetDoorInfo(ulong doorId)
        {
            if (!data.Doors.TryGetValue(doorId, out var info))
                return null;

            return new Dictionary<string, object>
            {
                ["ClaimedBy"] = info.ClaimedBy,
                ["ClaimExpiry"] = info.ClaimExpiry,
                ["TimeRemaining"] = GetTimeRemaining(info),
                ["EvictedCount"] = info.EvictedPlayers.Count
            };
        }

        #endregion

        #region Claim UI

        private void CreateClaimUI(BasePlayer player, ulong doorId)
        {
            DestroyClaimUI(player);

            if (!data.Doors.TryGetValue(doorId, out var info))
                return;

            if (info.ClaimedBy != player.userID)
                return;

            var uiId = $"ManualDoor_Claim_{player.userID}";
            claimUIs[player.userID] = uiId;

            UpdateClaimUI(player, doorId, info);
        }

        private void UpdateClaimUI(BasePlayer player, ulong doorId, DoorInfo info)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!claimUIs.TryGetValue(player.userID, out var uiId))
                uiId = $"ManualDoor_Claim_{player.userID}";

            CuiHelper.DestroyUi(player, uiId);

            var remaining = GetTimeRemaining(info);
            if (remaining <= 0)
            {
                claimUIs.Remove(player.userID);
                return;
            }

            var timeStr = FormatTime((int)remaining);
            string color;
            if (remaining > Warn10)
                color = "0.2 0.8 0.2 0.9";
            else if (remaining > Warn5)
                color = "0.9 0.7 0.1 0.9";
            else
                color = "0.9 0.2 0.2 0.9";

            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.85" },
                RectTransform = { AnchorMin = "0.85 0.92", AnchorMax = "0.995 0.99" },
                CursorEnabled = false
            }, "Overlay", uiId);

            elements.Add(new CuiLabel
            {
                Text = { Text = "DOOR CLAIM", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 1" }
            }, uiId);

            elements.Add(new CuiLabel
            {
                Text = { Text = timeStr, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.5" }
            }, uiId);

            CuiHelper.AddUi(player, elements);
        }

        private void UpdateAllClaimUIs()
        {
            foreach (var kvp in claimUIs.ToList())
            {
                var player = BasePlayer.FindByID(kvp.Key);
                if (player == null || !player.IsConnected)
                {
                    claimUIs.Remove(kvp.Key);
                    continue;
                }

                var doorPair = data.Doors.FirstOrDefault(d => d.Value.ClaimedBy == player.userID);
                if (doorPair.Value == null || GetTimeRemaining(doorPair.Value) <= 0)
                {
                    DestroyClaimUI(player);
                    continue;
                }

                UpdateClaimUI(player, doorPair.Key, doorPair.Value);
            }

            // ensure new claimants get UI
            foreach (var d in data.Doors)
            {
                var info = d.Value;
                if (info.ClaimedBy == 0) continue;
                if (GetTimeRemaining(info) <= 0) continue;

                var p = BasePlayer.FindByID(info.ClaimedBy);
                if (p != null && p.IsConnected && !claimUIs.ContainsKey(p.userID))
                    CreateClaimUI(p, d.Key);
            }
        }

        private void DestroyClaimUI(BasePlayer player)
        {
            if (player == null) return;

            if (claimUIs.TryGetValue(player.userID, out var uiId))
            {
                CuiHelper.DestroyUi(player, uiId);
                claimUIs.Remove(player.userID);
            }
        }

        #endregion

        #region Admin UI

        private void CreateAdminUI(BasePlayer player)
        {
            DestroyAdminUI(player);

            var m = moveStep.TryGetValue(player.userID, out var ms) ? ms : 0.1f;
            var r = rotateStep.TryGetValue(player.userID, out var rs) ? rs : 15f;

            var elements = new CuiElementContainer();
            const string panel = "ManualDoor_AdminUI";

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.95" },
                RectTransform = { AnchorMin = "0.01 0.3", AnchorMax = "0.22 0.7" },
                CursorEnabled = true
            }, "Overlay", panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = "DOOR EDITOR", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.9 0.7 0.2 1" },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.98" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 0.8", Command = "mdoor.close" },
                RectTransform = { AnchorMin = "0.85 0.88", AnchorMax = "0.98 0.98" },
                Text = { Text = "X", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            // Position section
            elements.Add(new CuiLabel
            {
                Text = { Text = "--- POSITION ---", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.6 0.8 1 1" },
                RectTransform = { AnchorMin = "0 0.78", AnchorMax = "1 0.86" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.3 0.5 0.3 0.9", Command = "mdoor.moveup" },
                RectTransform = { AnchorMin = "0.38 0.68", AnchorMax = "0.62 0.77" },
                Text = { Text = "UP", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.3 0.4 0.5 0.9", Command = "mdoor.moveleft" },
                RectTransform = { AnchorMin = "0.04 0.58", AnchorMax = "0.32 0.67" },
                Text = { Text = "LEFT", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.5 0.4 0.9", Command = "mdoor.moveforward" },
                RectTransform = { AnchorMin = "0.35 0.58", AnchorMax = "0.65 0.67" },
                Text = { Text = "FWD", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.3 0.4 0.5 0.9", Command = "mdoor.moveright" },
                RectTransform = { AnchorMin = "0.68 0.58", AnchorMax = "0.96 0.67" },
                Text = { Text = "RIGHT", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.5 0.4 0.9", Command = "mdoor.moveback" },
                RectTransform = { AnchorMin = "0.04 0.48", AnchorMax = "0.48 0.57" },
                Text = { Text = "BACK", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.5 0.3 0.3 0.9", Command = "mdoor.movedown" },
                RectTransform = { AnchorMin = "0.52 0.48", AnchorMax = "0.96 0.57" },
                Text = { Text = "DOWN", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = $"Step: {m:F2}m", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.25 0.40", AnchorMax = "0.75 0.47" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.step1" },
                RectTransform = { AnchorMin = "0.04 0.33", AnchorMax = "0.24 0.40" },
                Text = { Text = "0.01", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.step2" },
                RectTransform = { AnchorMin = "0.27 0.33", AnchorMax = "0.47 0.40" },
                Text = { Text = "0.1", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.step3" },
                RectTransform = { AnchorMin = "0.50 0.33", AnchorMax = "0.70 0.40" },
                Text = { Text = "0.5", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.step4" },
                RectTransform = { AnchorMin = "0.73 0.33", AnchorMax = "0.96 0.40" },
                Text = { Text = "1.0", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            // Rotation section
            elements.Add(new CuiLabel
            {
                Text = { Text = "--- ROTATION ---", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0.6 1" },
                RectTransform = { AnchorMin = "0 0.24", AnchorMax = "1 0.32" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.5 0.4 0.2 0.9", Command = "mdoor.rotateleft" },
                RectTransform = { AnchorMin = "0.04 0.14", AnchorMax = "0.48 0.23" },
                Text = { Text = "< LEFT", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.5 0.4 0.2 0.9", Command = "mdoor.rotateright" },
                RectTransform = { AnchorMin = "0.52 0.14", AnchorMax = "0.96 0.23" },
                Text = { Text = "RIGHT >", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = $"Step: {r:F0} deg", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.25 0.07", AnchorMax = "0.75 0.13" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.rot1" },
                RectTransform = { AnchorMin = "0.04 0.01", AnchorMax = "0.24 0.07" },
                Text = { Text = "5", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.rot2" },
                RectTransform = { AnchorMin = "0.27 0.01", AnchorMax = "0.47 0.07" },
                Text = { Text = "15", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.rot3" },
                RectTransform = { AnchorMin = "0.50 0.01", AnchorMax = "0.70 0.07" },
                Text = { Text = "45", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 0.9", Command = "mdoor.rot4" },
                RectTransform = { AnchorMin = "0.73 0.01", AnchorMax = "0.96 0.07" },
                Text = { Text = "90", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        private void DestroyAdminUI(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, "ManualDoor_AdminUI");
        }

        #endregion

        #region Utils

        private double GetCurrentTime()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        private double GetTimeRemaining(DoorInfo info)
        {
            if (info.ClaimExpiry == 0)
                return 0;

            return info.ClaimExpiry - GetCurrentTime();
        }

        private string FormatTime(int seconds)
        {
            var m = seconds / 60;
            var s = seconds % 60;
            return $"{m:D2}:{s:D2}";
        }

        private void SaveData()
        {
            dataFile.WriteObject(data);
        }

        #endregion
    }
}