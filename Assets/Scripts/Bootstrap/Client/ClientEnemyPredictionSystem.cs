#if !UNITY_SERVER || UNITY_EDITOR
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Movement;
using VaticanDefendor.Network;

namespace VaticanDefendor.Client
{
    // Reuse the shared AI/navigation for nearby ghosts. Distant ghosts use Netcode snapshots.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostPredictionSwitchingSystem))]
    public partial class ClientEnemyPredictionSystem : SystemBase
    {
        private struct Candidate : IComparable<Candidate>
        {
            public Entity Entity;
            public float Priority;
            public byte Predicted;
            public byte Eligible;
            public int CompareTo(Candidate other)
            {
                int order = Priority.CompareTo(other.Priority);
                return order != 0 ? order : Entity.Index.CompareTo(other.Entity.Index);
            }
        }

        private NativeList<Candidate> _candidates;
        private NetworkTick _snapshot;
        private double _snapshotAt;
        private double _nextScan;
        private double _nextReport;

        protected override void OnCreate()
        {
            _candidates = new NativeList<Candidate>(512, Allocator.Persistent);
            RequireForUpdate<NetworkMotionSettings>();
            RequireForUpdate<GhostPredictionSwitchingQueues>();
            RequireForUpdate<NetworkStreamInGame>();
        }

        protected override void OnDestroy() => _candidates.Dispose();

        protected override void OnUpdate()
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            float rtt = float.MaxValue;
            foreach (var ack in SystemAPI.Query<RefRO<NetworkSnapshotAck>>())
            {
                rtt = ack.ValueRO.EstimatedRTT;
                if (ack.ValueRO.LastReceivedSnapshotByLocal != _snapshot)
                {
                    _snapshot = ack.ValueRO.LastReceivedSnapshotByLocal;
                    _snapshotAt = now;
                }
                break;
            }
            if (now < _nextScan) return;
            _nextScan = now + 0.1d;
            NetworkMotionSettings settings = SystemAPI.GetSingleton<NetworkMotionSettings>();
            bool fresh = _snapshot.IsValid && (now - _snapshotAt) * 1000d <= settings.ExtrapolationMs;
            float2 hero = default;
            bool hasHero = false;
            foreach (var pose in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<HeroMoveInput, PredictedGhost>())
            {
                hero = pose.ValueRO.Position.xz;
                hasHero = true;
                break;
            }
            _candidates.Clear();
            foreach (var (pose, health, entity) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<CombatHealth>>()
                         .WithAll<CombatTargetTag, GhostInstance>().WithEntityAccess())
            {
                bool predicted = EntityManager.HasComponent<PredictedGhost>(entity);
                float distance = math.distancesq(pose.ValueRO.Position.xz, hero);
                float radius = predicted ? settings.EnemyExitRadiusMeters : settings.EnemyEnterRadiusMeters;
                float rttThreshold = settings.EnemyPredictionMaxRttMs + (predicted ? 25f : -25f);
                bool eligible = hasHero && fresh && rtt <= rttThreshold && health.ValueRO.Dead == 0 && distance <= radius * radius;
                _candidates.Add(new Candidate
                {
                    Entity = entity,
                    Priority = eligible ? distance - (predicted ? settings.EnemyExitRadiusMeters * settings.EnemyExitRadiusMeters -
                        settings.EnemyEnterRadiusMeters * settings.EnemyEnterRadiusMeters : 0f) : float.MaxValue,
                    Eligible = (byte)(eligible ? 1 : 0),
                    Predicted = (byte)(predicted ? 1 : 0)
                });
            }
            _candidates.Sort();
            GhostPredictionSwitchingQueues queues = SystemAPI.GetSingleton<GhostPredictionSwitchingQueues>();
            int selected = 0;
            foreach (Candidate candidate in _candidates)
            {
                bool predict = candidate.Eligible != 0 && selected < settings.MaximumPredictedEnemies;
                if (predict) selected++;
                if (predict == (candidate.Predicted != 0) ||
                    EntityManager.HasComponent<SwitchPredictionSmoothing>(candidate.Entity)) continue;
                // Existing actor presentation reads LocalTransform. Do not pretend LocalToWorld-only
                // transition smoothing affects that presentation; corrections use prediction smoothing.
                var entry = new ConvertPredictionEntry { TargetEntity = candidate.Entity, TransitionDurationSeconds = 0f };
                if (predict) queues.ConvertToPredictedQueue.Enqueue(entry);
                else queues.ConvertToInterpolatedQueue.Enqueue(entry);
            }
            if (UnityEngine.Debug.isDebugBuild && now >= _nextReport)
            {
                _nextReport = now + 5d;
                UnityEngine.Debug.Log($"[EnemyPrediction] selected={selected} total={_candidates.Length} limit={settings.MaximumPredictedEnemies} snapshotAgeMs={(now - _snapshotAt) * 1000d:F1}.");
            }
        }
    }
}
#endif
