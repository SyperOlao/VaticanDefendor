using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup), OrderFirst = true)]
    [UpdateBefore(typeof(CombatResolutionSystem))]
    public partial class CombatImpactSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<CombatFeelSettings>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.TryGetSingleton(out RunActivityState Activity) && (Activity.Paused != 0 || Activity.Terminal != 0))
            {
                return;
            }
            float Delta = SystemAPI.Time.DeltaTime;
            CombatFeelConfiguration Feel = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration;
            NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            foreach (var (Impact, Position, Health) in SystemAPI.Query<RefRW<CombatImpactState>,
                RefRW<LocalTransform>, RefRO<CombatHealth>>().WithAll<Simulate>())
            {
                Impact.ValueRW.FlashRemaining = math.max(0, Impact.ValueRO.FlashRemaining - Delta);
                if (Health.ValueRO.Dead != 0)
                {
                    Impact.ValueRW.DeathElapsed = math.min(Feel.CorpseDuration, Impact.ValueRO.DeathElapsed + Delta);
                }
                else
                {
                    Impact.ValueRW.DeathElapsed = 0;
                }
                if (Impact.ValueRO.PushRemaining <= 0)
                {
                    continue;
                }
                float Step = math.min(Delta, Impact.ValueRO.PushRemaining);
                float2 Translation = Impact.ValueRO.Velocity * Step * (Impact.ValueRO.PushRemaining - Step * 0.5f) / Impact.ValueRO.PushDuration;
                int Steps = math.max(1, (int)math.ceil(math.length(Translation) / 0.2f));
                float2 Current = Position.ValueRO.Position.xz;
                for (int Index = 0; Index < Steps; Index++)
                {
                    float2 Next = Current + Translation / Steps;
                    if (Navigation != null && !Navigation.IsWalkable(Next))
                    {
                        break;
                    }
                    Current = Next;
                }
                float GroundHeight = Position.ValueRO.Position.y;
                if (Navigation != null && Navigation.Map != null)
                {
                    GroundHeight = Navigation.Map.Height(Current.x, Current.y);
                }
                Position.ValueRW.Position = new float3(Current.x, GroundHeight, Current.y);
                Impact.ValueRW.PushRemaining = math.max(0, Impact.ValueRO.PushRemaining - Step);
            }
        }
    }
}
