using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GangKits", "Gemini", "1.6.0")]
    [Description("Automatic permanent gang outfits and weapons. Includes admin testing tools.")]
    public class GangKits : RustPlugin
    {
        // Track gang kit weapons dropped on ground (to clean up)
        private HashSet<uint> _droppedKitItems = new HashSet<uint>();
        
        // Track which gang each player is blooded into (persists across deaths)
        private Dictionary<ulong, string> _playerGangs = new Dictionary<ulong, string>();
        
        // Track wounded players to prevent kit loss on DBNO (down but not out)
        private HashSet<ulong> _woundedPlayers = new HashSet<ulong>();
        
        [PluginReference]
        private Plugin HoodWars;

        private const string PermAdmin = "hoodwars.admin";

        private class GangKit
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon;
            public ulong WeaponSkin;
        }

        private Dictionary<string, GangKit> _kits;

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            Config["Kits"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637124708, 
                        ["pants"] = 3637161289, 
                        ["mask.balaclava"] = 3637136628 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 0 
                },
                ["Northside Vagos"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637132959, 
                        ["pants"] = 3637162032, 
                        ["mask.bandana"] = 3637144551 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 0 
                },
                ["Southside Sureños"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637133781, 
                        ["pants"] = 3637162360, 
                        ["mask.balaclava"] = 3637136303 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 0 
                },
                ["Eastside Disciples"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637126631, 
                        ["pants"] = 3637163268, 
                        ["mask.bandana"] = 3637149926 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 0 
                }
            };
            SaveConfig();
        }

        private void Init()
        {
            _kits = new Dictionary<string, GangKit>();
            var configKits = Config["Kits"] as Dictionary<string, object>;
            if (configKits == null) return;

            foreach (var kvp in configKits)
            {
                var data = kvp.Value as Dictionary<string, object>;
                _kits[kvp.Key] = new GangKit
                {
                    Clothing = (data["Clothing"] as List<object>).Select(x => x.ToString()).ToList(),
                    Skins = (data["Skins"] as Dictionary<string, object>).ToDictionary(x => x.Key, x => ulong.Parse(x.Value.ToString())),
                    Weapon = data["Weapon"].ToString(),
                    WeaponSkin = ulong.Parse(data["WeaponSkin"].ToString())
                };
            }
        }

        #endregion

        #region Core Logic

        // API method to register a player's gang (called when they blood in)
        private void API_RegisterPlayerGang(ulong playerId, string gangName)
        {
            Puts($"[DEBUG] API_RegisterPlayerGang: playerId={playerId}, gangName={gangName}");
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[playerId] = gangName;
                Puts($"[DEBUG] Player {playerId} registered to gang: {gangName}");
            }
        }
        
        // API method to get a player's registered gang
        private string API_GetPlayerGang(ulong playerId)
        {
            return _playerGangs.ContainsKey(playerId) ? _playerGangs[playerId] : null;
        }

        // API method for external plugins to give a player their gang kit
        private void API_GiveGangKit(BasePlayer player, string gangName = null)
        {
            Puts($"[DEBUG] API_GiveGangKit called for player: {player?.displayName ?? "null"}, gangName: {gangName ?? "null"}");
            
            // If gang name provided, also register them
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[player.userID] = gangName;
            }
            
            GiveGangKit(player, gangName);
        }

        private void GiveGangKit(BasePlayer player, string forcedGang = null)
        {
            if (player == null) 
            {
                Puts("[DEBUG] GiveGangKit: player is null, aborting.");
                return;
            }

            string gangName = forcedGang ?? GetPlayerGang(player);
            Puts($"[DEBUG] GiveGangKit: player={player.displayName}, forcedGang={forcedGang ?? "null"}, resolvedGang={gangName}");
            
            if (string.IsNullOrEmpty(gangName) || gangName == "Neutral Ground" || gangName == "Neutral") 
            {
                Puts($"[DEBUG] GiveGangKit: Gang name is '{gangName}', not a valid gang - aborting.");
                return;
            }

            if (!_kits.TryGetValue(gangName, out var kit)) 
            {
                Puts($"[DEBUG] GiveGangKit: No kit found for gang '{gangName}'. Available kits: {string.Join(", ", _kits.Keys)}");
                return;
            }
            
            Puts($"[DEBUG] GiveGangKit: Found kit for '{gangName}', giving items...");
            int clothingGiven = 0;
            bool weaponGiven = false;

            // 1. Clothing
            foreach (var shortname in kit.Clothing)
            {
                if (forcedGang == null && IsSlotOccupied(player, shortname)) 
                {
                    Puts($"[DEBUG] Skipping {shortname} - slot already occupied");
                    continue;
                }

                ulong skin = kit.Skins.ContainsKey(shortname) ? kit.Skins[shortname] : 0;
                Item item = ItemManager.CreateByName(shortname, 1, skin);
                if (item != null)
                {
                    item.name = "GANG_KIT_ITEM"; 
                    if (item.MoveToContainer(player.inventory.containerWear))
                    {
                        clothingGiven++;
                        Puts($"[DEBUG] Gave {shortname} (skin: {skin}) to wear container");
                    }
                    else if (forcedGang != null && item.MoveToContainer(player.inventory.containerMain))
                    {
                        clothingGiven++;
                        Puts($"[DEBUG] Gave {shortname} (skin: {skin}) to main container (wear was full)");
                    }
                    else 
                    {
                        item.Remove();
                        Puts($"[DEBUG] Failed to give {shortname} - removed item");
                    }
                }
                else
                {
                    Puts($"[DEBUG] Failed to create item: {shortname}");
                }
            }

            // 2. Weapon
            if (forcedGang != null || !HasWeapon(player, kit.Weapon))
            {
                Item weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                if (weapon != null)
                {
                    weapon.name = "GANG_KIT_WEAPON";
                    BaseProjectile proj = weapon.GetHeldEntity() as BaseProjectile;
                    if (proj != null)
                    {
                        proj.primaryMagazine.contents = 0;
                        proj.SendNetworkUpdate();
                    }

                    if (weapon.MoveToContainer(player.inventory.containerBelt))
                    {
                        weaponGiven = true;
                        Puts($"[DEBUG] Gave weapon {kit.Weapon} to belt container");
                    }
                    else if (forcedGang != null && weapon.MoveToContainer(player.inventory.containerMain))
                    {
                        weaponGiven = true;
                        Puts($"[DEBUG] Gave weapon {kit.Weapon} to main container (belt was full)");
                    }
                    else 
                    {
                        weapon.Remove();
                        Puts($"[DEBUG] Failed to give weapon {kit.Weapon} - removed item");
                    }
                }
                else
                {
                    Puts($"[DEBUG] Failed to create weapon: {kit.Weapon}");
                }
            }
            else
            {
                Puts($"[DEBUG] Skipping weapon - player already has {kit.Weapon}");
            }
            
            Puts($"[DEBUG] GiveGangKit complete: {clothingGiven} clothing items, weapon: {weaponGiven}");
        }

        private bool IsSlotOccupied(BasePlayer player, string shortname)
        {
            // Check if slot is occupied by a NON-gang-kit item
            // Gang kit items don't count as "occupied" since they can be replaced
            return player.inventory.containerWear.itemList.Any(item => 
                item.info.shortname == shortname && item.name != "GANG_KIT_ITEM");
        }

        private bool HasWeapon(BasePlayer player, string shortname)
        {
            // Check if player has this weapon (either kit or non-kit version)
            return player.inventory.containerBelt.itemList.Any(i => i.info.shortname == shortname);
        }
        
        private bool HasGangKitWeapon(BasePlayer player, string shortname)
        {
            // Check if player specifically has a gang kit version of this weapon
            return player.inventory.containerBelt.itemList.Any(i => 
                i.info.shortname == shortname && i.name == "GANG_KIT_WEAPON");
        }

        private string GetPlayerGang(BasePlayer player)
        {
            // First check our internal tracking (persistent across deaths/locations)
            if (_playerGangs.ContainsKey(player.userID))
            {
                Puts($"[DEBUG] GetPlayerGang: Found cached gang for {player.displayName}: {_playerGangs[player.userID]}");
                return _playerGangs[player.userID];
            }
            
            // Fall back to HoodWars for fresh players
            if (HoodWars == null) 
            {
                Puts($"[DEBUG] GetPlayerGang: HoodWars not loaded, returning Neutral");
                return "Neutral";
            }
            
            object result = HoodWars.Call("GetPlayerGangName", player.userID);
            string gangName = result?.ToString() ?? "Neutral";
            Puts($"[DEBUG] GetPlayerGang: HoodWars returned '{gangName}' for {player.displayName}");
            
            // Cache the result if it's a valid gang
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[player.userID] = gangName;
            }
            
            return gangName;
        }

        #endregion

        #region Commands

        [ChatCommand("testallkits")]
        private void CmdTestAllKits(BasePlayer player)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            player.inventory.Strip();
            SendReply(player, "<color=#ffff00>ADMIN:</color> Clearing inventory and spawning all 4 Gang Kits for testing...");

            foreach (var gang in _kits.Keys)
            {
                GiveGangKit(player, gang);
            }
            
            SendReply(player, "Check your inventory. All kits have been spawned.");
        }

        #endregion

        #region Hooks

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerRespawned: {player.displayName}");
            
            // Clear any existing gang kit items (fresh start)
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                
                // Force give kit on respawn (true permanent kit behavior)
                string gangName = GetPlayerGang(player);
                Puts($"[DEBUG] OnPlayerRespawned giving kit for gang: {gangName}");
                GiveGangKit(player, gangName); // Force give by passing gang name
            });
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            BasePlayer player = container.playerOwner;
            if (player == null || item == null) return;

            // Only care about wear and belt containers
            if (container != player.inventory.containerWear && container != player.inventory.containerBelt)
                return;
                
            // If it's a gang kit item being removed, don't re-trigger kit give immediately
            // (prevents infinite loops when replacing items)
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
                return;
                
            Puts($"[DEBUG] OnItemRemovedFromContainer: {item.info.shortname} removed from {(container == player.inventory.containerWear ? "wear" : "belt")}");
            
            // Non-kit item removed - check if we need to restore gang kit in that slot
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                GiveGangKit(player); // Only give missing items
            });
        }

        // Track when items are dropped on the ground
        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if (item == null) return;
            
            // If this is a gang kit item dropped on ground
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
            {
                Puts($"[DEBUG] Gang kit item dropped on ground: {item.info.shortname}");
                
                // Find who dropped it
                BasePlayer dropper = item.GetOwnerPlayer();
                
                // If player is wounded/DBNO, don't destroy yet - they might recover
                if (dropper != null && _woundedPlayers.Contains(dropper.userID))
                {
                    Puts($"[DEBUG] Player is wounded - keeping dropped kit item for potential recovery");
                    return;
                }
                
                // Otherwise destroy it - gang kit items can't be dropped while alive
                timer.Once(0.1f, () => {
                    if (entity != null && !entity.IsDestroyed)
                    {
                        Puts($"[DEBUG] Destroying dropped gang kit item: {item.info.shortname}");
                        entity.Kill();
                    }
                });
            }
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            if (corpse == null) return;
            Puts($"[DEBUG] OnPlayerCorpseSpawned: {player?.displayName ?? "unknown"}");
            
            int removed = 0;
            foreach (var container in corpse.containers)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    var item = container.itemList[i];
                    if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
                    {
                        Puts($"[DEBUG] Removing gang kit item from corpse: {item.info.shortname}");
                        item.Remove();
                        removed++;
                    }
                }
            }
            Puts($"[DEBUG] Removed {removed} gang kit items from corpse");
        }
        
        // When player dies, ensure we clean up any dropped gang kit items
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerDeath: {player.displayName}");
            
            // Remove from wounded tracking since they're fully dead now
            _woundedPlayers.Remove(player.userID);
            
            // Note: Corpse handling is done in OnPlayerCorpseSpawned
            // Respawn kit is handled in OnPlayerRespawned
        }
        
        // Player got downed/wounded (DBNO state) - NOT full death
        private void OnPlayerWound(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerWound (DBNO): {player.displayName}");
            _woundedPlayers.Add(player.userID);
        }
        
        // Player recovered from wounded state (got back up)
        private void OnPlayerRecover(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerRecover: {player.displayName}");
            _woundedPlayers.Remove(player.userID);
            
            // Give kit back since they recovered (weapon may have been dropped while wounded)
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                GiveGangKit(player); // Only give missing items
            });
        }

        #endregion
    }
}