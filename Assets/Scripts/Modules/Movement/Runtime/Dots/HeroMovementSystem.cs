using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Session;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;

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

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class HeroMovementSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if (SystemAPI.TryGetSingleton(out RunActivityState Activity) && (Activity.Paused != 0 || Activity.Terminal != 0))
            {
                return;
            }
            float Delta = SystemAPI.Time.DeltaTime;
            NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            foreach (var (Transform, Input, Tuning, Hero) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<HeroMoveInput>,
                RefRO<HeroMovementTuning>>().WithAll<Simulate>().WithEntityAccess())
            {
                if (EntityManager.HasComponent<CombatHealth>(Hero) && EntityManager.GetComponentData<CombatHealth>(Hero).Dead != 0)
                {
                    continue;
                }
                float2 Direction = Input.ValueRO.Direction;
                if (!math.all(math.isfinite(Direction)) || Input.ValueRO.Run > 1)
                {
                    continue;
                }
                if (math.lengthsq(Direction) > 1)
                {
                    Direction = math.normalizesafe(Direction);
                }
                float Speed = Tuning.ValueRO.WalkSpeedMetersPerSecond;
                if (Input.ValueRO.Run != 0)
                {
                    Speed = Tuning.ValueRO.RunSpeedMetersPerSecond;
                }
                bool bMotion = false;
                float2 Translation = Direction * Speed * Delta;
                if (EntityManager.HasComponent<HeroActionState>(Hero))
                {
                    HeroActionState Action = EntityManager.GetComponentData<HeroActionState>(Hero);
                    if (Action.DashRemaining > 0 || Action.SpecialMotionRemaining > 0)
                    {
                        bMotion = true;
                        float Remaining = math.max(Action.DashRemaining, Action.SpecialMotionRemaining);
                        float Step = math.min(Delta, Remaining);
                        float PreviousProgress = math.saturate(Action.MotionElapsed / Action.MotionDuration);
                        float Progress = math.saturate((Action.MotionElapsed + Step) / Action.MotionDuration);
                        if (Action.MotionKind == 2)
                        {
                            PreviousProgress = 1 - (1 - PreviousProgress) * (1 - PreviousProgress);
                            Progress = 1 - (1 - Progress) * (1 - Progress);
                        }
                        Translation = Action.AttackDirection * Action.MotionDistance * (Progress - PreviousProgress);
                    }
                    else if (Action.RecoilRemaining > 0)
                    {
                        CombatFeelConfiguration Feel = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration;
                        Translation -= Action.RecoilDirection * math.max(0, math.dot(Translation, Action.RecoilDirection)) * (1 - Feel.AutoForwardSpeed);
                    }
                }
                float2 Position = Transform.ValueRO.Position.xz;
                int Steps = math.max(1, (int)math.ceil(math.length(Translation) / 0.2f));
                for (int Index = 0; Index < Steps; Index++)
                {
                    float2 Next = Position + Translation / Steps;
                    bool bWalkable = true;
                    if (Navigation != null)
                    {
                        bWalkable = Navigation.IsWalkable(Next);
                        if (bMotion)
                        {
                            bWalkable = Navigation.IsStaticWalkable(Next);
                        }
                    }
                    if (bWalkable)
                    {
                        Position = Next;
                    }
                    else if (!bMotion)
                    {
                        float2 Horizontal = new float2(Next.x, Position.y);
                        float2 Vertical = new float2(Position.x, Next.y);
                        if (Navigation.IsWalkable(Horizontal))
                        {
                            Position = Horizontal;
                        }
                        else if (Navigation.IsWalkable(Vertical))
                        {
                            Position = Vertical;
                        }
                    }
                }
                float GroundHeight = Transform.ValueRO.Position.y;
                if (Navigation != null && Navigation.Map != null)
                {
                    GroundHeight = Navigation.Map.Height(Position.x, Position.y);
                }
                Transform.ValueRW.Position = new float3(Position.x, GroundHeight, Position.y);
            }
        }
    }
}
