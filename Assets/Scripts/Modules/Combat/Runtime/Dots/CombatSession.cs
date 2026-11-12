using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace VaticanDefendor.Combat
{
    public static class CombatSession
    {
        public static Entity CreateRun(EntityManager entities, CombatRunTuning tuning)
        {
            if (!ValidPositive(tuning.Damage) || !ValidPositive(tuning.IntervalSeconds) ||
                !ValidPositive(tuning.RangeMeters) || !ValidNonNegative(tuning.HitPaddingMeters) ||
                !ValidNonNegative(tuning.StaminaCost) || !ValidPositive(tuning.HeroMaximumHealth) ||
                !ValidPositive(tuning.MaximumStamina) || !ValidNonNegative(tuning.StaminaRecoveryPerSecond) ||
                !ValidPositive(tuning.TargetMaximumHealth) || !ValidPositive(tuning.TargetSpawnForwardMeters) ||
                tuning.StaminaCost > tuning.MaximumStamina)
            {
                throw new ArgumentException("Combat run tuning is invalid.", nameof(tuning));
            }

            Entity entity = entities.CreateEntity(typeof(CombatRunTuning), typeof(CombatRunState));
            entities.SetComponentData(entity, tuning);
            entities.SetComponentData(entity, new CombatRunState { NextEventSequence = 1 });
            return entity;
        }

        public static void InitializeHero(EntityManager entities, Entity hero, CombatRunTuning tuning)
        {
            if (!entities.HasComponent<CombatHealth>(hero) ||
                !entities.HasComponent<HeroCombatStamina>(hero) ||
                !entities.HasComponent<HeroActionState>(hero) ||
                !entities.HasBuffer<EquippedWeapon>(hero))
            {
                throw new InvalidOperationException("Hero ghost lacks Combat components or buffers.");
            }

            entities.SetComponentData(hero, new CombatHealth
            {
                Current = tuning.HeroMaximumHealth,
                Maximum = tuning.HeroMaximumHealth
            });
            entities.SetComponentData(hero, new HeroCombatStamina
            {
                Current = tuning.MaximumStamina,
                Maximum = tuning.MaximumStamina
            });
            entities.SetComponentData(hero, new HeroActionState { Facing = new float2(0, 1) });
            using EntityQuery Loadout = entities.CreateEntityQuery(typeof(RunWeapon));
            DynamicBuffer<EquippedWeapon> Equipped = entities.GetBuffer<EquippedWeapon>(hero);
            foreach (RunWeapon Weapon in entities.GetBuffer<RunWeapon>(Loadout.GetSingletonEntity()))
            {
                Equipped.Add(Weapon.Value);
            }
        }

        public static Entity SpawnTarget(EntityManager entities, Entity prefab, CombatRunTuning tuning)
        {
            if (!entities.HasComponent<CombatTargetTag>(prefab) ||
                !entities.HasComponent<CombatHealth>(prefab) ||
                !entities.HasComponent<LocalTransform>(prefab))
            {
                throw new InvalidOperationException("Target ghost prefab lacks CombatTargetTag, CombatHealth or transform.");
            }

            Entity target = entities.Instantiate(prefab);
            entities.SetComponentData(target,
                LocalTransform.FromPosition(new float3(0f, 0f, tuning.TargetSpawnForwardMeters)));
            entities.SetComponentData(target, new CombatHealth
            {
                Current = tuning.TargetMaximumHealth,
                Maximum = tuning.TargetMaximumHealth
            });
            entities.SetComponentData(target, new CombatTargetRole());
            entities.SetComponentData(target, new CombatDeathLifecycle
            {
                DespawnDelaySeconds = -1f
            });
            return target;
        }

        private static bool ValidPositive(float value) => math.isfinite(value) && value > 0f;
        private static bool ValidNonNegative(float value) => math.isfinite(value) && value >= 0f;
    }
}
