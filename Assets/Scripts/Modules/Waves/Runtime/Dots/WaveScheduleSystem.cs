using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Session;

namespace VaticanDefendor.Waves
{
    public struct WaveScheduleConfig : IComponentData
    {
        public float InitialDelaySeconds;
        public int InitialEnemyCount;
        public int EnemiesAddedPerWave;
        public float SpawnIntervalSeconds;
        public int MaximumSpawnsPerTick;
        public float WaveDelaySeconds;
        public int MaximumSimultaneousEnemies;
        public int MaximumWaves;
        public float SpawnRadiusMeters;
    }

    public struct WaveState : IComponentData
    {
        public int WaveNumber;
        public int SpawnedInWave;
        public int TotalSpawned;
        public double NextEventSeconds;
        public byte Phase;
    }

    public struct WaveSpawnRequest : IBufferElementData
    {
        public int StableId;
        public float2 Position;
        public float SpeedVariationUnit;
        public int WaveNumber;
    }

    public static class WaveSession
    {
        public static Entity Create(EntityManager entities, WaveScheduleConfig config)
        {
            if (config.InitialDelaySeconds < 0f || config.InitialEnemyCount < 1 ||
                config.EnemiesAddedPerWave < 0 || config.SpawnIntervalSeconds <= 0f ||
                config.MaximumSpawnsPerTick < 1 || config.WaveDelaySeconds < 0f ||
                config.MaximumSimultaneousEnemies < 1 || config.MaximumWaves < 1 ||
                config.SpawnRadiusMeters <= 0f)
                throw new ArgumentException("Wave schedule is invalid.", nameof(config));
            Entity entity = entities.CreateEntity(typeof(WaveScheduleConfig), typeof(WaveState));
            entities.SetComponentData(entity, config);
            entities.SetComponentData(entity, new WaveState
            {
                WaveNumber = 1,
                NextEventSeconds = config.InitialDelaySeconds
            });
            entities.AddBuffer<WaveSpawnRequest>(entity);
            return entity;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class WaveScheduleSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<WaveScheduleConfig>();
            RequireForUpdate<HeroCombatStamina>();
            RequireForUpdate<RunSimulationTime>();
            RequireForUpdate<RunRandomState>();
        }

        protected override void OnUpdate()
        {
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            if (activity.Paused != 0 || activity.Terminal != 0) return;
            WaveScheduleConfig config = SystemAPI.GetSingleton<WaveScheduleConfig>();
            WaveState state = SystemAPI.GetSingleton<WaveState>();
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            RunRandomState random = SystemAPI.GetSingleton<RunRandomState>();
            DynamicBuffer<WaveSpawnRequest> pending =
                EntityManager.GetBuffer<WaveSpawnRequest>(SystemAPI.GetSingletonEntity<WaveState>());
            int living = 0;
            foreach (RefRO<CombatHealth> health in
                SystemAPI.Query<RefRO<CombatHealth>>().WithAll<EnemyAgentTag>())
                if (health.ValueRO.Dead == 0) living++;

            if (state.Phase == 0 && time.ElapsedSeconds >= state.NextEventSeconds)
            {
                state.Phase = 1;
                state.NextEventSeconds = time.ElapsedSeconds;
                Debug.Log($"[Waves] Started wave {state.WaveNumber}; target count " +
                    $"{config.InitialEnemyCount + (state.WaveNumber - 1) * config.EnemiesAddedPerWave}.");
            }

            if (state.Phase == 1)
            {
                int planned = config.InitialEnemyCount +
                    (state.WaveNumber - 1) * config.EnemiesAddedPerWave;
                int budget = config.MaximumSpawnsPerTick;
                while (state.SpawnedInWave < planned && budget-- > 0 &&
                    living + pending.Length < config.MaximumSimultaneousEnemies &&
                    time.ElapsedSeconds >= state.NextEventSeconds)
                {
                    float2 position;
                    if (state.WaveNumber == 1 && state.SpawnedInWave == 0)
                    {
                        position = new float2(0f, config.SpawnRadiusMeters);
                    }
                    else
                    {
                        float angle = RunSimulationSession.NextUnitFloat(ref random) *
                            math.PI * 2f;
                        position = new float2(math.sin(angle), math.cos(angle)) *
                            config.SpawnRadiusMeters;
                    }
                    pending.Add(new WaveSpawnRequest
                    {
                        StableId = state.TotalSpawned + 1,
                        Position = position,
                        SpeedVariationUnit = RunSimulationSession.NextUnitFloat(ref random) * 2f - 1f,
                        WaveNumber = state.WaveNumber
                    });
                    state.SpawnedInWave++;
                    state.TotalSpawned++;
                    state.NextEventSeconds += config.SpawnIntervalSeconds;
                }
                if (state.SpawnedInWave == planned) state.Phase = 2;
            }

            if (state.Phase == 2 && living == 0 && pending.Length == 0)
            {
                if (state.WaveNumber >= config.MaximumWaves)
                {
                    state.Phase = 4;
                    Debug.Log($"[Waves] Completed {state.WaveNumber} waves.");
                }
                else
                {
                    state.Phase = 3;
                    state.NextEventSeconds = time.ElapsedSeconds + config.WaveDelaySeconds;
                }
            }
            if (state.Phase == 3 && time.ElapsedSeconds >= state.NextEventSeconds)
            {
                state.WaveNumber++;
                state.SpawnedInWave = 0;
                state.Phase = 1;
                Debug.Log($"[Waves] Started wave {state.WaveNumber}; target count " +
                    $"{config.InitialEnemyCount + (state.WaveNumber - 1) * config.EnemiesAddedPerWave}.");
            }
            SystemAPI.SetSingleton(state);
            SystemAPI.SetSingleton(random);
        }
    }
}
