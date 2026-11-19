using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Production
{
    [System.Serializable]
    public struct ResourceNode : IComponentData
    {
        [GhostField] public ResourceKind Kind;
        [GhostField] public int Remaining;
        [GhostField] public int Capacity;
        [GhostField] public int WaveNumber;
        [GhostField] public byte Infinite;

        public readonly bool HasResources => Infinite != 0 || Remaining > 0;

        public int Take(int Requested)
        {
            if (Infinite != 0)
            {
                return Requested;
            }
            int Amount = System.Math.Min(Requested, Remaining);
            Remaining -= Amount;
            return Amount;
        }
    }

    public struct GatheringTuning : IComponentData
    {
        public float Range;
        public float CycleSeconds;
        public int Amount;
        public float ProductionRange;
    }
}
