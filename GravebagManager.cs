using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using MySql.Data.MySqlClient;
using TShockAPI.DB;
using TShockAPI;
using Microsoft.Xna.Framework;

namespace Gravebags {
    public class GravebagManager {
        private IDbConnection database;

        public GravebagManager(IDbConnection db) {
            database = db;

            var table = new SqlTable("Gravebags",
                                    new SqlColumn("ID", MySqlDbType.Int32) { Primary = true, AutoIncrement = true },
                                    new SqlColumn("WorldID", MySqlDbType.Int32),
                                    new SqlColumn("AccountID", MySqlDbType.Int32),
                                    new SqlColumn("PositionX", MySqlDbType.Float),
                                    new SqlColumn("PositionY", MySqlDbType.Float),
                                    new SqlColumn("Inventory", MySqlDbType.Text),
                                    new SqlColumn("TrashItem", MySqlDbType.Text)
                                    );

            var creator = new SqlTableCreator(db,
                db.GetSqlType() == SqlType.Sqlite
                    ? (IQueryBuilder)new SqliteQueryCreator()
                    : new MysqlQueryCreator());
            creator.EnsureTableStructure(table);
        }

        public Gravebag PersistGravebag(int worldID, int accountID, Vector2 position, List<NetItem> inv, NetItem trashItem) {
            string query = "INSERT INTO Gravebags (WorldID, AccountID, PositionX, PositionY, Inventory, TrashItem) VALUES (@0, @1, @2, @3, @4, @5);";
            if (database.GetSqlType() == SqlType.Mysql) {
                query += "SELECT LAST_INSERT_ID();";
            } else {
                query += "SELECT CAST(last_insert_rowid() as INT);";
            }
            int id = database.QueryScalar<int>(query, worldID, accountID, position.X, position.Y, string.Join("~", inv), trashItem);
            return new Gravebag(id, accountID, position, inv, trashItem);
        }

        public bool UpdateGravebagPosition(int id, Vector2 position) {
            return database.Query("UPDATE Gravebags SET PositionX = @0, PositionY = @1 WHERE ID = @2", position.X, position.Y, id) > 0;
        }

        public bool UpdateGravebagInventory(int id, List<NetItem> inventory, NetItem trashItem) {
            return database.Query("UPDATE Gravebags SET Inventory = @0, TrashItem = @1 WHERE ID = @2", string.Join("~", inventory), trashItem, id) > 0;
        }

        public bool RemoveGravebag(int id) {
            return database.Query("DELETE FROM Gravebags WHERE ID = @0", id) > 0;
        }

        public List<Gravebag> GetAllGravebags(int worldID) {
            List<Gravebag> gravebags = new List<Gravebag>();

            using (var reader = database.QueryReader("SELECT * FROM Gravebags WHERE WorldID = @0", worldID)) {
                while (reader.Read()) {
                    gravebags.Add(PopulateGravebagFromResult(reader));
                }
            }
            return gravebags;
        }

        private Gravebag PopulateGravebagFromResult(QueryResult result) {
            int id = result.Get<int>("ID");
            int accountID = result.Get<int>("AccountID");
            float posX = result.Get<float>("PositionX");
            float posY = result.Get<float>("PositionY");
            List<NetItem> inventory = result.Get<string>("Inventory").Split('~').Select(NetItem.Parse).ToList();
            NetItem trashItem = NetItem.Parse(result.Get<string>("TrashItem"));
            return new Gravebag(id, accountID, new Vector2(posX, posY), inventory, trashItem);
        }
    }
}
