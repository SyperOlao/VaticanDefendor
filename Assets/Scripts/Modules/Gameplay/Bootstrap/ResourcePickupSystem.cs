using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class ResourcePickupSystem : SystemBase
    {
        private EntityQuery Heroes;

        protected override void OnCreate()
        {
            Heroes = GetEntityQuery(typeof(HeroActionState), typeof(CombatHealth), typeof(LocalTransform));
            RequireForUpdate<ResourcePickup>();
            RequireForUpdate<ResourcePickupTuning>();
            RequireForUpdate<RunEconomyState>();
            RequireForUpdate<RunSimulationTime>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            ResourcePickupTuning Tuning = SystemAPI.GetSingleton<ResourcePickupTuning>();
            float Delta = SystemAPI.GetSingleton<RunSimulationTime>().DeltaSeconds;
            if (Delta <= 0)
            {
                return;
            }
            RunEconomyState Economy = SystemAPI.GetSingleton<RunEconomyState>();
            using NativeArray<Entity> Candidates = Heroes.ToEntityArray(Allocator.Temp);
            foreach (DynamicBuffer<ResourcePickup> Buffer in SystemAPI.Query<DynamicBuffer<ResourcePickup>>().WithAll<ChurchTag>())
            {
                DynamicBuffer<ResourcePickup> Pickups = Buffer;
                for (int PickupIndex = Pickups.Length - 1; PickupIndex >= 0; PickupIndex--)
                {
                    ResourcePickup Pickup = Pickups[PickupIndex];
                    Pickup.Age += Delta;
                    float BestDistance = float.MaxValue;
                    float3 Target = Pickup.Position;
                    bool bFound = false;
                    foreach (Entity Hero in Candidates)
                    {
                        if (EntityManager.GetComponentData<CombatHealth>(Hero).Dead != 0)
                        {
                            continue;
                        }
                        float3 Position = EntityManager.GetComponentData<LocalTransform>(Hero).Position;
                        float Distance = math.distancesq(Position.xz, Pickup.Position.xz);
                        if (Distance < BestDistance)
                        {
                            BestDistance = Distance;
                            Target = Position + new float3(0, 0.65f, 0);
                            bFound = true;
                        }
                    }
                    if (bFound && Pickup.Age >= Tuning.ReleaseDelay &&
                        (Pickup.Attracted != 0 || BestDistance <= Tuning.AttractionRadius * Tuning.AttractionRadius))
                    {
                        Pickup.Attracted = 1;
                        if (Pickup.AttractionElapsed == 0)
                        {
                            Pickup.AttractionOrigin = Pickup.Position;
                        }
                        Pickup.AttractionElapsed += Delta;
                        float Progress = Pickup.AttractionElapsed / Tuning.AttractionSeconds;
                        if (Progress >= 1)
                        {
                            RunEconomySession.Credit(ref Economy, ResourceAmounts.For(Pickup.Kind, Pickup.Amount));
                            Pickups.RemoveAt(PickupIndex);
                            continue;
                        }
                        Pickup.Position = math.lerp(Pickup.AttractionOrigin, Target, Progress);
                    }
                    Pickups[PickupIndex] = Pickup;
                }
            }
            SystemAPI.SetSingleton(Economy);
        }
    }
}
