using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Session;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Movement
{
    public struct SessionContentRelease : IComponentData
    {
        public FixedString128Bytes ReleaseId;
    }

    public struct HeroMovementTuning : IComponentData
    {
        [GhostField]
        public float WalkSpeedMetersPerSecond;
        [GhostField]
        public float RunSpeedMetersPerSecond;
    }

    public struct HeroMoveInput : IInputComponentData
    {
        public float2 Direction;
        public byte Run;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class HeroMovementSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if ((World.Flags & WorldFlags.GameServer) != 0 &&
                SystemAPI.TryGetSingleton(out RunActivityState activity) &&
                (activity.Paused != 0 || activity.Terminal != 0)) return;
            float deltaSeconds = SystemAPI.Time.DeltaTime;
            foreach (var (transform, intent, tuning, entity) in
                SystemAPI.Query<RefRW<LocalTransform>, RefRO<HeroMoveInput>,
                    RefRO<HeroMovementTuning>>().WithAll<Simulate>().WithEntityAccess())
            {
                if (EntityManager.HasComponent<CombatHealth>(entity) &&
                    EntityManager.GetComponentData<CombatHealth>(entity).Dead != 0) continue;
                float2 direction = intent.ValueRO.Direction;
                if (!math.all(math.isfinite(direction)) || intent.ValueRO.Run > 1)
                {
                    continue;
                }

                float lengthSquared = math.lengthsq(direction);
                if (lengthSquared > 1f)
                {
                    direction *= math.rsqrt(lengthSquared);
                }

                float speed = intent.ValueRO.Run == 0
                    ? tuning.ValueRO.WalkSpeedMetersPerSecond
                    : tuning.ValueRO.RunSpeedMetersPerSecond;
                transform.ValueRW.Position += new float3(direction.x, 0f, direction.y) * speed * deltaSeconds;
            }
        }
    }
}
