#if !UNITY_SERVER || UNITY_EDITOR
using System;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Movement;
using VaticanDefendor.Combat;
using VaticanDefendor.Network;

namespace VaticanDefendor.Client
{
    // Explicit diagnostic only: observes the package's existing smoothing without
    // replacing its action. Enable separately from clean CPU/GC runs: these reads
    // complete transform dependencies. No pose or gameplay component is written.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostPredictionSmoothingSystem))]
    public partial class ClientCorrectionProbeBeginSystem : SystemBase
    {
        private NativeParallelHashMap<Entity, float3> _positions;
        public int Overflow { get; private set; }

        protected override void OnCreate()
        {
            Enabled = UnityEngine.Debug.isDebugBuild &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-vdCorrectionProbe") >= 0;
            if (Enabled) _positions = new NativeParallelHashMap<Entity, float3>(64, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (_positions.IsCreated) _positions.Dispose();
        }

        public bool TryGetPosition(Entity entity, out float3 position) => _positions.TryGetValue(entity, out position);

        protected override void OnUpdate()
        {
            _positions.Clear();
            Overflow = 0;
            int count = 0;
            foreach (var (pose, entity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<GhostInstance, PredictedGhost>().WithAny<HeroMoveInput, CombatTargetTag>().WithEntityAccess())
            {
                if (count++ >= _positions.Capacity) { Overflow++; continue; }
                _positions.TryAdd(entity, pose.ValueRO.Position);
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GhostPredictionSmoothingSystem))]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    public partial class ClientCorrectionProbeEndSystem : SystemBase
    {
        private double _since;
        private int _heroCorrections;
        private int _enemyCorrections;
        private int _overflow;
        private float _maximum;

        protected override void OnCreate()
        {
            Enabled = UnityEngine.Debug.isDebugBuild &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-vdCorrectionProbe") >= 0;
        }

        protected override void OnUpdate()
        {
            var before = World.GetExistingSystemManaged<ClientCorrectionProbeBeginSystem>();
            if (before == null || !before.Enabled) return;
            _overflow += before.Overflow;
            foreach (var (pose, entity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<GhostInstance, PredictedGhost>().WithAny<HeroMoveInput, CombatTargetTag>().WithEntityAccess())
            {
                if (!before.TryGetPosition(entity, out float3 previous)) continue;
                float adjustment = math.distance(previous, pose.ValueRO.Position);
                if (adjustment <= 0.00001f) continue;
                if (EntityManager.HasComponent<HeroMoveInput>(entity)) _heroCorrections++;
                else _enemyCorrections++;
                _maximum = math.max(_maximum, adjustment);
            }
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_since == 0) _since = now;
            if (now - _since < 5) return;
            UnityEngine.Debug.LogFormat(UnityEngine.LogType.Log, UnityEngine.LogOption.NoStacktrace, null,
                "[CorrectionProbe] seconds={0:F3} heroCorrections={1} enemyCorrections={2} maxAdjustmentMeters={3:F6} overflow={4}",
                now - _since, _heroCorrections, _enemyCorrections, _maximum, _overflow);
            _since = now;
            _heroCorrections = 0;
            _enemyCorrections = 0;
            _overflow = 0;
            _maximum = 0;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    public partial class ClientMotionSmoothingSystem : SystemBase
    {
        private bool _registered;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkMotionSettings>();
            RequireForUpdate<GhostPredictionSmoothing>();
        }

        protected override void OnUpdate()
        {
            if (!_registered)
            {
                // Ghosts without explicit user parameters must retain the corrected state.
                DefaultTranslationSmoothingAction.DefaultStaticUserParams.maxDist.Data = 0f;
                _registered = SystemAPI.GetSingletonRW<GhostPredictionSmoothing>().ValueRW
                    .RegisterSmoothingAction<LocalTransform, DefaultSmoothingActionUserParams>(
                        EntityManager, DefaultTranslationSmoothingAction.Action);
            }
            NetworkMotionSettings settings = SystemAPI.GetSingleton<NetworkMotionSettings>();
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAny<HeroMoveInput, CombatTargetTag>()
                         .WithAll<GhostInstance, PredictedGhost>()
                         .WithNone<DefaultSmoothingActionUserParams>().WithEntityAccess())
            {
                commands.AddComponent(entity, new DefaultSmoothingActionUserParams
                {
                    maxDist = settings.CorrectionSnapMeters,
                    delta = settings.CorrectionStepMeters
                });
            }
            commands.Playback(EntityManager);
            // The server negotiates the actual simulation rate during the handshake.
            if (SystemAPI.TryGetSingleton(out ClientServerTickRate tickRate) &&
                SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientRate))
            {
                clientRate.ValueRW.MaxExtrapolationTimeSimTicks =
                    (uint)System.Math.Ceiling(settings.ExtrapolationMs * tickRate.SimulationTickRate / 1000d);
            }
        }
    }
}
#endif
