#if UNITY_SERVER || UNITY_EDITOR
using System.Diagnostics;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    public struct ServerTickMetricState : IComponentData
    {
        public long StartedTimestamp;
        public long SumTimestampTicks;
        public long MaximumTimestampTicks;
        public int Samples;
        public double NextReportSeconds;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(RunSimulationClockSystem))]
    public partial class ServerTickMetricsBeginSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<ServerTickMetricState>();

        protected override void OnUpdate()
        {
            ServerTickMetricState state = SystemAPI.GetSingleton<ServerTickMetricState>();
            state.StartedTimestamp = Stopwatch.GetTimestamp();
            SystemAPI.SetSingleton(state);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EnemyDeathCleanupSystem))]
    public partial class ServerTickMetricsEndSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ServerTickMetricState>();
            RequireForUpdate<RunSimulationTime>();
        }

        protected override void OnUpdate()
        {
            ServerTickMetricState state = SystemAPI.GetSingleton<ServerTickMetricState>();
            long duration = Stopwatch.GetTimestamp() - state.StartedTimestamp;
            if (duration < 0) return;
            state.SumTimestampTicks += duration;
            if (duration > state.MaximumTimestampTicks) state.MaximumTimestampTicks = duration;
            state.Samples++;
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            if (time.ElapsedSeconds >= state.NextReportSeconds && state.Samples > 0)
            {
                int living = 0;
                float sampleX = 0f;
                float sampleZ = 0f;
                foreach (var (health, transform) in
                    SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>()
                        .WithAll<EnemyAgentTag>())
                {
                    if (health.ValueRO.Dead != 0) continue;
                    if (living == 0)
                    {
                        sampleX = transform.ValueRO.Position.x;
                        sampleZ = transform.ValueRO.Position.z;
                    }
                    living++;
                }
                double milliseconds = 1000d / Stopwatch.Frequency;
                UnityEngine.Debug.Log($"[ServerTick] samples={state.Samples} " +
                    $"avgMs={state.SumTimestampTicks * milliseconds / state.Samples:F3} " +
                    $"maxMs={state.MaximumTimestampTicks * milliseconds:F3} livingEnemies={living} " +
                    $"sampleEnemy=({sampleX:F2},{sampleZ:F2}).");
                state.SumTimestampTicks = 0;
                state.MaximumTimestampTicks = 0;
                state.Samples = 0;
                state.NextReportSeconds = time.ElapsedSeconds + 5d;
            }
            SystemAPI.SetSingleton(state);
        }
    }
}
#endif
