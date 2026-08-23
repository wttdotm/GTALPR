using GTA;
using GTA.Math;

namespace FlockSurveillance
{
    public sealed class LootDrop
    {
        public Prop Prop { get; set; }
        public Vector3 Position { get; set; }

        public int CopperScrap { get; set; }
        public int ElectronicComponents { get; set; }
        public int GoldPlatedContacts { get; set; }
    }
}