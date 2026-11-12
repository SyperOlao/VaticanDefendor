using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup))]
    [UpdateAfter(typeof(EnemyCombatResolutionSystem))]
    public partial class HeroRecoverySystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<HeroActionTuning>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            HeroActionTuning Tuning = SystemAPI.GetSingleton<HeroActionTuning>();
            float Delta = SystemAPI.Time.DeltaTime;
            float3 ChurchPosition = float3.zero;
            bool bChurchAlive = false;
            foreach (var (Health, Position) in SystemAPI.Query<RefRW<CombatHealth>, RefRO<LocalTransform>>().WithAll<ChurchTag, Simulate>())
            {
                if (Health.ValueRO.Dead == 0)
                {
                    Health.ValueRW.Current = math.min(Health.ValueRO.Maximum, Health.ValueRO.Current + Tuning.ChurchHealingPerSecond * Delta);
                    ChurchPosition = Position.ValueRO.Position;
                    bChurchAlive = true;
                }
            }
            foreach (var (Health, Action, Position, Stamina) in
                SystemAPI.Query<RefRW<CombatHealth>, RefRW<HeroActionState>, RefRW<LocalTransform>, RefRW<HeroCombatStamina>>().WithAll<Simulate>())
            {
                if (Health.ValueRO.Dead != 0)
                {
                    if (Action.ValueRO.RespawnRemaining <= 0)
                    {
                        Action.ValueRW.RespawnRemaining = Tuning.RespawnSeconds;
                    }
                    Action.ValueRW.RespawnRemaining -= Delta;
                    if (Action.ValueRO.RespawnRemaining <= 0 && bChurchAlive)
                    {
                        NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
                        float2 Spawn = Navigation.NearestWalkable(ChurchPosition.xz + new float2(0, 3));
                        float Height = 0;
                        if (Navigation.Map != null)
                        {
                            Height = Navigation.Map.Height(Spawn.x, Spawn.y);
                        }
                        Position.ValueRW.Position = new float3(Spawn.x, Height, Spawn.y);
                        Health.ValueRW.Current = Health.ValueRO.Maximum;
                        Health.ValueRW.Dead = 0;
                        Stamina.ValueRW.Current = Stamina.ValueRO.Maximum;
                        Action.ValueRW.Gathering = 0;
                        Action.ValueRW.Combo = 0;
                        Action.ValueRW.DashRemaining = 0;
                        Action.ValueRW.RespawnRemaining = 0;
                        Action.ValueRW.SpecialMotionRemaining = 0;
                        Action.ValueRW.MotionKind = 0;
                        Action.ValueRW.ChargeProgress = 0;
                        Action.ValueRW.GuardRemaining = 0;
                        Action.ValueRW.ComboPendingLoss = 0;
                        Action.ValueRW.Shield = 0;
                        Action.ValueRW.DamageBonusRemaining = 0;
                        Action.ValueRW.SpecialVisualRemaining = 0;
                        Action.ValueRW.RecoilRemaining = 0;
                    }
                }
                else if (bChurchAlive && math.distancesq(Position.ValueRO.Position, ChurchPosition) <= Tuning.HealingRange * Tuning.HealingRange)
                {
                    Health.ValueRW.Current = math.min(Health.ValueRO.Maximum, Health.ValueRO.Current + Tuning.HeroHealingPerSecond * Delta);
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup))]
    [UpdateAfter(typeof(CombatResolutionSystem))]
    public partial class StructureCombatSystem : SystemBase
    {
        private EntityQuery _enemies;
        private EntityQuery _heroes;

        protected override void OnCreate()
        {
            _enemies = GetEntityQuery(typeof(CombatTargetTag), typeof(CombatHealth), typeof(LocalTransform));
            _heroes = GetEntityQuery(typeof(HeroActionState), typeof(CombatHealth), typeof(LocalTransform));
            RequireForUpdate<HeroActionTuning>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            using NativeArray<Entity> Enemies = _enemies.ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> Heroes = _heroes.ToEntityArray(Allocator.Temp);
            foreach (var (Weapon, Position, Health) in SystemAPI.Query<RefRW<StructureCombatState>, RefRO<LocalTransform>, RefRW<CombatHealth>>().WithAll<Simulate>())
            {
                if (Health.ValueRO.Dead != 0 || Weapon.ValueRO.Interval <= 0 || Weapon.ValueRO.Range <= 0)
                {
                    continue;
                }
                Weapon.ValueRW.Remaining = math.max(0, Weapon.ValueRO.Remaining - SystemAPI.Time.DeltaTime);
                if (Weapon.ValueRO.Remaining > 0)
                {
                    continue;
                }
                if (Weapon.ValueRO.Healing == 2)
                {
                    Health.ValueRW.Current = 0;
                    Health.ValueRW.Dead = 1;
                    foreach (Entity Enemy in Enemies)
                    {
                        if (math.distancesq(Position.ValueRO.Position, EntityManager.GetComponentData<LocalTransform>(Enemy).Position) <= Weapon.ValueRO.Range * Weapon.ValueRO.Range)
                        {
                            CombatDamage.Apply(EntityManager, Enemy, Weapon.ValueRO.Damage);
                        }
                    }
                    continue;
                }
                if (Weapon.ValueRO.Healing == 1)
                {
                    foreach (Entity Hero in Heroes)
                    {
                        CombatHealth HeroHealth = EntityManager.GetComponentData<CombatHealth>(Hero);
                        if (HeroHealth.Dead == 0 && math.distancesq(Position.ValueRO.Position,
                            EntityManager.GetComponentData<LocalTransform>(Hero).Position) <= Weapon.ValueRO.Range * Weapon.ValueRO.Range)
                        {
                            HeroHealth.Current = math.min(HeroHealth.Maximum, HeroHealth.Current + Weapon.ValueRO.Damage);
                            EntityManager.SetComponentData(Hero, HeroHealth);
                        }
                    }
                    Weapon.ValueRW.Remaining = Weapon.ValueRO.Interval;
                    continue;
                }
                Entity Target = Entity.Null;
                float Best = Weapon.ValueRO.Range * Weapon.ValueRO.Range;
                foreach (Entity Enemy in Enemies)
                {
                    if (EntityManager.GetComponentData<CombatHealth>(Enemy).Dead != 0)
                    {
                        continue;
                    }
                    float Distance = math.distancesq(Position.ValueRO.Position, EntityManager.GetComponentData<LocalTransform>(Enemy).Position);
                    if (Distance < Best)
                    {
                        Best = Distance;
                        Target = Enemy;
                    }
                }
                if (Target != Entity.Null)
                {
                    using EntityQuery ChurchQuery = EntityManager.CreateEntityQuery(typeof(ChurchTag), typeof(CombatProjectileState));
                    if (!ChurchQuery.IsEmptyIgnoreFilter)
                    {
                        Weapon.ValueRW.AttackSequence++;
                        EntityManager.GetBuffer<CombatProjectileState>(ChurchQuery.GetSingletonEntity()).Add(new CombatProjectileState
                        {
                            Position = Position.ValueRO.Position.xz,
                            Direction = math.normalizesafe(EntityManager.GetComponentData<LocalTransform>(Target).Position.xz - Position.ValueRO.Position.xz),
                            Speed = 16, Damage = Weapon.ValueRO.Damage, Remaining = Weapon.ValueRO.Range + 2,
                            Friendly = 1, Push = 0.5f, Sequence = Weapon.ValueRO.AttackSequence,
                            SourceId = (int)math.hash(Position.ValueRO.Position.xz)
                        });
                    }
                    Weapon.ValueRW.Remaining = Weapon.ValueRO.Interval;
                }
            }
        }
    }
}
