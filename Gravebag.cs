
namespace Gravebags {
    public class Gravebag {
        public Gravebag(int id, int accID, Vector2 deathPos, List<NetItem> inv, NetItem trash) {
            ID = id;
            accountID = accID;
            position = deathPos;
            inventory = inv;
            trashItem = trash;
        }
        public int ID;
        public int accountID;
        public Vector2 position;
        public List<NetItem> inventory;
        public NetItem trashItem;
    }
}
