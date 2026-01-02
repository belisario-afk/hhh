using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GangKits", "Gemini", "1.4.0")]
    [Description("Automatic permanent gang outfits and weapons. Includes admin testing tools.")]
    public class GangKits : RustPlugin
    {
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

        // API method for external plugins to give a player their gang kit
        private void API_GiveGangKit(BasePlayer player, string gangName = null)
        {
            Puts($"[DEBUG] API_GiveGangKit called for player: {player?.displayName ?? "null"}, gangName: {gangName ?? "null"}");
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
            return player.inventory.containerWear.itemList.Any(item => item.info.shortname == shortname);
        }

        private bool HasWeapon(BasePlayer player, string shortname)
        {
            return player.inventory.containerBelt.itemList.Any(i => i.info.shortname == shortname);
        }

        private string GetPlayerGang(BasePlayer player)
        {
            if (HoodWars == null) return "Neutral";
            object result = HoodWars.Call("GetPlayerGangName", player.userID);
            return result?.ToString() ?? "Neutral";
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
            timer.Once(1.5f, () => GiveGangKit(player));
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            BasePlayer player = container.playerOwner;
            if (player == null) return;

            if (container == player.inventory.containerWear || container == player.inventory.containerBelt)
            {
                timer.Once(0.5f, () => GiveGangKit(player));
            }
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            if (corpse == null) return;
            foreach (var container in corpse.containers)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    var item = container.itemList[i];
                    if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
                        item.Remove();
                }
            }
        }

        #endregion
    }
}