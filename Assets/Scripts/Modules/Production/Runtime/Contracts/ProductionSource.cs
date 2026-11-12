using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Production
{
    [System.Serializable]
    public struct ProductionSource : IComponentData
    {
        [GhostField] public ResourceKind Kind;
        [GhostField] public float CycleSeconds;
        [GhostField] public int AmountPerCycle;
        [GhostField] public float ProgressSeconds;
    }
}
