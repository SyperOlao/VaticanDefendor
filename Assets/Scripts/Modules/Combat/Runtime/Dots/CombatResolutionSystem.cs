using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class CombatServerGroup : ComponentSystemGroup { }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CombatServerGroup))]
    public partial class CombatResolutionSystem : SystemBase
    {
        private EntityQuery _heroes;
        private EntityQuery _targets;

        protected override void OnCreate()
        {
            _heroes = GetEntityQuery(
                ComponentType.ReadOnly<CombatHealth>(),
                ComponentType.ReadWrite<HeroCombatStamina>(),
                ComponentType.ReadWrite<HeroCombatAttackState>(),
                ComponentType.ReadWrite<CombatAttackCommand>(),
                ComponentType.ReadWrite<CombatAttackResult>(),
                ComponentType.ReadOnly<LocalTransform>());
            _targets = GetEntityQuery(
                ComponentType.ReadOnly<CombatTargetTag>(),
                ComponentType.ReadOnly<CombatHealth>(),
                ComponentType.ReadOnly<LocalTransform>());
            RequireForUpdate<CombatRunTuning>();
            RequireForUpdate<RunEconomyState>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.TryGetSingleton(out RunActivityState activity) &&
                (activity.Paused != 0 || activity.Terminal != 0)) return;
            CombatRunTuning tuning = SystemAPI.GetSingleton<CombatRunTuning>();
            CombatRunState run = SystemAPI.GetSingleton<CombatRunState>();
            RunEconomyState economy = SystemAPI.GetSingleton<RunEconomyState>();
            RunEconomyTuning reward = SystemAPI.GetSingleton<RunEconomyTuning>();
            double now = SystemAPI.TryGetSingleton(out RunSimulationTime sessionTime)
                ? sessionTime.ElapsedSeconds : SystemAPI.Time.ElapsedTime;
            float deltaSeconds = math.max(0f, SystemAPI.Time.DeltaTime);

            using NativeArray<Entity> heroes = _heroes.ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> targets = _targets.ToEntityArray(Allocator.Temp);
            foreach (Entity hero in heroes)
            {
                HeroCombatStamina stamina = EntityManager.GetComponentData<HeroCombatStamina>(hero);
                stamina.Current = math.min(stamina.Maximum,
                    stamina.Current + tuning.StaminaRecoveryPerSecond * deltaSeconds);
                HeroCombatAttackState attack = EntityManager.GetComponentData<HeroCombatAttackState>(hero);
                CombatHealth heroHealth = EntityManager.GetComponentData<CombatHealth>(hero);
                LocalTransform heroTransform = EntityManager.GetComponentData<LocalTransform>(hero);
                DynamicBuffer<CombatAttackCommand> commands = EntityManager.GetBuffer<CombatAttackCommand>(hero);
                DynamicBuffer<CombatAttackResult> results = EntityManager.GetBuffer<CombatAttackResult>(hero);
                for (int index = 0; index < commands.Length; index++)
                {
                    CombatAttackCommand command = commands[index];
                    if (command.Sequence == 0 || command.Sequence <= attack.LastProcessedCommandSequence)
                    {
                        continue;
                    }
                    attack.LastProcessedCommandSequence = command.Sequence;
                    CombatAttackOutcome outcome;
                    float damage = 0f;
                    float targetHealth = -1f;
                    byte died = 0;
                    if (heroHealth.Dead != 0 || heroHealth.Current <= 0f)
                    {
                        outcome = CombatAttackOutcome.HeroDead;
                    }
                    else if (!CombatRules.TryNormalizeAim(command.Aim, out float2 facing))
                    {
                        outcome = CombatAttackOutcome.InvalidAim;
                    }
                    else if (now < attack.NextAllowedTime)
                    {
                        outcome = CombatAttackOutcome.Cooldown;
                    }
                    else if (stamina.Current < tuning.StaminaCost)
                    {
                        outcome = CombatAttackOutcome.NoStamina;
                    }
                    else
                    {
                        stamina.Current -= tuning.StaminaCost;
                        attack.NextAllowedTime = now + tuning.IntervalSeconds;
                        Entity target = FindNearestTarget(targets, heroTransform.Position, facing, tuning);
                        if (target == Entity.Null)
                        {
                            outcome = CombatAttackOutcome.Miss;
                        }
                        else
                        {
                            outcome = CombatAttackOutcome.Hit;
                            CombatHealth health = EntityManager.GetComponentData<CombatHealth>(target);
                            damage = math.min(health.Current, tuning.Damage);
                            health.Current = math.max(0f, health.Current - damage);
                            if (health.Current == 0f && health.Dead == 0)
                            {
                                health.Dead = 1;
                                died = 1;
                                if (EntityManager.HasComponent<CombatDeathLifecycle>(target))
                                {
                                    CombatDeathLifecycle lifecycle =
                                        EntityManager.GetComponentData<CombatDeathLifecycle>(target);
                                    if (lifecycle.DespawnDelaySeconds >= 0f && lifecycle.Scheduled == 0)
                                    {
                                        lifecycle.Scheduled = 1;
                                        lifecycle.DespawnAtSeconds = now + lifecycle.DespawnDelaySeconds;
                                        EntityManager.SetComponentData(target, lifecycle);
                                    }
                                }
                            }
                            targetHealth = health.Current;
                            health.HitSequence = run.NextEventSequence;
                            EntityManager.SetComponentData(target, health);
                        }
                    }

                    uint eventSequence = run.NextEventSequence++;
                    if (died != 0)
                    {
                        RunEconomySession.ApplyKillReward(ref economy, reward, eventSequence);
                    }
                    results.Add(new CombatAttackResult
                    {
                        EventSequence = eventSequence,
                        CommandSequence = command.Sequence,
                        Outcome = outcome,
                        DamageApplied = damage,
                        TargetHealth = targetHealth,
                        TargetMaximumHealth = tuning.TargetMaximumHealth,
                        TargetDied = died,
                        RunGold = economy.Gold,
                        RunExperience = economy.Experience,
                        RunKills = economy.Kills
                    });
                }
                commands.Clear();
                EntityManager.SetComponentData(hero, stamina);
                EntityManager.SetComponentData(hero, attack);
            }

            SystemAPI.SetSingleton(run);
            SystemAPI.SetSingleton(economy);
        }

        private Entity FindNearestTarget(NativeArray<Entity> targets, float3 heroPosition,
            float2 facing, CombatRunTuning tuning)
        {
            float2 origin = new float2(heroPosition.x, heroPosition.z);
            float bestDistanceSquared = float.MaxValue;
            Entity selected = Entity.Null;
            foreach (Entity candidate in targets)
            {
                CombatHealth health = EntityManager.GetComponentData<CombatHealth>(candidate);
                if (health.Dead != 0 || health.Current <= 0f) continue;
                float3 position = EntityManager.GetComponentData<LocalTransform>(candidate).Position;
                float2 point = new float2(position.x, position.z);
                if (!CombatRules.SwordHits(origin, point, facing,
                    tuning.RangeMeters, tuning.HitPaddingMeters)) continue;
                float distanceSquared = math.lengthsq(point - origin);
                if (distanceSquared < bestDistanceSquared ||
                    distanceSquared == bestDistanceSquared && candidate.Index < selected.Index)
                {
                    bestDistanceSquared = distanceSquared;
                    selected = candidate;
                }
            }
            return selected;
        }
    }
}
