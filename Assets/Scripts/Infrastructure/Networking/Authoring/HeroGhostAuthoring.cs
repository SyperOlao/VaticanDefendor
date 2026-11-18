using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Movement;
using VaticanDefendor.Combat;
using VaticanDefendor.RunEconomy;

namespace VaticanDefendor.Network
{
    [DisallowMultipleComponent]
    public sealed class HeroGhostAuthoring : MonoBehaviour
    {
        private sealed class HeroGhostBaker : Baker<HeroGhostAuthoring>
        {
            public override void Bake(HeroGhostAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<HeroMovementTuning>(entity);
                AddComponent<HeroMoveInput>(entity);
                AddComponent<SessionContentRelease>(entity);
                AddComponent<CombatHealth>(entity);
                AddComponent<CombatImpactState>(entity);
                AddComponent<RunEconomyView>(entity);
                AddComponent<HeroCombatStamina>(entity);
                AddComponent<HeroActionState>(entity);
                AddBuffer<EquippedWeapon>(entity);
            }
        }
    }
}
