using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using Microsoft.Xna.Framework;
using Terraria.ID;
using System.Collections.Generic;
using Terraria.UI;

namespace Gravebags
{
    [ApiVersion(2, 1)]
    public class GravebagPlugin : TerrariaPlugin
    {
        // Dictionary<itemIndex, gravebag>
        private Dictionary<int, Gravebag> gravebags = new Dictionary<int, Gravebag>();

        // Dictionary<accountID, List<itemIndex>>
        private Dictionary<int, int> gravebagsNearPlayer = new Dictionary<int, int>();

        public GravebagManager dbManager;

        static readonly int TotalSlots = NetItem.InventorySlots + NetItem.ArmorSlots
            + NetItem.DyeSlots + NetItem.MiscEquipSlots + NetItem.MiscDyeSlots;
        static readonly int CursorSlotIndex = NetItem.InventorySlots - 1;

        #region Info
        public override string Name => "Gravebags";

        public override string Author => "PotatoCider";

        public override string Description => "Gravebags plugin for Mediumcore playthrough.";

        public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;
        #endregion

        #region Initialize
        public override void Initialize()
        {
            dbManager = new GravebagManager(TShock.DB);

            ServerApi.Hooks.GamePostInitialize.Register(this, OnGamePostInitialize);

            GetDataHandlers.KillMe.Register(OnKillMe);
            GetDataHandlers.PlayerUpdate.Register(OnPlayerUpdate);
            GetDataHandlers.PlayerSlot.Register(OnPlayerSlot);
        }
        #endregion

