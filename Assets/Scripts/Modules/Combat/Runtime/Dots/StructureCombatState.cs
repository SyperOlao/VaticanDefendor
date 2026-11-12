using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Combat
{
    [System.Serializable]
    public struct StructureCombatState : IComponentData
    {
        [GhostField] public float Range;
        [GhostField] public float Damage;
        [GhostField] public float Interval;
        [GhostField] public float Remaining;
        [GhostField] public byte Healing;
        [GhostField] public int Level;
        [GhostField] public uint AttackSequence;
    }
}
