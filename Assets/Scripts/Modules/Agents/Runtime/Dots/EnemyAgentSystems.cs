using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Agents
{
    public struct EnemyAgentTag : IComponentData { }

    internal struct EnemyTargetCandidate
    {
        public Entity Entity;
        public float2 Position;
        public bool Hero;
    }

    public struct EnemyAgentTuning : IComponentData
    {
        [GhostField] public float SpeedMetersPerSecond;
        [GhostField] public float FormationDistanceMeters;
    }

    public struct EnemyAgentRunTuning : IComponentData
    {
        public float SpeedMetersPerSecond;
        public float SpeedVariationMetersPerSecond;
        public float FormationDistanceMeters;
        public float AttackDamage;
        public float AttackRadiusMeters;
        public float WindupSeconds;
        public float RecoverySeconds;
        public float DespawnDelaySeconds;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(NavigationFlowSystem))]
    public partial class EnemyDecisionSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<NavigationGoalRequest>();

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 ||
                SystemAPI.GetSingleton<RunActivityState>().Terminal != 0) return;
            Entity Church = Entity.Null;
            float2 ChurchPosition = float2.zero;
            foreach (var (Health, Transform, Candidate) in SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>()
                .WithAll<ChurchTag>().WithEntityAccess())
            {
                if (Health.ValueRO.Dead == 0)
                {
                    Church = Candidate;
                    ChurchPosition = Transform.ValueRO.Position.xz;
                    break;
                }
            }
            SystemAPI.SetSingleton(new NavigationGoalRequest { Active = (byte)(Church != Entity.Null ? 1 : 0), Position = ChurchPosition });
            using var Candidates = new Unity.Collections.NativeList<EnemyTargetCandidate>(Unity.Collections.Allocator.Temp);
            foreach (var (Health, Transform, Candidate) in SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (Health.ValueRO.Dead != 0 || EntityManager.HasComponent<CombatTargetTag>(Candidate))
                {
                    continue;
                }
                bool bHero = EntityManager.HasComponent<HeroCombatStamina>(Candidate);
                bool bStructure = EntityManager.HasComponent<StructureCombatState>(Candidate) &&
                    EntityManager.GetComponentData<StructureCombatState>(Candidate).Level > 0;
                if (!bHero && !bStructure && Candidate != Church)
                {
                    continue;
                }
                Candidates.Add(new EnemyTargetCandidate { Entity = Candidate, Position = Transform.ValueRO.Position.xz, Hero = bHero });
            }
            foreach (var (Health, Transform, Goal, Tuning) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>, RefRW<NavigationGoal>, RefRO<EnemyCombatTuning>>()
                    .WithAll<EnemyAgentTag, Simulate>())
            {
                Entity Target = Church;
                float2 Position = Transform.ValueRO.Position.xz;
                float2 TargetPosition = ChurchPosition;
                float Best = math.distance(Position, ChurchPosition);
                foreach (EnemyTargetCandidate Candidate in Candidates)
                {
                    float Distance = math.distance(Position, Candidate.Position);
                    if (Candidate.Hero && Distance > 6)
                    {
                        continue;
                    }
                    if (Distance < Best)
                    {
                        Best = Distance;
                        Target = Candidate.Entity;
                        TargetPosition = Candidate.Position;
                    }
                }
                bool bActive = Target != Entity.Null && Health.ValueRO.Dead == 0;
                Goal.ValueRW.Target = Target;
                Goal.ValueRW.Active = (byte)(bActive ? 1 : 0);
                Goal.ValueRW.Position = TargetPosition;
                Goal.ValueRW.StoppingDistanceMeters = Tuning.ValueRO.AttackRadiusMeters + 0.5f;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(NavigationFlowSystem))]
    [UpdateBefore(typeof(CombatSimulationGroup))]
    public partial class EnemyMovementSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSimulationTime>();

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 ||
                SystemAPI.GetSingleton<RunActivityState>().Terminal != 0) return;
            float delta = SystemAPI.GetSingleton<RunSimulationTime>().DeltaSeconds;
            NavigationFlowSystem navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            foreach (var (health, transform, steering, tuning, Enemy) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRW<LocalTransform>,
                    RefRO<NavigationSteering>, RefRO<EnemyAgentTuning>>().WithAll<EnemyAgentTag, Simulate>().WithEntityAccess())
            {
                if (health.ValueRO.Dead != 0)
                {
                    continue;
                }
                if (EntityManager.HasComponent<CombatImpactState>(Enemy) &&
                    EntityManager.GetComponentData<CombatImpactState>(Enemy).PushRemaining > 0)
                {
                    continue;
                }
                if (EntityManager.HasComponent<EnemyCombatAttackState>(Enemy) &&
                    (EntityManager.GetComponentData<EnemyCombatAttackState>(Enemy).Phase == 1 ||
                        EntityManager.GetComponentData<EnemyCombatAttackState>(Enemy).Phase == 3))
                {
                    continue;
                }
                float2 direction = steering.ValueRO.Direction;
                if (EntityManager.HasComponent<EnemyCombatTuning>(Enemy))
                {
                    EnemyCombatTuning Combat = EntityManager.GetComponentData<EnemyCombatTuning>(Enemy);
                    Entity Target = steering.ValueRO.Target;
                    if (Combat.MinimumRange > 0 && Target != Entity.Null && EntityManager.Exists(Target))
                    {
                        float2 Away = transform.ValueRO.Position.xz - EntityManager.GetComponentData<LocalTransform>(Target).Position.xz;
                        if (math.lengthsq(Away) < Combat.MinimumRange * Combat.MinimumRange)
                        {
                            direction = math.normalizesafe(Away, new float2(0, 1));
                        }
                    }
                }
                if (math.lengthsq(direction) < 0.01f) continue;
                float3 position = transform.ValueRO.Position;
                float2 next = new float2(position.x, position.z) +
                    direction * tuning.ValueRO.SpeedMetersPerSecond * delta;
                if (!navigation.IsWalkable(next)) continue;
                float GroundHeight = position.y;
                if (navigation.Map != null)
                {
                    GroundHeight = navigation.Map.Height(next.x, next.y);
                }
                transform.ValueRW.Position = new float3(next.x, GroundHeight, next.y);
            }
        }
    }
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyMovementSystem))]
    [UpdateBefore(typeof(CombatSimulationGroup))]
    public partial class EnemyAttackIntentSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunActivityState>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            foreach (var (Health, Position, Steering, Tuning, Attack, Intents) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>, RefRO<NavigationSteering>,
                    RefRO<EnemyCombatTuning>, RefRO<EnemyCombatAttackState>, DynamicBuffer<EnemyAttackIntent>>()
                    .WithAll<EnemyAgentTag, Simulate>())
            {
                Intents.Clear();
                Entity Target = Steering.ValueRO.Target;
                if (Health.ValueRO.Dead != 0 || Attack.ValueRO.Phase != 0 || Target == Entity.Null || !EntityManager.Exists(Target))
                {
                    continue;
                }
                if (EntityManager.GetComponentData<CombatHealth>(Target).Dead == 0 &&
                    math.distance(Position.ValueRO.Position.xz, EntityManager.GetComponentData<LocalTransform>(Target).Position.xz) <=
                    Tuning.ValueRO.AttackRadiusMeters + 0.5f)
                {
                    Intents.Add(new EnemyAttackIntent { Target = Target });
                }
            }
        }
    }

}
