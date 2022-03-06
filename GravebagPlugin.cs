using System;
using System.IO;
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

        public Dictionary<int, Item[]> Gravebags = new Dictionary<int, Item[]>();

        private int TotalSlots = NetItem.InventorySlots + NetItem.ArmorSlots
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
            Item[] fullInv = new Item[99];

            player.inventory.CopyTo(fullInv, 0);
            player.armor.CopyTo(fullInv, 59);
            player.dye.CopyTo(fullInv, 79);
            player.miscEquips.CopyTo(fullInv, 89);
            player.miscDyes.CopyTo(fullInv, 94);

            int i = 0;
            foreach (Item item in fullInv)
            {
                if (item.netID == 3506 || item.netID == 3507 || item.netID == 3509) continue;

                // suppress item drops
                int last = i;
                do
                {
                    Item invItem = Main.item[i];
                    if (invItem.active &&
                        invItem.netID == item.netID &&
                        invItem.stack == item.stack &&
                        invItem.prefix == item.prefix)
                    {
                        Main.item[i].netID = 0;
                        Main.item[i].active = false;
                        NetMessage.SendData(21, -1, -1, null, i, 0);
                    }
                    i = (i + 1) % 400;
                } while (i != last);
            }

            Vector2 pos = args.Player.LastNetPosition;
            Console.WriteLine("Died at ({0}, {1})", pos.X, pos.Y);

            int itemID = Item.NewItem(new EntitySource_DebugCommand(), pos, Vector2.Zero, 3331);

            Gravebags[itemID] = fullInv;

            // Drop only for the player that died
            Main.item[itemID].playerIndexTheItemIsReservedFor = 255;
            Main.item[itemID].keepTime = int.MaxValue;
            args.Player.SendData(PacketTypes.ItemOwner, null, itemID);

        }

        void OnPlayerUpdate(object _, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            if (args.Player.Dead) return;
            foreach (int itemID in Gravebags.Keys)
            {
                Item item = Main.item[itemID];

                if (args.Position.Distance(item.position) <= 32.0)
                {
                    PickupGravebag(args.Player, itemID);
                }
            }
        }

        void GetSubInventoryIndex(Player player, int i, out Item[] subInv, out int index)
        {
            if (i < NetItem.InventorySlots)
            {
                index = i;
                subInv = player.inventory;
            }
            else if (i < NetItem.InventorySlots + NetItem.ArmorSlots)
            {
                index = i - NetItem.InventorySlots;
                subInv = player.armor;
            }
            else if (i < NetItem.InventorySlots + NetItem.ArmorSlots + NetItem.DyeSlots)
            {
                index = i - NetItem.InventorySlots - NetItem.ArmorSlots;
                subInv = player.dye;
            }
            else if (i < NetItem.InventorySlots + NetItem.ArmorSlots + NetItem.DyeSlots + NetItem.MiscEquipSlots)
            {
                index = i - NetItem.InventorySlots - NetItem.ArmorSlots - NetItem.DyeSlots;
                subInv = player.miscEquips;
            }
            else
            {
                index = i - NetItem.InventorySlots - NetItem.ArmorSlots - NetItem.DyeSlots - NetItem.MiscEquipSlots;
                subInv = player.miscDyes;
            }
        }
        void PickupGravebag(TSPlayer player, int itemID)
        {
            Console.WriteLine("[Pick up Gravebag]");
            Item[] inv = Gravebags[itemID];

            Stack<Item> overflow = new Stack<Item>();

            for (int i = 0; i < TotalSlots; i++)
            {
                Console.Write("{0} ", i);
                if (i % 10 == 9) Console.WriteLine();
                Item item = inv[i];
                if (item.stack == 0 || item.netID == 3506 || item.netID == 3507 || item.netID == 3509) continue;

                if (Main.ServerSideCharacter)
                {
                    GetSubInventoryIndex(player.TPlayer, i, out Item[] subInv, out int index);
                    if (subInv[index].stack == 0) 
                    {
                        subInv[index] = item;
                    }
                    else 
                    {
                        overflow.Push(item);
                    }
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, item.prefix);
                }
                else
                {
                    player.GiveItem(item.netID, item.stack, item.prefix);
                }
            }

            // Give remaining items 
            if (overflow.Count > 0)
            {
                foreach (Item item in overflow)
                {
                    player.GiveItem(item.netID, item.stack, item.prefix);
                }
            }

            Gravebags.Remove(itemID);
            Main.item[itemID].active = false;
            Main.item[itemID].keepTime = 0;
            TSPlayer.All.SendData(PacketTypes.ItemDrop, null, itemID);

        }

        void OnPlayerSlot(object _, GetDataHandlers.PlayerSlotEventArgs args)
        {

        }

        void OnItemDrop(object _, GetDataHandlers.ItemDropEventArgs args)
        {

        }

        #endregion

    }
}