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

    public struct EnemyAgentTuning : IComponentData
    {
        public float SpeedMetersPerSecond;
        public float FormationDistanceMeters;
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

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(NavigationFlowSystem))]
    public partial class EnemyDecisionSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<NavigationGoalRequest>();

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 ||
                SystemAPI.GetSingleton<RunActivityState>().Terminal != 0) return;
            Entity target = Entity.Null;
            float3 targetPosition = float3.zero;
            foreach (var (health, transform, entity) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>()
                    .WithAll<HeroCombatStamina>().WithEntityAccess())
            {
                if (health.ValueRO.Dead != 0) continue;
                target = entity;
                targetPosition = transform.ValueRO.Position;
                break;
            }
            if (target == Entity.Null)
            {
                foreach (var (health, transform, entity) in
                    SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>()
                        .WithAll<ChurchTag>().WithEntityAccess())
                {
                    if (health.ValueRO.Dead != 0) continue;
                    target = entity;
                    targetPosition = transform.ValueRO.Position;
                    break;
                }
            }
            NavigationGoalRequest sharedGoal = new NavigationGoalRequest
            {
                Active = (byte)(target != Entity.Null ? 1 : 0),
                Position = new float2(targetPosition.x, targetPosition.z)
            };
            SystemAPI.SetSingleton(sharedGoal);

            foreach (var (health, transform, goal, attackState, tuning, attacks) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>,
                    RefRW<NavigationGoal>, RefRO<EnemyCombatAttackState>,
                    RefRO<EnemyCombatTuning>,
                    DynamicBuffer<EnemyAttackIntent>>().WithAll<EnemyAgentTag>())
            {
                bool active = target != Entity.Null && health.ValueRO.Dead == 0;
                goal.ValueRW.Active = (byte)(active ? 1 : 0);
                goal.ValueRW.Position = sharedGoal.Position;
                if (!active || attackState.ValueRO.Phase != 0) continue;
                float3 position = transform.ValueRO.Position;
                float2 offset = sharedGoal.Position - new float2(position.x, position.z);
                if (math.lengthsq(offset) <=
                    tuning.ValueRO.AttackRadiusMeters * tuning.ValueRO.AttackRadiusMeters)
                {
                    attacks.Add(new EnemyAttackIntent { Target = target });
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NavigationFlowSystem))]
    [UpdateBefore(typeof(CombatServerGroup))]
    public partial class EnemyMovementSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSimulationTime>();

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 ||
                SystemAPI.GetSingleton<RunActivityState>().Terminal != 0) return;
            float delta = SystemAPI.GetSingleton<RunSimulationTime>().DeltaSeconds;
            NavigationFlowSystem navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            foreach (var (health, transform, steering, tuning) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRW<LocalTransform>,
                    RefRO<NavigationSteering>, RefRO<EnemyAgentTuning>>().WithAll<EnemyAgentTag>())
            {
                if (health.ValueRO.Dead != 0) continue;
                float2 direction = steering.ValueRO.Direction;
                if (math.lengthsq(direction) < 0.01f) continue;
                float3 position = transform.ValueRO.Position;
                float2 next = new float2(position.x, position.z) +
                    direction * tuning.ValueRO.SpeedMetersPerSecond * delta;
                if (!navigation.IsWalkable(next)) continue;
                transform.ValueRW.Position = new float3(next.x, position.y, next.y);
            }
        }
    }
}
