using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Production
{
    // Construction creates this component; Production alone changes progress and stored gold.
    public struct ProductionSource : IComponentData
    {
        public float CycleSeconds;
        public int GoldPerCycle;
        public float PickupOffsetZMeters;
        public float PickupRadiusMeters;
        public float ProgressSeconds;
        [GhostField] public long StoredGold;
    }
}
