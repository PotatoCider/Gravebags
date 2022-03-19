using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using Microsoft.Xna.Framework;
using Terraria.ID;
using System.Collections.Generic;

namespace Gravebags
{
    [ApiVersion(2, 1)]
    public class GravebagPlugin : TerrariaPlugin
    {
        // Gravebags by Main.item index
        private Dictionary<int, Gravebag> gravebags = new Dictionary<int, Gravebag>();

        public GravebagManager dbManager;

        static readonly int TotalSlots = NetItem.InventorySlots + NetItem.ArmorSlots
            + NetItem.DyeSlots + NetItem.MiscEquipSlots + NetItem.MiscDyeSlots;

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
            TShock.Log.Debug("[Gravebags] Post Initialize");
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
                if (item.Stack == 0 || IsMediumcoreIgnoredItem(item.NetId)) continue;

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

            Gravebag bag = dbManager.PersistGravebag(Main.worldID, args.Player.Account.ID, args.Player.LastNetPosition, inventory);

            SpawnGravebag(bag);
        }


        void OnPlayerUpdate(object _, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            if (!args.Player.Dead) CheckGravebags(args.Player);
        }

        #endregion

        int SpawnGravebag(Gravebag bag)
        {
            int itemID = Item.NewItem(new EntitySource_DebugCommand(), bag.position, Vector2.Zero, ItemID.CultistBossBag);

            gravebags[itemID] = bag;

            // Drop only for the player that died
            Main.item[itemID].playerIndexTheItemIsReservedFor = TSPlayer.Server.Index;
            Main.item[itemID].keepTime = int.MaxValue;
            TSPlayer.All.SendData(PacketTypes.ItemOwner, null, itemID);

            TShock.Log.Debug("[Gravebags] Spawn {0} item {1}", bag.ID, itemID);
            return itemID;
        }

        void CheckGravebags(TSPlayer player)
        {
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
                    TShock.Log.Debug("[Gravebags] Update Position {0} item {1} pos ({2}, {3})", bag.ID, itemIndex, bag.position.X, bag.position.Y);
                }

                // sync bag position
                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, itemIndex);

                if (player.Account.ID != bag.accountID) continue;

                // check within 3 blocks
                if (distance <= 48.0)
                {
                    PickupGravebag(player, itemIndex);
                }
            }
        }

        void PickupGravebag(TSPlayer player, int itemID)
        {
            Gravebag bag = gravebags[itemID];

            List<int> pickUpItems = new List<int>();

            for (int i = 0; i < TotalSlots; i++)
            {
                NetItem item = bag.inventory[i];
                if (item.NetId == ItemID.None || IsMediumcoreIgnoredItem(item.NetId)) continue;

                if (Main.ServerSideCharacter)
                {
                    GetSubInventoryIndex(player.TPlayer, i, out Item[] subInv, out int index);
                    if (subInv[index].netID == ItemID.None || IsMediumcoreIgnoredItem(subInv[index].netID))
                    {
                        subInv[index] = NetItemToItem(item);

                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, item.PrefixId);

                        bag.inventory[i] = new NetItem();
                    }
                    else
                    {
                        pickUpItems.Add(i);
                    }
                }
                else
                {
                    pickUpItems.Add(i);
                }
            }

            // Give remaining items
            bool overflow = false;
            bool pickUp = false;
            if (pickUpItems.Count > 0)
            {
                foreach (int i in pickUpItems)
                {
                    Item item = NetItemToItem(bag.inventory[i]);

                    // Fill item server-side first, since item is not immediately updated server-side. This simulates
                    // the item entering the players inventory, so that we can calculate whether the player can take more.
                    if (player.TPlayer.ItemSpace(item).CanTakeItem)
                    {
                        player.TPlayer.GetItem(-1, item, GetItemSettings.PickupItemFromWorld);
                        int itemIndex = Item.NewItem(new EntitySource_DebugCommand(), player.LastNetPosition, Vector2.Zero, 
                            item.netID, item.stack, false, item.prefix, true);

                        Main.item[itemIndex].playerIndexTheItemIsReservedFor = player.Index;
                        player.SendData(PacketTypes.ItemOwner, null, itemIndex);

                        bag.inventory[i] = new NetItem();
                        pickUp = true;
                    }
                    else
                    {
                        overflow = true;
                    }
                }
            }

            if (pickUp) TShock.Log.Debug("[Gravebags] Pick up {0} item {1}", bag.ID, itemID);

            if (overflow) return;

            TShock.Log.Debug("[Gravebags] Delete {0} item {1}", bag.ID, itemID);

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
        
        bool IsMediumcoreIgnoredItem(int netID)
        {
            return netID == ItemID.CopperShortsword ||
                    netID == ItemID.CopperPickaxe ||
                    netID == ItemID.CopperAxe;
        }

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