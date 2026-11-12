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
    }

    public struct GatheringTuning : IComponentData
    {
        public float Range;
        public float CycleSeconds;
        public int Amount;
        public float ProductionRange;
    }
}
