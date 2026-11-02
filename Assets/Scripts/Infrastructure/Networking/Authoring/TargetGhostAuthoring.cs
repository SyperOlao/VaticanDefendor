using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Network
{
    [DisallowMultipleComponent]
    public sealed class TargetGhostAuthoring : MonoBehaviour
    {
        private sealed class TargetGhostBaker : Baker<TargetGhostAuthoring>
        {
            public override void Bake(TargetGhostAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<CombatHealth>(entity);
                AddComponent<CombatTargetTag>(entity);
                AddComponent<CombatTargetRole>(entity);
                AddComponent<CombatDeathLifecycle>(entity);
            }
        }
    }
}
