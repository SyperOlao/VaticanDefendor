using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CombatServerGroup))]
    [UpdateAfter(typeof(CombatResolutionSystem))]
    public partial class EnemyCombatResolutionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSimulationTime>();
            RequireForUpdate<CombatRunState>();
            RequireForUpdate<EnemyCombatConcurrency>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 ||
                SystemAPI.GetSingleton<RunActivityState>().Terminal != 0) return;
            double now = SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds;
            CombatRunState run = SystemAPI.GetSingleton<CombatRunState>();
            int maximumWindups = SystemAPI.GetSingleton<EnemyCombatConcurrency>().MaximumSimultaneousWindups;
            int activeWindups = 0;
            foreach (RefRO<EnemyCombatAttackState> attack in
                SystemAPI.Query<RefRO<EnemyCombatAttackState>>())
            {
                if (attack.ValueRO.Phase == 1) activeWindups++;
            }
            foreach (var (health, transform, tuning, state, intents) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>,
                    RefRO<EnemyCombatTuning>, RefRW<EnemyCombatAttackState>,
                    DynamicBuffer<EnemyAttackIntent>>())
            {
                if (health.ValueRO.Dead != 0)
                {
                    if (state.ValueRO.Phase == 1) activeWindups--;
                    state.ValueRW.Phase = 0;
                    intents.Clear();
                    continue;
                }
                if (state.ValueRO.Phase == 2 && now >= state.ValueRO.PhaseEndsSeconds)
                    state.ValueRW.Phase = 0;
                if (state.ValueRO.Phase == 0 && intents.Length > 0 &&
                    activeWindups < maximumWindups)
                {
                    Entity target = intents[0].Target;
                    if (CanHit(target, transform.ValueRO.Position,
                        tuning.ValueRO.AttackRadiusMeters))
                    {
                        state.ValueRW.Target = target;
                        state.ValueRW.Phase = 1;
                        state.ValueRW.PhaseEndsSeconds = now + tuning.ValueRO.WindupSeconds;
                        activeWindups++;
                    }
                }
                intents.Clear();
                if (state.ValueRO.Phase != 1 || now < state.ValueRO.PhaseEndsSeconds) continue;
                Entity victim = state.ValueRO.Target;
                if (CanHit(victim, transform.ValueRO.Position, tuning.ValueRO.AttackRadiusMeters))
                {
                    CombatHealth victimHealth = EntityManager.GetComponentData<CombatHealth>(victim);
                    float damage = math.min(victimHealth.Current, tuning.ValueRO.Damage);
                    victimHealth.Current = math.max(0f, victimHealth.Current - damage);
                    if (victimHealth.Current == 0f && victimHealth.Dead == 0)
                        victimHealth.Dead = 1;
                    victimHealth.HitSequence = run.NextEventSequence++;
                    EntityManager.SetComponentData(victim, victimHealth);
                    if (victimHealth.HitSequence <= 15 || victimHealth.Dead != 0)
                        Debug.Log($"[EnemyCombat] " +
                            $"{(EntityManager.HasComponent<ChurchTag>(victim) ? "churchHP" : "heroHP")}=" +
                            $"{victimHealth.Current} damage={damage} dead={victimHealth.Dead}.");
                }
                state.ValueRW.Phase = 2;
                state.ValueRW.PhaseEndsSeconds = now + tuning.ValueRO.RecoverySeconds;
                activeWindups--;
            }
            SystemAPI.SetSingleton(run);
        }

        private bool CanHit(Entity target, float3 attacker, float radius)
        {
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                !EntityManager.HasComponent<HeroCombatStamina>(target) &&
                    !EntityManager.HasComponent<ChurchTag>(target) ||
                !EntityManager.HasComponent<CombatHealth>(target) ||
                !EntityManager.HasComponent<LocalTransform>(target)) return false;
            CombatHealth health = EntityManager.GetComponentData<CombatHealth>(target);
            if (health.Dead != 0 || health.Current <= 0f) return false;
            float3 position = EntityManager.GetComponentData<LocalTransform>(target).Position;
            return math.distancesq(new float2(attacker.x, attacker.z),
                new float2(position.x, position.z)) <= radius * radius;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatServerGroup))]
    public partial class EnemyDeathCleanupSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSimulationTime>();

        protected override void OnUpdate()
        {
            double now = SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds;
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (health, lifecycle, entity) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<CombatDeathLifecycle>>()
                    .WithAll<CombatTargetTag>().WithEntityAccess())
            {
                if (health.ValueRO.Dead != 0 && lifecycle.ValueRO.Scheduled != 0 &&
                    now >= lifecycle.ValueRO.DespawnAtSeconds)
                    commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
        }
    }
}
