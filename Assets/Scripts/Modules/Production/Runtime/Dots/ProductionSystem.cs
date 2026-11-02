using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Production
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ProductionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSimulationTime>();
            RequireForUpdate<RunActivityState>();
            RequireForUpdate<RunEconomyState>();
        }

        protected override void OnUpdate()
        {
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            if (activity.Paused != 0 || activity.Terminal != 0) return;
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            RunEconomyState economy = SystemAPI.GetSingleton<RunEconomyState>();
            bool hasHero = false;
            float2 heroPosition = float2.zero;
            foreach (var (health, transform) in
                SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>()
                    .WithAll<HeroCombatStamina>())
            {
                if (health.ValueRO.Dead != 0) break;
                float3 position = transform.ValueRO.Position;
                heroPosition = new float2(position.x, position.z);
                hasHero = true;
                break;
            }

            foreach (var (source, transform, structure) in
                SystemAPI.Query<RefRW<ProductionSource>, RefRO<LocalTransform>,
                    RefRO<StructureInstance>>())
            {
                if (source.ValueRO.CycleSeconds <= 0f || source.ValueRO.GoldPerCycle <= 0) continue;
                float progress = source.ValueRO.ProgressSeconds + time.DeltaSeconds;
                int completed = (int)math.floor(progress / source.ValueRO.CycleSeconds);
                if (completed > 0)
                {
                    progress -= completed * source.ValueRO.CycleSeconds;
                    source.ValueRW.StoredGold = checked(source.ValueRO.StoredGold +
                        (long)completed * source.ValueRO.GoldPerCycle);
                    Debug.Log($"[Production] structure={structure.ValueRO.StructureId} " +
                        $"stored={source.ValueRO.StoredGold} cycles={completed}.");
                }
                source.ValueRW.ProgressSeconds = progress;
                if (!hasHero || source.ValueRO.StoredGold == 0) continue;
                float3 position = transform.ValueRO.Position;
                float2 pickup = new float2(position.x,
                    position.z + source.ValueRO.PickupOffsetZMeters);
                if (math.distancesq(heroPosition, pickup) >
                    source.ValueRO.PickupRadiusMeters * source.ValueRO.PickupRadiusMeters) continue;
                long amount = source.ValueRO.StoredGold;
                RunEconomySession.CreditProduction(ref economy, amount);
                source.ValueRW.StoredGold = 0;
                Debug.Log($"[Production] collected={amount} structure={structure.ValueRO.StructureId} " +
                    $"runGold={economy.Gold}.");
            }
            SystemAPI.SetSingleton(economy);
        }
    }
}
