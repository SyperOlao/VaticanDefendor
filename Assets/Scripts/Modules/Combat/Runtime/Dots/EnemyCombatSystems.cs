using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup))]
    [UpdateAfter(typeof(CombatResolutionSystem))]
    public partial class EnemyCombatResolutionSystem : SystemBase
    {
        private EntityQuery _actors;
        private EntityQuery _enemies;
        private EntityQuery _feel;
        private EntityQuery _church;

        protected override void OnCreate()
        {
            RequireForUpdate<RunSimulationTime>();
            RequireForUpdate<EnemyCombatConcurrency>();
            _actors = GetEntityQuery(typeof(CombatHealth), typeof(LocalTransform));
            _enemies = GetEntityQuery(typeof(CombatHealth), typeof(LocalTransform), typeof(EnemyCombatTuning),
                typeof(EnemyCombatAttackState), typeof(EnemyAttackIntent), typeof(Simulate));
            _feel = GetEntityQuery(typeof(CombatFeelSettings));
            _church = GetEntityQuery(typeof(ChurchTag), typeof(CombatProjectileState));
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            float Delta = SystemAPI.Time.DeltaTime;
            double Now = SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds;
            CombatFeelConfiguration Feel = null;
            if (!_feel.IsEmptyIgnoreFilter)
            {
                Feel = EntityManager.GetComponentObject<CombatFeelSettings>(_feel.GetSingletonEntity()).Configuration;
            }
            int MaximumWindups = SystemAPI.GetSingleton<EnemyCombatConcurrency>().MaximumSimultaneousWindups;
            int ActiveWindups = 0;
            using NativeArray<Entity> Enemies = _enemies.ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> Actors = _actors.ToEntityArray(Allocator.Temp);
            foreach (Entity Enemy in Enemies)
            {
                byte Phase = EntityManager.GetComponentData<EnemyCombatAttackState>(Enemy).Phase;
                if (Phase == 1 || Phase == 3)
                {
                    ActiveWindups++;
                }
            }
            Navigation.NavigationFlowSystem Navigation = World.GetExistingSystemManaged<Navigation.NavigationFlowSystem>();
            foreach (Entity Enemy in Enemies)
            {
                EnemyCombatAttackState Attack = EntityManager.GetComponentData<EnemyCombatAttackState>(Enemy);
                EnemyCombatTuning Tuning = EntityManager.GetComponentData<EnemyCombatTuning>(Enemy);
                LocalTransform Transform = EntityManager.GetComponentData<LocalTransform>(Enemy);
                DynamicBuffer<EnemyAttackIntent> Intents = EntityManager.GetBuffer<EnemyAttackIntent>(Enemy);
                if (EntityManager.GetComponentData<CombatHealth>(Enemy).Dead != 0)
                {
                    if (Attack.Phase == 1 || Attack.Phase == 3)
                    {
                        ActiveWindups--;
                    }
                    Attack.Phase = 0;
                    Intents.Clear();
                    EntityManager.SetComponentData(Enemy, Attack);
                    continue;
                }
                float PreviousRemaining = Attack.Remaining;
                Attack.Remaining = math.max(0, Attack.Remaining - Delta);
                if (Attack.Phase == 2 && Attack.Remaining <= 0)
                {
                    Attack.Phase = 0;
                }
                if (Attack.Phase == 0 && Intents.Length > 0 && ActiveWindups < MaximumWindups &&
                    CanHit(Intents[0].Target, Transform.Position.xz, Tuning.AttackRadiusMeters, Tuning.MinimumRange))
                {
                    Attack.Target = Intents[0].Target;
                    Attack.Origin = Transform.Position.xz;
                    Attack.Direction = math.normalizesafe(EntityManager.GetComponentData<LocalTransform>(Attack.Target).Position.xz - Attack.Origin, new float2(0, 1));
                    Attack.Phase = 1;
                    Attack.Remaining = Tuning.WindupSeconds;
                    Attack.PhaseEndsSeconds = Now + Attack.Remaining;
                    Attack.Sequence++;
                    if (EntityManager.HasBuffer<CombatAttackVictim>(Enemy))
                    {
                        EntityManager.GetBuffer<CombatAttackVictim>(Enemy).Clear();
                    }
                    ActiveWindups++;
                }
                Intents.Clear();
                if (Attack.Phase == 1 && Attack.Remaining <= 0)
                {
                    if (Tuning.Kind == 3 && !_church.IsEmptyIgnoreFilter)
                    {
                        EntityManager.GetBuffer<CombatProjectileState>(_church.GetSingletonEntity()).Add(new CombatProjectileState
                        {
                            Position = Attack.Origin, Direction = Attack.Direction, Speed = Tuning.ProjectileSpeed,
                            Damage = Tuning.Damage, Remaining = Tuning.AttackRadiusMeters, Sequence = Attack.Sequence,
                            SourceId = EntityManager.GetComponentData<PersistentTargetId>(Enemy).Value
                        });
                    }
                    else if (Tuning.Kind != 2)
                    {
                        ResolveArea(Enemy, Attack, Tuning, Attack.Origin, Attack.Origin, Tuning.AttackRadiusMeters, Actors, Navigation, Feel);
                    }
                    if (Tuning.Kind == 2)
                    {
                        Attack.Phase = 3;
                        Attack.Remaining = Tuning.DashDuration;
                    }
                    else
                    {
                        Attack.Phase = 2;
                        Attack.Remaining = Tuning.RecoverySeconds;
                        ActiveWindups--;
                    }
                    Attack.PhaseEndsSeconds = Now + Attack.Remaining;
                }
                else if (Attack.Phase == 3)
                {
                    float Step = math.min(Delta, PreviousRemaining);
                    float2 Previous = Transform.Position.xz;
                    float2 Next = Previous + Attack.Direction * Tuning.DashDistance / Tuning.DashDuration * Step;
                    int Steps = math.max(1, (int)math.ceil(math.distance(Previous, Next) / 0.2f));
                    float2 Current = Previous;
                    for (int Index = 0; Index < Steps; Index++)
                    {
                        float2 Candidate = math.lerp(Previous, Next, (Index + 1f) / Steps);
                        if (Navigation != null && !Navigation.IsWalkable(Candidate))
                        {
                            break;
                        }
                        Current = Candidate;
                    }
                    float GroundHeight = Transform.Position.y;
                    if (Navigation != null && Navigation.Map != null)
                    {
                        GroundHeight = Navigation.Map.Height(Current.x, Current.y);
                    }
                    Transform.Position = new float3(Current.x, GroundHeight, Current.y);
                    EntityManager.SetComponentData(Enemy, Transform);
                    ResolveArea(Enemy, Attack, Tuning, Previous, Current, Tuning.ThrustLength, Actors, Navigation, Feel);
                    if (Attack.Remaining <= 0)
                    {
                        Attack.Phase = 2;
                        Attack.Remaining = Tuning.RecoverySeconds;
                        Attack.PhaseEndsSeconds = Now + Attack.Remaining;
                        ActiveWindups--;
                    }
                }
                EntityManager.SetComponentData(Enemy, Attack);
            }
        }

        private void ResolveArea(Entity Enemy, EnemyCombatAttackState Attack, EnemyCombatTuning Tuning,
            float2 Previous, float2 Current, float Radius, NativeArray<Entity> Actors,
            Navigation.NavigationFlowSystem Navigation, CombatFeelConfiguration Feel)
        {
            foreach (Entity Victim in Actors)
            {
                if (!IsVictim(Victim))
                {
                    continue;
                }
                bool bAlreadyHit = false;
                if (EntityManager.HasBuffer<CombatAttackVictim>(Enemy))
                {
                    foreach (CombatAttackVictim Contact in EntityManager.GetBuffer<CombatAttackVictim>(Enemy))
                    {
                        if (Contact.Target == Victim)
                        {
                            bAlreadyHit = true;
                            break;
                        }
                    }
                }
                float2 Position = EntityManager.GetComponentData<LocalTransform>(Victim).Position.xz;
                float Width = math.max(0.65f, Tuning.HalfWidth);
                float AttackRadius = Radius;
                if (!EntityManager.HasComponent<HeroCombatStamina>(Victim))
                {
                    AttackRadius += 0.6f;
                    Width += 0.5f;
                }
                int Shape = 1;
                if (Tuning.Kind == 1)
                {
                    Shape = 2;
                }
                if (bAlreadyHit || !CombatDamage.InWeaponArea(Position, Previous, Current, Attack.Direction, AttackRadius, Width, Shape, false, 1) ||
                    Navigation != null && !CombatDamage.HasLineOfSight(Navigation, Current, Position, Victim))
                {
                    continue;
                }
                float2 Direction = Attack.Direction;
                if (Tuning.Kind == 1)
                {
                    Direction = math.normalizesafe(Position - Current);
                }
                if (Feel == null)
                {
                    CombatDamage.Apply(EntityManager, Victim, Tuning.Damage);
                }
                else
                {
                    float Push = 0;
                    if (EntityManager.HasComponent<HeroActionState>(Victim))
                    {
                        Push = Feel.PlayerHitPush;
                    }
                    CombatDamage.Hit(EntityManager, Victim, Tuning.Damage, Direction, Push, Feel.PlayerHitDuration, 0, false, Feel);
                }
                if (EntityManager.HasBuffer<CombatAttackVictim>(Enemy))
                {
                    EntityManager.GetBuffer<CombatAttackVictim>(Enemy).Add(new CombatAttackVictim { Target = Victim });
                }
            }
        }

        private bool CanHit(Entity Target, float2 Origin, float Radius, float MinimumRange)
        {
            if (!IsVictim(Target))
            {
                return false;
            }
            float Distance = math.distance(Origin, EntityManager.GetComponentData<LocalTransform>(Target).Position.xz);
            return Distance <= Radius + 0.6f && Distance >= MinimumRange;
        }

        private bool IsVictim(Entity Target)
        {
            if (Target == Entity.Null || !EntityManager.Exists(Target) || !EntityManager.HasComponent<CombatHealth>(Target) ||
                !EntityManager.HasComponent<LocalTransform>(Target) || EntityManager.HasComponent<CombatTargetTag>(Target))
            {
                return false;
            }
            if (!EntityManager.HasComponent<HeroCombatStamina>(Target) && !EntityManager.HasComponent<ChurchTag>(Target) &&
                !EntityManager.HasComponent<DestructibleTree>(Target) &&
                !EntityManager.HasComponent<DestructibleRock>(Target) &&
                (!EntityManager.HasComponent<StructureCombatState>(Target) || EntityManager.GetComponentData<StructureCombatState>(Target).Level <= 0))
            {
                return false;
            }
            return EntityManager.GetComponentData<CombatHealth>(Target).Dead == 0;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
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
