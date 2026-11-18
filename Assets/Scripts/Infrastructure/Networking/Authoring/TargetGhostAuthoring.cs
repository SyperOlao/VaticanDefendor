using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Agents;
using VaticanDefendor.Navigation;

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
                AddComponent<CombatImpactState>(entity);
                AddComponent<EnemyAgentTag>(entity);
                AddComponent<PersistentTargetId>(entity);
                AddComponent<EnemyAgentTuning>(entity);
                AddComponent<NavigationGoal>(entity);
                AddComponent<NavigationSteering>(entity);
                AddComponent<EnemyCombatTuning>(entity);
                AddComponent<EnemyCombatAttackState>(entity);
                AddBuffer<EnemyAttackIntent>(entity);
                AddBuffer<CombatAttackVictim>(entity);
                AddComponent<CombatTargetTag>(entity);
                AddComponent<CombatHitState>(entity);
                AddComponent<CombatTargetRole>(entity);
                AddComponent<CombatDeathLifecycle>(entity);
            }
        }
    }
}
