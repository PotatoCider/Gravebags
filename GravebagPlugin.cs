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

namespace Gravebag
{
    [ApiVersion(2, 1)]
    public class GravebagPlugin : TerrariaPlugin
    {
        class Gravebag
        {
            public Gravebag(TSPlayer player, Vector2 deathPos, Item[] fullInv)
            {
                playerIndex = player.Index;
                accountID = player.Account.ID;
                fullInventory = fullInv;
                position = deathPos;
            }
            public int playerIndex = -1;
            public int accountID;
            public Item[] fullInventory;
            public Vector2 position;
        }

        private Dictionary<int, Gravebag> gravebags = new Dictionary<int, Gravebag>();

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
            ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);

            GetDataHandlers.KillMe.Register(OnKillMe);
            GetDataHandlers.PlayerSlot.Register(OnPlayerSlot);
            GetDataHandlers.ItemDrop.Register(OnItemDrop, HandlerPriority.Low, true);
            GetDataHandlers.PlayerUpdate.Register(OnPlayerUpdate);
        }
        #endregion

        #region Dispose
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
                GetDataHandlers.KillMe.UnRegister(OnKillMe);
                GetDataHandlers.PlayerSlot.UnRegister(OnPlayerSlot);
                GetDataHandlers.ItemDrop.UnRegister(OnItemDrop);
                GetDataHandlers.PlayerUpdate.UnRegister(OnPlayerUpdate);
            }
            base.Dispose(disposing);
        }
        #endregion

        public GravebagPlugin(Main game)
            : base(game)
        {
            Order = 10;
        }

        #region Handlers

        void OnInitialize(EventArgs args)
        {
            Console.WriteLine("[Gravebags Init]");
        }

        void OnKillMe(object _, GetDataHandlers.KillMeEventArgs args)
        {
            Console.WriteLine("[Gravebags Death]");

            Player player = args.Player.TPlayer;
            if (player.difficulty != 1) return;

            Item[] fullInv = new Item[TotalSlots];
            player.inventory.CopyTo(fullInv, NetItem.InventoryIndex.Item1);
            player.armor.CopyTo(fullInv, NetItem.ArmorIndex.Item1);
            player.dye.CopyTo(fullInv, NetItem.DyeIndex.Item1);
            player.miscEquips.CopyTo(fullInv, NetItem.MiscEquipIndex.Item1);
            player.miscDyes.CopyTo(fullInv, NetItem.MiscDyeIndex.Item1);

            // suppress item drops
            int i = 0;
            foreach (Item item in fullInv)
            {
                if (item.stack == 0 || item.netID == 3506 || item.netID == 3507 || item.netID == 3509) continue;

                int last = i;
                do
                {
                    Item invItem = Main.item[i];
                    if (invItem.active &&
                        invItem.netID == item.netID &&
                        invItem.stack == item.stack &&
                        invItem.prefix == item.prefix)
                    {
                        Main.item[i].netDefaults(0);
                        Main.item[i].active = false;
                        NetMessage.SendData(21, -1, -1, null, i, 0);
                    }
                    i = (i + 1) % 400;
                } while (i != last);
            }

            SpawnGravebag(args.Player, args.Player.LastNetPosition, fullInv);
        }


        void OnPlayerUpdate(object _, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            if (!args.Player.Dead) CheckGravebags(args.Player);
        }

        void OnPlayerSlot(object _, GetDataHandlers.PlayerSlotEventArgs args) { }

        void OnItemDrop(object _, GetDataHandlers.ItemDropEventArgs args) { }

        #endregion

        int SpawnGravebag(TSPlayer player, Vector2 position, Item[] fullInv)
        {
            int itemID = Item.NewItem(new EntitySource_DebugCommand(), position, Vector2.Zero, 3331);

            gravebags[itemID] = new Gravebag(player, position, fullInv);

            // Drop only for the player that died
            Main.item[itemID].playerIndexTheItemIsReservedFor = 255;
            Main.item[itemID].keepTime = int.MaxValue;
            TSPlayer.All.SendData(PacketTypes.ItemOwner, null, itemID);
            return itemID;
        }

        void CheckGravebags(TSPlayer player)
        {
            foreach (int itemIndex in gravebags.Keys.ToList())
            {
                Gravebag bag = gravebags[itemIndex];

                if (player.Account.ID != bag.accountID) continue;
                Item item = Main.item[itemIndex];

                // respawn bag if not present
                if (!item.active || item.netID != 3331)
                {
                    gravebags.Remove(itemIndex);
                    SpawnGravebag(player, bag.position, bag.fullInventory);
                    continue;
                }

                bag.position = item.position;

                // check within 3 blocks
                if (bag.position.Distance(player.LastNetPosition) <= 48.0)
                {
                    PickupGravebag(player, itemIndex);
                }
            }
        }

        void PickupGravebag(TSPlayer player, int itemID)
        {
            Console.WriteLine("[Pick up Gravebag {0}]", itemID);
            Gravebag bag = gravebags[itemID];

            List<int> pickUpItems = new List<int>();

            for (int i = 0; i < TotalSlots; i++)
            {
                Item item = bag.fullInventory[i];
                if (item.stack == 0 || item.netID == 3506 || item.netID == 3507 || item.netID == 3509) continue;

                if (Main.ServerSideCharacter)
                {
                    GetSubInventoryIndex(player.TPlayer, i, out Item[] subInv, out int index);
                    if (subInv[index].stack == 0)
                    {
                        subInv[index] = item.Clone();
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, item.prefix);
                        item.netDefaults(0);
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
            if (pickUpItems.Count > 0)
            {
                foreach (int i in pickUpItems)
                {
                    Item item = bag.fullInventory[i];

                    // Fill item server-side first, since item is not immediately updated server-side. This simulates
                    // the item entering the players inventory, so that we can calculate whether the player can take more.
                    if (player.TPlayer.ItemSpace(item).CanTakeItem)
                    {
                        player.TPlayer.GetItem(-1, item.Clone(), GetItemSettings.PickupItemFromWorld);
                        int itemIndex = Item.NewItem(new EntitySource_DebugCommand(), player.LastNetPosition, Vector2.Zero, 
                            item.netID, item.stack, false, item.prefix);
                        Main.item[itemIndex].playerIndexTheItemIsReservedFor = player.Index;
                        player.SendData(PacketTypes.ItemOwner, null, itemIndex);

                        item.netDefaults(0);
                    }
                    else
                    {
                        overflow = true;
                    }
                }
            }

            if (overflow) return;

            Console.WriteLine("Delete Gravebag");

            gravebags.Remove(itemID);
            Main.item[itemID].active = false;
            Main.item[itemID].keepTime = 0;
            TSPlayer.All.SendData(PacketTypes.ItemDrop, null, itemID);
        }

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

    }
}