        #region Dispose
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnGamePostInitialize);
                GetDataHandlers.KillMe.UnRegister(OnKillMe);
                GetDataHandlers.PlayerUpdate.UnRegister(OnPlayerUpdate);
                GetDataHandlers.PlayerSlot.UnRegister(OnPlayerSlot);
            }
            base.Dispose(disposing);
        }
        #endregion

        public GravebagPlugin(Main game)
            : base(game)
        {
            Order = 1;
        }

        #region Handlers

        void OnGamePostInitialize(EventArgs args)
        {
            TShock.Log.ConsoleDebug("[Gravebags] Post Initialize");
            List<Gravebag> bags = dbManager.GetAllGravebags(Main.worldID);
            foreach (Gravebag bag in bags)
            {
                SpawnGravebag(bag);
            }
        }

        void OnKillMe(object _, GetDataHandlers.KillMeEventArgs args)
        {
            Player player = args.Player.TPlayer;
            if (player.difficulty != PlayerDifficultyID.MediumCore) return;

            List<NetItem> inventory = new List<NetItem>(TotalSlots);
            inventory.AddRange(player.inventory.Select(item => (NetItem)item));
            inventory.AddRange(player.armor.Select(item => (NetItem)item));
            inventory.AddRange(player.dye.Select(item => (NetItem)item));
            inventory.AddRange(player.miscEquips.Select(item => (NetItem)item));
            inventory.AddRange(player.miscDyes.Select(item => (NetItem)item));

            // suppress item drops
            int i = 0;
            foreach (NetItem item in inventory)
            {
                if (IsEmptyOrIgnoredItem(item)) continue;

                int last = i;
                do
                {
                    Item floorItem = Main.item[i];
                    if (floorItem.active &&
                        floorItem.netID == item.NetId &&
                        floorItem.stack == item.Stack &&
                        floorItem.prefix == item.PrefixId)
                    {
                        Main.item[i].netDefaults(ItemID.None);
                        Main.item[i].active = false;
                        NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, i);
                        break;
                    }
                    i = (i + 1) % 400;
                } while (i != last);
            }

            Gravebag bag = dbManager.PersistGravebag(Main.worldID, args.Player.Account.ID, args.Player.LastNetPosition, inventory, (NetItem)player.trashItem);

            SpawnGravebag(bag);
        }


        void OnPlayerUpdate(object _, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            CheckGravebags(args.Player);
        }

        void OnPlayerSlot(object _, GetDataHandlers.PlayerSlotEventArgs args)
        {
            if (gravebagsNearPlayer.ContainsKey(args.Player.Account.ID)) CheckGravebags(args.Player);
        }

        #endregion

        int SpawnGravebag(Gravebag bag)
        {
            int itemID = Item.NewItem(new EntitySource_DebugCommand(), bag.position, Vector2.Zero, ItemID.CultistBossBag);

            gravebags[itemID] = bag;

            // Prevent players from picking up unobtainable boss bag
            Main.item[itemID].playerIndexTheItemIsReservedFor = TSPlayer.Server.Index;
            Main.item[itemID].keepTime = int.MaxValue;
            TSPlayer.All.SendData(PacketTypes.ItemOwner, null, itemID);

            TShock.Log.ConsoleDebug("[Gravebags] Spawn {0} item {1} pos ({2} {3})", bag.ID, itemID, bag.position.X / 16.0, bag.position.Y / 16.0);
            return itemID;
        }

        void CheckGravebags(TSPlayer player)
        {
            if (player.Dead) return;

            foreach (int itemIndex in gravebags.Keys.ToList())
            {
                Gravebag bag = gravebags[itemIndex];
                float distance = bag.position.Distance(player.LastNetPosition);

                // ignore players more than 50 blocks away
                if (distance > 800.0) continue;

                Item floorItem = Main.item[itemIndex];

                // respawn bag if not present
                if (!floorItem.active || floorItem.netID != ItemID.CultistBossBag)
                {
                    gravebags.Remove(itemIndex);
                    SpawnGravebag(bag);
                    continue;
                }

                if (floorItem.position.Distance(bag.position) > 16.0 && floorItem.velocity.Equals(Vector2.Zero))
                {
                    bag.position = floorItem.position;
                    dbManager.UpdateGravebagPosition(bag.ID, bag.position);
                    TShock.Log.ConsoleDebug("[Gravebags] Update Position {0} item {1} pos ({2} {3})", bag.ID, itemIndex, bag.position.X / 16.0, bag.position.Y / 16.0);
                }

                // sync bag position
                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, itemIndex);

                // check within 3 blocks
                int accountID = player.Account.ID;
                if (distance <= 48.0)
                {
                    if (accountID == bag.accountID)
                    {
                        PickupGravebag(player, itemIndex);
                    }
                    else if (!gravebagsNearPlayer.TryGetValue(player.Account.ID, out int index) || index != itemIndex)
                    {
                        UserAccount bagOwner = TShock.UserAccounts.GetUserAccountByID(bag.accountID);
                        player.SendInfoMessage("[Gravebags] {0}'s gravebag.", bagOwner.Name);
                    }
                    gravebagsNearPlayer[accountID] = itemIndex;
                }
                else
                {
                    gravebagsNearPlayer.Remove(accountID);
                }
            }
        }

        void PickupGravebag(TSPlayer player, int itemID)
        {
            Gravebag bag = gravebags[itemID];

            List<int> pickUpItems = new List<int>();
            int pickedUp = 0;

            // Try to fill items using SSC
            for (int i = 0; i < TotalSlots; i++)
            {
                Item bagItem = NetItemToItem(bag.inventory[i]);
                if (IsEmptyOrIgnoredItem(bagItem)) continue;

                GetSubInventoryIndex(player.TPlayer, i, out Item[] subInv, out int index);

                // Checks made to auto-fill inventory slot:
                // - SSC must be enabled
                // - Current inventory slot is empty or ignored item
                // - Current inventory slot is not cursor slot
                // - Bag accessory does not clash with other accessories on player
                // Trash slot is auto-filled if SSC is enabled
                if (Main.ServerSideCharacter && IsEmptyOrIgnoredItem(subInv[index]) && index != CursorSlotIndex
                    && !(ReferenceEquals(subInv, player.TPlayer.armor) && ItemSlot.AccCheck(subInv, bagItem, index)))
                {
                    subInv[index] = bagItem;
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, bagItem.prefix);

                    bag.inventory[i] = new NetItem();
                    pickedUp++;
                }
                else
                {
                    pickUpItems.Add(i);
                }
            }

            // Give remaining items
            int overflowCount = 0;
            if (pickUpItems.Count > 0)
            {
                foreach (int i in pickUpItems)
                {
                    Item bagItem = NetItemToItem(bag.inventory[i]);

                    // Fill item server-side first, since item is not immediately updated server-side. This simulates
                    // the item entering the players inventory, so that we can calculate whether the player can take more.
                    if (player.TPlayer.ItemSpace(bagItem).CanTakeItem)
                    {
                        Item leftoverItem = player.TPlayer.GetItem(-1, bagItem.Clone(), GetItemSettings.PickupItemFromWorld);

                        int itemIndex = Item.NewItem(new EntitySource_DebugCommand(), player.TPlayer.position, Vector2.Zero,
                            bagItem.netID, bagItem.stack - leftoverItem.stack, false, bagItem.prefix, true);

                        Main.item[itemIndex].playerIndexTheItemIsReservedFor = player.Index;
                        player.SendData(PacketTypes.ItemOwner, null, itemIndex);

                        bag.inventory[i] = (NetItem)leftoverItem;
                        pickedUp++;
                    }
                    if (bag.inventory[i].Stack > 0) overflowCount++;
                }
            }


            // Handle trash item
            if (Main.ServerSideCharacter && !IsEmptyOrIgnoredItem(bag.trashItem) && IsEmptyOrIgnoredItem(player.TPlayer.trashItem))
            {
                player.TPlayer.trashItem = NetItemToItem(bag.trashItem);
                bag.trashItem = new NetItem();
                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, NetItem.TrashIndex.Item1, bag.trashItem.PrefixId);
                pickedUp++;
            }

            if (pickedUp > 0)
            {
                player.SendInfoMessage("[Gravebags] You picked up {0} item{1}. {2} item{3} left.",
                    pickedUp, pickedUp == 1 ? "" : "s", overflowCount, overflowCount == 1 ? "" : "s");
                TShock.Log.ConsoleDebug("[Gravebags] Pick up {0} item {1} pickUp {2} count {3} overflow {4}", bag.ID, itemID, pickUpItems.Count, pickedUp, overflowCount);
                
                if (overflowCount > 0) dbManager.UpdateGravebagInventory(bag.ID, bag.inventory, bag.trashItem);
            }

            if (overflowCount > 0) return;

            TShock.Log.ConsoleDebug("[Gravebags] Delete {0} item {1}", bag.ID, itemID);

            gravebags.Remove(itemID);
            Main.item[itemID].active = false;
            Main.item[itemID].keepTime = 0;
            TSPlayer.All.SendData(PacketTypes.ItemDrop, null, itemID);
            dbManager.RemoveGravebag(bag.ID);
        }

        #region Helpers

        void GetSubInventoryIndex(Player player, int i, out Item[] subInv, out int index)
        {
            if (i < NetItem.InventoryIndex.Item2)
            {
                index = i - NetItem.InventoryIndex.Item1;
                subInv = player.inventory;
            }
            else if (i < NetItem.ArmorIndex.Item2)
            {
                index = i - NetItem.ArmorIndex.Item1;
                subInv = player.armor;
            }
            else if (i < NetItem.DyeIndex.Item2)
            {
                index = i - NetItem.DyeIndex.Item1;
                subInv = player.dye;
            }
            else if (i < NetItem.MiscEquipIndex.Item2)
            {
                index = i - NetItem.MiscEquipIndex.Item1;
                subInv = player.miscEquips;
            }
            else if (i < NetItem.MiscDyeIndex.Item2)
            {
                index = i - NetItem.MiscDyeIndex.Item1;
                subInv = player.miscDyes;
            }
            else
            {
                throw new ArgumentOutOfRangeException("i", "Index out of range");
            }
        }

        bool IsEmptyOrIgnoredItem(Item item) { return IsEmptyOrIgnoredItem((NetItem)item); }
        bool IsEmptyOrIgnoredItem(NetItem item)
        {
            return item.NetId == ItemID.None ||
                    item.NetId == ItemID.CopperShortsword ||
                    item.NetId == ItemID.CopperPickaxe ||
                    item.NetId == ItemID.CopperAxe;
        }

        /// <summary>
        /// Converts a NetItem to an Item.
        /// </summary>
        /// <param name="netItem"></param>
        /// <returns>A new instance of an Item.</returns>
        Item NetItemToItem(NetItem netItem)
        {
            Item item = new Item();
            item.netDefaults(netItem.NetId);
            item.stack = netItem.Stack;
            item.prefix = netItem.PrefixId;
            return item;
        }

        #endregion
    }
}