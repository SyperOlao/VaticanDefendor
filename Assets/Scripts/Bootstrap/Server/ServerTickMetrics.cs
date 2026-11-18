#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Text;
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
        private readonly long[] _durations = new long[4096];
        private int _durationCount;
        private int _dropped;
        private bool _rawSamples;

        protected override void OnCreate()
        {
            _rawSamples = UnityEngine.Debug.isDebugBuild &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-vdMetricSamples") >= 0;
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
            if (_durationCount < _durations.Length) _durations[_durationCount++] = duration;
            else _dropped++;
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
                Array.Sort(_durations, 0, _durationCount);
                double p95 = _durationCount == 0 ? 0 : _durations[(int)Math.Ceiling(_durationCount * 0.95) - 1] * milliseconds;
                double p99 = _durationCount == 0 ? 0 : _durations[(int)Math.Ceiling(_durationCount * 0.99) - 1] * milliseconds;
                RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
                UnityEngine.Debug.Log($"[ServerTick] samples={state.Samples} " +
                    $"avgMs={state.SumTimestampTicks * milliseconds / state.Samples:F3} " +
                    $"p95Ms={p95:F3} p99Ms={p99:F3} droppedSamples={_dropped} " +
                    $"wall={UnityEngine.Time.realtimeSinceStartupAsDouble:F3} paused={activity.Paused} terminal={activity.Terminal} " +
                    $"maxMs={state.MaximumTimestampTicks * milliseconds:F3} livingEnemies={living} " +
                    $"sampleEnemy=({sampleX:F2},{sampleZ:F2}).");
                if (_rawSamples)
                {
                    var samples = new StringBuilder("[ServerTickSamples] durationUs=", _durationCount * 6 + 40);
                    for (int i = 0; i < _durationCount; i++)
                    {
                        if (i > 0) samples.Append(',');
                        samples.Append((long)Math.Round(_durations[i] * milliseconds * 1000));
                    }
                    UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", samples.ToString());
                }
                _durationCount = 0;
                _dropped = 0;
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
